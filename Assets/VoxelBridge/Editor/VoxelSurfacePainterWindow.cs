using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge
{
    internal sealed class VoxelSurfacePainterWindow : EditorWindow
    {
        [SerializeField] private Object source;
        [SerializeField] private string sourceGuid;
        [SerializeField] private string prefabGuid;
        [SerializeField] private int level;
        [SerializeField] private int surfaceId;
        [SerializeField] private Vector2 orbit = new(20, -30);
        [SerializeField] private float distance = 1;
        [SerializeField] private string draftFingerprint;
        [SerializeField] private int[] draftCells = Array.Empty<int>();
        [SerializeField] private int[] draftSurfaces = Array.Empty<int>();

        private VoxelSurfaceEdit edit;
        private readonly HashSet<int> selected = new();
        private PreviewRenderUtility preview;
        private Mesh mesh;
        private Material material;
        private float sourceEmissionIntensity = 1f;
        private bool hasSourceEmissionMaterial;
        private string status;
        private MessageType statusType = MessageType.Info;
        private Vector3 center;
        private float radius;
        private int hover = -1;
        private bool selecting;
        private string SourcePath => AssetDatabase.GUIDToAssetPath(sourceGuid);

        [MenuItem("Tools/Voxel Bridge/Surface Painter")]
        private static void OpenWindow()
        {
            var window = GetWindow<VoxelSurfacePainterWindow>("Surface Painter");
            window.minSize = new Vector2(680, 560);
            window.Show();
        }

        internal static void OpenForSource(string path, string prefabPath = null, int lod = 0)
        {
            var window = GetWindow<VoxelSurfacePainterWindow>("Surface Painter");
            if (window.SourcePath != path && !window.ConfirmLeave()) return;
            window.prefabGuid = string.IsNullOrEmpty(prefabPath) ? null : AssetDatabase.AssetPathToGUID(prefabPath);
            window.level = lod;
            if (window.SourcePath != path)
            {
                window.ResetDraft();
                window.sourceGuid = AssetDatabase.AssetPathToGUID(path);
                window.source = AssetDatabase.LoadMainAssetAtPath(path);
                window.LoadSource(false);
            }
            window.minSize = new Vector2(680, 560);
            window.Show();
        }

        private void OnEnable()
        {
            wantsMouseMove = true;
            EditorApplication.playModeStateChanged += OnPlayMode;
            saveChangesMessage = "Hay superficies sin guardar. Guardar modifica el .vox y su sidecar; no regenera los LODs.";
            if (!string.IsNullOrEmpty(sourceGuid)) EditorApplication.update += RestoreAfterReload;
        }

        private void RestoreAfterReload()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            EditorApplication.update -= RestoreAfterReload;
            if (this != null) LoadSource(true);
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayMode;
            EditorApplication.update -= RestoreAfterReload;
            ReleasePreview();
        }

        private void OnPlayMode(PlayModeStateChange state)
        {
            selecting = false;
            if (state == PlayModeStateChange.ExitingEditMode) ReleasePreview();
            if (state == PlayModeStateChange.EnteredEditMode && edit != null) Run(BuildPreview);
            Repaint();
        }

        private void LoadSource(bool restoreDraft)
        {
            ReleasePreview(); edit = null; selected.Clear(); status = null; statusType = MessageType.Info;
            if (string.IsNullOrEmpty(SourcePath)) { status = "The source asset is missing."; statusType = MessageType.Error; return; }
            try
            {
                edit = new VoxelSurfaceEdit(SourcePath);
                level = edit.LodIndex;
                if (restoreDraft && draftCells.Length > 0)
                {
                    if (edit.Fingerprint != draftFingerprint || draftCells.Length != draftSurfaces.Length)
                        throw new InvalidOperationException("The source or palettes changed. The draft was kept; restore the original files or discard the draft explicitly.");
                    foreach (var group in Enumerable.Range(0, draftCells.Length).GroupBy(i => draftSurfaces[i]))
                    {
                        int[] cells = group.Select(i => draftCells[i]).ToArray();
                        for (int offset = 0; offset < cells.Length; offset += VoxelSurfaceEdit.MaximumSelection)
                            edit.Apply(cells.Skip(offset).Take(VoxelSurfaceEdit.MaximumSelection).ToArray(), group.Key);
                    }
                    edit.ClearHistory();
                    status = "Borrador restaurado. El historial Undo se reinicia después de recargar scripts.";
                }
                center = edit.Grid.Origin + (Vector3)edit.Grid.Size * edit.Grid.VoxelSize * .5f;
                radius = Mathf.Max(edit.Grid.VoxelSize, ((Vector3)edit.Grid.Size * edit.Grid.VoxelSize).magnitude * .5f);
                if (!restoreDraft) distance = radius * 4;
                if (!EditorApplication.isPlayingOrWillChangePlaymode) BuildPreview();
            }
            catch (Exception exception) { edit = null; status = exception.Message; statusType = MessageType.Error; }
            hasUnsavedChanges = draftCells.Length > 0;
            Repaint();
        }

        private void BuildPreview()
        {
            if (edit == null) return;
            var generated = VoxelSemanticMesher.Build(edit.Grid, edit.HideInternalCavities, VoxelProductionEditor.Progress);
            try
            {
                VoxelProductionExporter.ValidatePalettes(edit.Colors, edit.Surfaces);
                if (material == null)
                {
                    var shader = AssetDatabase.LoadAssetAtPath<Shader>(VoxelProductionExporter.ShaderPath);
                    if (shader == null || !shader.isSupported || ShaderUtil.ShaderHasError(shader))
                        throw new InvalidOperationException("The production shader is not available for preview.");
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(prefabGuid ?? ""));
                    Material shared = prefab == null ? null : prefab.GetComponentsInChildren<Renderer>(true)
                        .SelectMany(renderer => renderer.sharedMaterials).FirstOrDefault(value => value != null && value.shader == shader &&
                            value.GetTexture("_PaletteColor") == edit.Colors.GeneratedLut && value.GetTexture("_PaletteSurface") == edit.Surfaces.GeneratedLut);
                    material = shared != null ? new Material(shared) : new Material(shader);
                    material.hideFlags = HideFlags.HideAndDontSave;
                    material.SetTexture("_PaletteColor", edit.Colors.GeneratedLut);
                    material.SetTexture("_PaletteSurface", edit.Surfaces.GeneratedLut);
                    hasSourceEmissionMaterial = shared != null;
                    sourceEmissionIntensity = hasSourceEmissionMaterial ? shared.GetFloat("_EmissionIntensity") : 1f;
                    if (!float.IsFinite(sourceEmissionIntensity) || sourceEmissionIntensity < 0)
                        throw new InvalidOperationException("The source material has an invalid emission intensity.");
                    // Preview cameras have no HDRP exposure/tonemapping. Preserve hue using
                    // a bounded authoring reference on this temporary material only.
                    material.SetFloat("_EmissionIntensity", Mathf.Clamp01(sourceEmissionIntensity));
                }
                if (mesh != null) DestroyImmediate(mesh);
                mesh = generated; generated = null;
                if (preview == null)
                {
                    preview = new PreviewRenderUtility();
                    preview.camera.fieldOfView = 35;
                    preview.camera.clearFlags = CameraClearFlags.SolidColor;
                    preview.camera.backgroundColor = new Color(.16f, .17f, .19f);
                    ConfigurePreviewLight(preview.lights[0], 4f, new Vector3(40, 35, 0));
                    ConfigurePreviewLight(preview.lights[1], 2f, new Vector3(340, 218, 0));
                    preview.ambientColor = new Color(.4f, .4f, .4f);
                }
            }
            finally { if (generated != null) DestroyImmediate(generated); EditorUtility.ClearProgressBar(); }
        }

        private void ReleasePreview()
        {
            preview?.Cleanup(); preview = null;
            if (mesh != null) DestroyImmediate(mesh);
            if (material != null) DestroyImmediate(material);
            mesh = null; material = null;
        }

        private static void ConfigurePreviewLight(Light light, float lux, Vector3 rotation)
        {
            // Register HDRP data before rendering. Lazy initialization otherwise resets
            // directional preview lights to 100,000 lux after the first cull.
            if (light.GetComponent<HDAdditionalLightData>() == null)
                light.gameObject.AddComponent<HDAdditionalLightData>();
            light.lightUnit = LightUnit.Lux;
            light.intensity = lux;
            light.color = Color.white;
            light.useColorTemperature = false;
            light.shadows = LightShadows.None;
            light.transform.rotation = Quaternion.Euler(rotation);
        }

        private void OnGUI()
        {
            Object candidate = EditorGUILayout.ObjectField("Source VOX", source, typeof(Object), false);
            if (candidate != source)
            {
                string path = AssetDatabase.GetAssetPath(candidate);
                if (!path.EndsWith(".vox", StringComparison.OrdinalIgnoreCase)) status = "Choose a VOX source with semantic bindings.";
                else if (ConfirmLeave()) { ResetDraft(); source = candidate; sourceGuid = AssetDatabase.AssetPathToGUID(path); prefabGuid = null; level = 0; LoadSource(false); }
            }
            EditorGUILayout.HelpBox("Seleccionar voxels con clic o arrastre; Shift quita celdas. Alt + arrastre o botón derecho rota; rueda acerca/aleja. Se modifica el voxel completo, sin cambiar su color. Keep Original queda fuera de esta vista.", MessageType.Info);
            EditorGUILayout.HelpBox("Los slots con RGB idéntico y superficies distintas requieren una prueba de intercambio con MagicaVoxel. No se debe asumir que un guardado externo conserva su identidad.", MessageType.Warning);
            if (!string.IsNullOrEmpty(SourcePath) && VoxelSurfaceEditStore.HasPending(SourcePath))
                if (GUILayout.Button("Recover Interrupted Save")) Run(() => { VoxelSurfaceEditStore.Recover(SourcePath); LoadSource(true); });
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reload Source") && ConfirmLeave()) { ResetDraft(); LoadSource(false); }
                if (GUILayout.Button("Discard Changes") && EditorUtility.DisplayDialog("Discard Surface Changes", "Se descartará el borrador local. Los archivos guardados no cambian.", "Discard", "Cancel"))
                { ResetDraft(); LoadSource(false); }
            }
            if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, statusType);
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            { EditorGUILayout.HelpBox("La edición está deshabilitada en Play Mode. Los cambios pendientes se conservan.", MessageType.Info); return; }
            if (edit == null) return;
            EditorGUILayout.LabelField(new GUIContent("Emission Preview",
                "La vista usa un multiplicador de emisión entre 0 y 1 para evitar saturación a blanco. No reproduce la exposición ni el bloom de la escena y no modifica su material."),
                hasSourceEmissionMaterial ? $"Reference ×{Mathf.Clamp01(sourceEmissionIntensity):0.###} · Source HDR ×{sourceEmissionIntensity:0.###}" :
                    "Reference ×1 · No linked material");
            if (!edit.Surfaces.TryValidate(out string surfaceError))
            { EditorGUILayout.HelpBox(surfaceError, MessageType.Error); return; }
            var options = edit.Surfaces.Entries.Where(s => s.RenderClass == VoxelSurfaceRenderClass.Opaque).OrderBy(s => s.Id).ToArray();
            int choice = Array.FindIndex(options, s => s.Id == surfaceId);
            choice = EditorGUILayout.Popup("Surface", Mathf.Max(0, choice), options.Select(s => $"{s.Id:000} · {s.DisplayName}").ToArray());
            if (choice >= 0 && choice < options.Length)
            {
                var surface = options[choice]; surfaceId = surface.Id;
                EditorGUILayout.LabelField($"Metallic {surface.Metallic:0.##}   Smoothness {surface.Smoothness:0.##}   Emission {surface.Emission:0.##}");
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(selected.Count == 0))
                    if (GUILayout.Button("Apply Surface")) Run(() => Change(() => edit.Apply(selected, surfaceId)));
                if (GUILayout.Button("Clear Selection")) { selected.Clear(); Repaint(); }
                using (new EditorGUI.DisabledScope(!edit.CanUndo)) if (GUILayout.Button("Undo")) Run(() => Change(edit.Undo));
                using (new EditorGUI.DisabledScope(!edit.CanRedo)) if (GUILayout.Button("Redo")) Run(() => Change(edit.Redo));
                if (GUILayout.Button("Frame")) { distance = radius * 4; Repaint(); }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(edit.PendingCells == 0)) if (GUILayout.Button("Save Source")) Run(SaveSource);
                using (new EditorGUI.DisabledScope(edit.PendingCells != 0 || string.IsNullOrEmpty(prefabGuid)))
                    if (GUILayout.Button("Rebuild Edited LOD")) Run(Rebuild);
            }
            EditorGUILayout.LabelField($"Selected {selected.Count:N0}   Pending {edit.PendingCells:N0}   LOD {level}");
            Rect viewport = GUILayoutUtility.GetRect(100, 100, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            DrawPreview(viewport);
        }

        private void DrawPreview(Rect rect)
        {
            if (preview == null || mesh == null || material == null || rect.height <= 0) return;
            Camera camera = preview.camera;
            Quaternion rotation = Quaternion.Euler(orbit.x, orbit.y, 0);
            camera.transform.SetPositionAndRotation(center - rotation * Vector3.forward * distance, rotation);
            camera.nearClipPlane = Mathf.Max(.001f, radius * .001f); camera.farClipPlane = distance + radius * 4;
            camera.aspect = rect.width / rect.height;
            var e = Event.current;
            if (e.type == EventType.Repaint)
            {
                preview.BeginPreview(rect, GUIStyle.none);
                Texture texture;
                bool failed = false;
                try
                {
                    preview.DrawMesh(mesh, Matrix4x4.identity, material, 0);
                    preview.Render(true, false);
                }
                catch (Exception exception)
                { status = "Preview rendering failed: " + exception.Message; statusType = MessageType.Error; failed = true; }
                finally { texture = preview.EndPreview(); }
                if (failed) { ReleasePreview(); Repaint(); return; }
                GUI.DrawTexture(rect, texture, ScaleMode.StretchToFill, false);
                Handles.BeginGUI();
                Color previous = Handles.color; Handles.color = Color.yellow;
                // A bounded overlay keeps large selections from stalling the editor.
                GUI.BeginClip(rect);
                var localRect = new Rect(0, 0, rect.width, rect.height);
                foreach (int index in selected.Take(512)) DrawCell(index, localRect, camera);
                if (hover >= 0) { Handles.color = Color.cyan; DrawCell(hover, localRect, camera); }
                GUI.EndClip();
                Handles.color = previous; Handles.EndGUI();
            }
            if (!rect.Contains(e.mousePosition)) { if (e.type == EventType.MouseUp) selecting = false; return; }
            if (e.type == EventType.ScrollWheel)
            { distance = Mathf.Clamp(distance * Mathf.Exp(e.delta.y * .06f), radius * 1.05f, radius * 30); e.Use(); Repaint(); }
            if (e.type == EventType.MouseDrag && (e.alt || e.button == 1))
            { orbit += new Vector2(-e.delta.y, -e.delta.x) * .5f; e.Use(); Repaint(); }
            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt) selecting = true;
            if ((e.type == EventType.MouseMove || e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && !e.alt && e.button != 1)
            {
                Vector2 uv = new((e.mousePosition.x - rect.x) / rect.width, 1 - (e.mousePosition.y - rect.y) / rect.height);
                hover = VoxelSurfaceEdit.Pick(edit.Grid, camera.ViewportPointToRay(uv), out int index) ? index : -1;
                if (selecting && hover >= 0)
                {
                    if (e.shift) selected.Remove(hover);
                    else if (selected.Count < VoxelSurfaceEdit.MaximumSelection) selected.Add(hover);
                    else status = "Selection limit reached. Apply or clear this selection first.";
                    e.Use();
                }
                Repaint();
            }
            if (e.type == EventType.MouseUp) { selecting = false; e.Use(); }
        }

        private void DrawCell(int index, Rect rect, Camera camera)
        {
            edit.Grid.Coordinates(index, out int x, out int y, out int z);
            Vector3 min = edit.Grid.Origin + new Vector3(x, y, z) * edit.Grid.VoxelSize;
            var points = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                Vector3 p = camera.WorldToViewportPoint(min + new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1) * edit.Grid.VoxelSize);
                if (p.z <= 0) return;
                points[i] = new Vector3(rect.x + p.x * rect.width, rect.y + (1 - p.y) * rect.height, 0);
            }
            for (int i = 0; i < 8; i++) for (int axis = 0; axis < 3; axis++)
                if ((i & (1 << axis)) == 0) Handles.DrawLine(points[i], points[i | (1 << axis)]);
        }

        private void Change(Action operation)
        {
            operation();
            edit.GetChanges(out draftCells, out draftSurfaces);
            draftFingerprint = edit.Fingerprint; hasUnsavedChanges = draftCells.Length > 0;
            Run(BuildPreview); Repaint();
        }

        private void SaveSource()
        {
            if (edit == null) throw new InvalidOperationException("Load a valid source before saving.");
            string warning = VoxelSurfaceEditStore.Save(SourcePath, edit);
            ResetDraft(); LoadSource(true);
            status = warning ?? "Fuente guardada. Rebuild actualiza el prefab; los LODs descendientes no se regeneran automáticamente.";
            statusType = warning == null ? MessageType.Info : MessageType.Warning;
        }

        private void Rebuild()
        {
            string path = AssetDatabase.GUIDToAssetPath(prefabGuid);
            var link = VoxelProductionLink.Load(path);
            if (!string.IsNullOrEmpty(link.manifestGuid))
            {
                string manifestPath = AssetDatabase.GUIDToAssetPath(link.manifestGuid);
                var manifest = VoxelProductionFamily.Load(manifestPath);
                if (level < 0 || level >= manifest.lods.Length || VoxelProductionFamily.SourcePath(manifest.lods[level]) != SourcePath)
                    throw new InvalidOperationException("The production source link changed. Reopen the editor from the intended LOD.");
                VoxelProductionFamily.RebuildLevel(manifestPath, level, VoxelProductionEditor.Progress);
            }
            else
            {
                if (link.SourcePath != SourcePath) throw new InvalidOperationException("The production source link changed. Reopen the surface editor.");
                VoxelProductionExporter.Rebuild(path, VoxelProductionEditor.Progress);
            }
        }

        public override void SaveChanges() { SaveSource(); base.SaveChanges(); }
        public override void DiscardChanges() { ResetDraft(); base.DiscardChanges(); }
        private void ResetDraft() { draftCells = Array.Empty<int>(); draftSurfaces = Array.Empty<int>(); draftFingerprint = null; hasUnsavedChanges = false; }
        private bool ConfirmLeave()
        {
            if (!hasUnsavedChanges) return true;
            int decision = EditorUtility.DisplayDialogComplex("Unsaved Surface Changes", saveChangesMessage, "Save", "Cancel", "Discard");
            if (decision == 1) return false;
            if (decision == 2) { ResetDraft(); return true; }
            try { SaveSource(); return true; } catch (Exception exception) { status = exception.Message; return false; }
        }
        private void Run(Action action)
        {
            try { status = null; statusType = MessageType.Info; action(); }
            catch (OperationCanceledException) { status = "Operation cancelled. Pending edits were kept."; }
            catch (Exception exception) { status = exception.Message; statusType = MessageType.Error; }
            finally { EditorUtility.ClearProgressBar(); Repaint(); }
        }
    }
}
