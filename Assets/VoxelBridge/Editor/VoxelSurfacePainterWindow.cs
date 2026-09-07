using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.ShortcutManagement;
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
        [SerializeField] private Vector3 panOffset;
        [SerializeField] private VoxelSelectionTool selectionTool;
        [SerializeField] private VoxelSelectionMode selectionMode = VoxelSelectionMode.Add;
        [SerializeField] private int brushDiameter = 16;
        [SerializeField] private bool showHelp;
        [SerializeField] private bool showSelectionTint = true;
        [SerializeField] private string draftFingerprint;
        [SerializeField] private int[] draftCells = Array.Empty<int>();
        [SerializeField] private int[] draftSurfaces = Array.Empty<int>();

        private VoxelSurfaceEdit edit;
        private readonly HashSet<int> selected = new();
        private PreviewRenderUtility preview;
        private Mesh mesh;
        private Material material;
        private string status;
        private MessageType statusType = MessageType.Info;
        private Vector3 center;
        private float radius;
        private int hover = -1;
        private bool selecting;
        private VoxelSurfaceSelection selectionJob;
        private Rect selectionViewport;
        private Vector2 gestureStart, gestureEnd;
        private int selectionControl;
        private double nextSelectionRepaint;
        private readonly List<Mesh> selectionMeshes = new();
        private readonly List<Mesh> hoverMeshes = new();
        private Material selectionMaterial, hoverMaterial;
        private IReadOnlyCollection<int> overlaySource;
        private int overlayCount = -1, overlayHover = -1;
        private GUIStyle glyphStyle;
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
            CancelSelection();
            EditorApplication.playModeStateChanged -= OnPlayMode;
            EditorApplication.update -= RestoreAfterReload;
            ReleasePreview();
        }

        private void OnPlayMode(PlayModeStateChange state)
        {
            CancelSelection();
            if (state == PlayModeStateChange.ExitingEditMode) ReleasePreview();
            if (state == PlayModeStateChange.EnteredEditMode && edit != null) Run(BuildPreview);
            Repaint();
        }

        private void LoadSource(bool restoreDraft)
        {
            CancelSelection();
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
                if (!restoreDraft) { distance = radius * 4; panOffset = Vector3.zero; }
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
                    float sourceEmissionIntensity = shared != null ? shared.GetFloat("_EmissionIntensity") : 1f;
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
            VoxelSelectionOverlay.Destroy(selectionMeshes); VoxelSelectionOverlay.Destroy(hoverMeshes);
            if (selectionMaterial != null) DestroyImmediate(selectionMaterial);
            if (hoverMaterial != null) DestroyImmediate(hoverMaterial);
            selectionMaterial = null; hoverMaterial = null;
            overlaySource = null; overlayCount = overlayHover = -1;
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

        private bool DrawControls()
        {
            Object candidate;
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Source", GUILayout.Width(44));
                candidate = EditorGUILayout.ObjectField(source, typeof(Object), false);
                if (GUILayout.Button(new GUIContent("Source Actions", "Recargar, descartar, guardar sólo la fuente o reconstruir el LOD vinculado."), EditorStyles.toolbarDropDown, GUILayout.Width(110))) ShowSourceActions();
                showHelp = GUILayout.Toggle(showHelp, new GUIContent("?", "Controls & external editing\nControles, límites de la vista y precauciones de intercambio con MagicaVoxel."), EditorStyles.toolbarButton, GUILayout.Width(25));
            }
            if (candidate != source)
            {
                string path = AssetDatabase.GetAssetPath(candidate);
                if (!path.EndsWith(".vox", StringComparison.OrdinalIgnoreCase)) status = "Choose a VOX source with semantic bindings.";
                else if (ConfirmLeave()) { ResetDraft(); source = candidate; sourceGuid = AssetDatabase.AssetPathToGUID(path); prefabGuid = null; level = 0; LoadSource(false); }
            }
            if (showHelp)
            {
                EditorGUILayout.HelpBox("Pincel o rectángulo selecciona voxels visibles; Shift al iniciar resta. Esc cancela. Alt + arrastre o botón derecho rota; botón central desplaza; rueda acerca/aleja. Apply Surface modifica el voxel completo. Keep Original queda fuera de esta vista. La emisión de referencia no reproduce bloom ni exposición.", MessageType.Info);
                EditorGUILayout.HelpBox("Intercambio externo: los slots con RGB idéntico y superficies distintas requieren una prueba en MagicaVoxel. No se debe asumir que un guardado externo conserva su identidad. Las validaciones y el bloqueo ante conflictos permanecen activos.", MessageType.Warning);
            }
            if (!string.IsNullOrEmpty(SourcePath) && VoxelSurfaceEditStore.HasPending(SourcePath))
                if (GUILayout.Button("Recover Interrupted Save")) Run(() => { VoxelSurfaceEditStore.Recover(SourcePath); LoadSource(true); });
            if (!string.IsNullOrEmpty(status) && (statusType != MessageType.Info || showHelp)) EditorGUILayout.HelpBox(status, statusType);
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            { EditorGUILayout.HelpBox("La edición está deshabilitada en Play Mode. Los cambios pendientes se conservan.", MessageType.Info); return false; }
            if (edit == null) return false;
            if (!edit.Surfaces.TryValidate(out string surfaceError))
            { EditorGUILayout.HelpBox(surfaceError, MessageType.Error); return false; }
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                selectionTool = (VoxelSelectionTool)GUILayout.Toolbar((int)selectionTool, new[] { "Brush", "Rectangle" }, EditorStyles.toolbarButton, GUILayout.Width(160));
                GUILayout.Space(8);
                selectionMode = (VoxelSelectionMode)GUILayout.Toolbar((int)selectionMode, new[] { "Replace", "Add", "Subtract" }, EditorStyles.toolbarButton, GUILayout.Width(205));
                if (selectionTool == VoxelSelectionTool.Brush)
                {
                    GUILayout.Space(8);
                    GUILayout.Label(new GUIContent("Size", "Diámetro en píxeles de interfaz. Acercar la cámara para seleccionar detalles subpíxel."), GUILayout.Width(28));
                    brushDiameter = Mathf.RoundToInt(GUILayout.HorizontalSlider(brushDiameter, 1, 128, GUILayout.Width(90)));
                    brushDiameter = Mathf.Clamp(EditorGUILayout.IntField(brushDiameter, GUILayout.Width(38)), 1, 128);
                }
                GUILayout.FlexibleSpace();
            }
            var options = edit.Surfaces.Entries.Where(s => s.RenderClass == VoxelSurfaceRenderClass.Opaque).OrderBy(s => s.Id).ToArray();
            int choice = Array.FindIndex(options, s => s.Id == surfaceId);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Surface", GUILayout.Width(48));
                choice = EditorGUILayout.Popup(Mathf.Max(0, choice), options.Select(s => new GUIContent($"{s.Id:000} · {s.DisplayName}")).ToArray(), GUILayout.MinWidth(170), GUILayout.MaxWidth(340));
                if (choice >= 0 && choice < options.Length)
                {
                    var surface = options[choice]; surfaceId = surface.Id;
                    GUILayout.Label(new GUIContent($"M {surface.Metallic:0.##}   S {surface.Smoothness:0.##}   E {surface.Emission:0.##}",
                        "Metallic / Smoothness / Emission\nValores de referencia de la superficie, sin bloom ni exposición de escena."), EditorStyles.miniLabel, GUILayout.Width(145));
                }
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(selected.Count == 0))
                    if (GUILayout.Button("Apply Surface", GUILayout.Width(108))) Run(() => Change(() => edit.Apply(selected, surfaceId)));
            }
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                using (new EditorGUI.DisabledScope(!edit.CanUndo)) if (GlyphButton("↶", "Undo — Ctrl+Z\nDeshace asignaciones pendientes; no revierte archivos guardados.")) Run(() => Change(edit.Undo));
                using (new EditorGUI.DisabledScope(!edit.CanRedo)) if (GlyphButton("↷", "Redo — Ctrl+Y / Ctrl+Shift+Z")) Run(() => Change(edit.Redo));
                using (new EditorGUI.DisabledScope(selected.Count == 0))
                    if (GlyphButton("×", "Clear Selection\nQuita la selección sin modificar las asignaciones.")) { selected.Clear(); Repaint(); }
                GUILayout.Space(8);
                if (GUILayout.Button("Frame All", EditorStyles.toolbarButton, GUILayout.Width(75))) { distance = radius * 4; panOffset = Vector3.zero; Repaint(); }
                using (new EditorGUI.DisabledScope(selected.Count == 0))
                    if (GUILayout.Button("Frame Selection", EditorStyles.toolbarButton, GUILayout.Width(108))) Run(FrameSelection);
                bool tint = GUILayout.Toggle(showSelectionTint, new GUIContent("Tint", "Tinte suave sobre la selección. Desactivar para mostrar sólo el perímetro; no modifica materiales ni asignaciones."), EditorStyles.toolbarButton, GUILayout.Width(42));
                if (tint != showSelectionTint) { showSelectionTint = tint; overlayCount = -1; Repaint(); }
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(prefabGuid)))
                    if (GUILayout.Button(new GUIContent("Save & Rebuild", "Guarda la fuente y reconstruye este LOD del prefab. No regenera los LODs descendientes."), EditorStyles.toolbarButton, GUILayout.Width(125))) Run(SaveAndRebuild);
            }
            return true;
        }

        private bool GlyphButton(string symbol, string tooltip)
        {
            glyphStyle ??= new GUIStyle(EditorStyles.toolbarButton) { fontSize = 16 };
            return GUILayout.Button(new GUIContent(symbol, tooltip), glyphStyle, GUILayout.Width(28));
        }

        private void ShowSourceActions()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Reload Source"), false, () => { if (ConfirmLeave()) { ResetDraft(); LoadSource(false); } });
            if (edit != null && edit.PendingCells > 0) menu.AddItem(new GUIContent("Save Source Only"), false, () => Run(SaveSource));
            else menu.AddDisabledItem(new GUIContent("Save Source Only"));
            if (edit != null && edit.PendingCells == 0 && !string.IsNullOrEmpty(prefabGuid)) menu.AddItem(new GUIContent("Rebuild Edited LOD"), false, () => Run(Rebuild));
            else menu.AddDisabledItem(new GUIContent("Rebuild Edited LOD"));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Discard Changes..."), false, () =>
            {
                if (EditorUtility.DisplayDialog("Discard Surface Changes", "Se descartará el borrador local. Los archivos guardados no cambian.", "Discard", "Cancel"))
                { ResetDraft(); LoadSource(false); }
            });
            menu.AddItem(new GUIContent("External Editing Notice"), showHelp, () => { showHelp = true; Repaint(); });
            menu.ShowAsContext();
        }

        private void OnGUI()
        {
            using (new EditorGUI.DisabledScope(selectionJob != null))
                if (!DrawControls()) return;
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUILayout.LabelField(new GUIContent(selectionJob == null ? $"Selected {selected.Count:N0}   Pending {edit.PendingCells:N0}   LOD {level}" :
                    $"Selecting {selectionJob.Result.Count:N0}   Samples {selectionJob.Samples:N0}   Esc to cancel", status), EditorStyles.miniLabel);
                // Keep the control present while dragging so IMGUI control IDs stay stable.
                using (new EditorGUI.DisabledScope(selectionJob == null))
                    if (GUILayout.Button(new GUIContent("Cancel", "Cancel Selection — Esc\nConserva la selección anterior al gesto."), EditorStyles.toolbarButton, GUILayout.Width(60))) CancelSelection();
            }
            Rect viewport = GUILayoutUtility.GetRect(100, 100, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            DrawPreview(viewport);
        }

        private void DrawPreview(Rect rect)
        {
            if (preview == null || mesh == null || material == null || rect.height <= 0) return;
            int control = GUIUtility.GetControlID("VoxelSurfaceSelection".GetHashCode(), FocusType.Passive, rect);
            // GUILayout returns a placeholder rectangle during Layout, not the viewport.
            if (Event.current.type == EventType.Layout) return;
            if (selectionJob != null && rect != selectionViewport) CancelSelection();
            Camera camera = preview.camera;
            Quaternion rotation = Quaternion.Euler(orbit.x, orbit.y, 0);
            camera.transform.SetPositionAndRotation(center + panOffset - rotation * Vector3.forward * distance, rotation);
            camera.nearClipPlane = Mathf.Max(.0001f, Mathf.Min(edit.Grid.VoxelSize * .05f, distance * .01f));
            camera.farClipPlane = distance + (radius + panOffset.magnitude) * 4;
            camera.aspect = rect.width / rect.height;
            var e = Event.current;
            if (e.type == EventType.Repaint)
            {
                // BeginClip also changes Event.mousePosition's coordinate space. Snapshot
                // both position and containment before entering the local viewport.
                Vector2 localPointer = e.mousePosition - rect.position;
                bool drawBrushCursor = selectionTool == VoxelSelectionTool.Brush && rect.Contains(e.mousePosition);
                preview.BeginPreview(rect, GUIStyle.none);
                Texture texture;
                bool failed = false;
                try
                {
                    UpdateSelectionOverlay();
                    preview.DrawMesh(mesh, Matrix4x4.identity, material, 0);
                    foreach (var faces in selectionMeshes) preview.DrawMesh(faces, Matrix4x4.identity, selectionMaterial, 0);
                    foreach (var faces in hoverMeshes) preview.DrawMesh(faces, Matrix4x4.identity, hoverMaterial, 0);
                    preview.Render(true, false);
                }
                catch (Exception exception)
                { status = "Preview rendering failed: " + exception.Message; statusType = MessageType.Error; failed = true; }
                finally { texture = preview.EndPreview(); }
                if (failed) { CancelSelection(); ReleasePreview(); Repaint(); return; }
                GUI.DrawTexture(rect, texture, ScaleMode.StretchToFill, false);
                Handles.BeginGUI();
                Color previous = Handles.color;
                GUI.BeginClip(rect);
                Handles.color = Color.cyan;
                if (selecting && selectionTool == VoxelSelectionTool.Rectangle)
                {
                    Rect area = VoxelSurfaceSelection.Area(gestureStart, gestureEnd);
                    EditorGUI.DrawRect(area, new Color(0, 1, 1, .08f));
                    Handles.DrawAAPolyLine(2, new Vector3(area.xMin, area.yMin), new Vector3(area.xMax, area.yMin),
                        new Vector3(area.xMax, area.yMax), new Vector3(area.xMin, area.yMax), new Vector3(area.xMin, area.yMin));
                }
                else if (drawBrushCursor)
                    Handles.DrawWireDisc((Vector3)localPointer, Vector3.forward, brushDiameter * .5f);
                GUI.EndClip();
                Handles.color = previous; Handles.EndGUI();
            }
            if (selectionJob != null)
            {
                if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { CancelSelection(); e.Use(); }
                else if (selecting && (e.type == EventType.MouseDrag || e.type == EventType.MouseUp) && e.button == 0)
                {
                    Vector2 point = e.mousePosition - rect.position;
                    RunSelection(() =>
                    {
                        if (selectionTool == VoxelSelectionTool.Brush) selectionJob.Brush(gestureEnd, point, brushDiameter);
                        gestureEnd = point;
                        if (e.type == EventType.MouseUp)
                        {
                            if (selectionTool == VoxelSelectionTool.Rectangle) selectionJob.Rectangle(gestureStart, gestureEnd);
                            selecting = false;
                            ReleaseSelectionControl();
                        }
                    });
                    e.Use(); Repaint();
                }
                return;
            }
            if (!rect.Contains(e.mousePosition)) return;
            if (e.type == EventType.ScrollWheel)
            { distance = ZoomDistance(distance, e.delta.y, edit.Grid.VoxelSize, radius); e.Use(); Repaint(); }
            if (e.type == EventType.MouseDrag && (e.button == 2 || (e.alt && e.shift && e.button == 0)))
            {
                float unitsPerPoint = 2 * distance * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * .5f) / Mathf.Max(1, rect.height);
                panOffset = Vector3.ClampMagnitude(panOffset + rotation * new Vector3(-e.delta.x, e.delta.y, 0) * unitsPerPoint, radius * 100);
                selecting = false; e.Use(); Repaint();
            }
            if (e.type == EventType.MouseDrag && (e.alt || e.button == 1))
            { orbit = RotateView(orbit, e.delta); selecting = false; e.Use(); Repaint(); }
            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                RunSelection(() =>
                {
                    selectionJob = new VoxelSurfaceSelection(edit.Grid, camera, rect.size,
                        e.shift ? VoxelSelectionMode.Subtract : selectionMode, selected);
                    selectionViewport = rect;
                    gestureStart = gestureEnd = e.mousePosition - rect.position;
                    selecting = true; selectionControl = control; GUIUtility.hotControl = control;
                    if (selectionTool == VoxelSelectionTool.Brush) selectionJob.Brush(gestureStart, gestureEnd, brushDiameter);
                    EditorApplication.update += ProcessSelection;
                });
                e.Use(); Repaint(); return;
            }
            if ((e.type == EventType.MouseMove || e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && !e.alt && e.button == 0)
            {
                Vector2 uv = new((e.mousePosition.x - rect.x) / rect.width, 1 - (e.mousePosition.y - rect.y) / rect.height);
                hover = VoxelSurfaceEdit.Pick(edit.Grid, camera.ViewportPointToRay(uv), out int index) ? index : -1;
                Repaint();
            }
        }

        private void OnLostFocus() => CancelSelection();

        private void ProcessSelection()
        {
            if (selectionJob == null) { EditorApplication.update -= ProcessSelection; return; }
            if (EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode) { CancelSelection(); return; }
            RunSelection(() =>
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                do { selectionJob.Step(64); } while (!selectionJob.IsIdle && timer.Elapsed.TotalMilliseconds < 4);
                if (selectionJob.IsIdle && !selecting)
                {
                    selected.Clear(); selected.UnionWith(selectionJob.Result);
                    CancelSelection();
                }
                else if (EditorApplication.timeSinceStartup >= nextSelectionRepaint)
                { nextSelectionRepaint = EditorApplication.timeSinceStartup + .1; Repaint(); }
            });
        }

        private void RunSelection(Action action)
        {
            try { action(); }
            catch (Exception exception)
            { CancelSelection(); status = exception.Message; statusType = MessageType.Warning; Repaint(); }
        }

        private void ReleaseSelectionControl()
        {
            if (selectionControl != 0 && GUIUtility.hotControl == selectionControl) GUIUtility.hotControl = 0;
            selectionControl = 0;
        }

        private void CancelSelection()
        {
            EditorApplication.update -= ProcessSelection;
            selecting = false;
            ReleaseSelectionControl();
            // A completed replacement may have the same count and reuse the committed set.
            // Invalidate even if the provisional result never received a Repaint event.
            if (selectionJob != null) overlayCount = -1;
            selectionJob?.Dispose(); selectionJob = null;
            Repaint();
        }

        internal static Vector2 RotateView(Vector2 angles, Vector2 delta) => new(
            Mathf.Clamp(angles.x + delta.y * .5f, -89, 89),
            Mathf.Repeat(angles.y + delta.x * .5f + 180, 360) - 180);

        internal static float ZoomDistance(float current, float scroll, float voxelSize, float modelRadius) =>
            Mathf.Clamp(current * Mathf.Exp(Mathf.Clamp(scroll, -100, 100) * .06f), voxelSize * 2, Mathf.Max(voxelSize * 2, modelRadius * 100));

        internal static void SelectionFrame(VoxelGrid grid, IReadOnlyCollection<int> cells, out Vector3 pivot, out float frameDistance)
        {
            if (cells == null || cells.Count == 0) throw new ArgumentException("Select at least one occupied cell.", nameof(cells));
            Vector3Int min = grid.Size, max = Vector3Int.zero;
            foreach (int cell in cells)
            {
                if (cell < 0 || cell >= grid.Occupied.Length || !grid.Occupied[cell]) throw new ArgumentException("Invalid selected cell.", nameof(cells));
                grid.Coordinates(cell, out int x, out int y, out int z);
                var position = new Vector3Int(x, y, z);
                min = Vector3Int.Min(min, position); max = Vector3Int.Max(max, position);
            }
            pivot = grid.Origin + ((Vector3)min + max + Vector3.one) * (.5f * grid.VoxelSize);
            frameDistance = Mathf.Max(grid.VoxelSize * 2, ((Vector3)(max - min) + Vector3.one).magnitude * grid.VoxelSize * 2);
        }

        private void FrameSelection()
        {
            SelectionFrame(edit.Grid, selected, out var pivot, out distance);
            panOffset = pivot - center;
            Repaint();
        }

        [Shortcut("Voxel Bridge/Surface Painter/Undo", typeof(VoxelSurfacePainterWindow), KeyCode.Z, ShortcutModifiers.Action)]
        private static void UndoShortcut(ShortcutArguments args) => (args.context as VoxelSurfacePainterWindow)?.ApplyHistoryShortcut(false);

        [Shortcut("Voxel Bridge/Surface Painter/Redo", typeof(VoxelSurfacePainterWindow), KeyCode.Y, ShortcutModifiers.Action)]
        private static void RedoShortcut(ShortcutArguments args) => (args.context as VoxelSurfacePainterWindow)?.ApplyHistoryShortcut(true);

        [Shortcut("Voxel Bridge/Surface Painter/Redo Alternate", typeof(VoxelSurfacePainterWindow), KeyCode.Z, ShortcutModifiers.Action | ShortcutModifiers.Shift)]
        private static void RedoAlternateShortcut(ShortcutArguments args) => (args.context as VoxelSurfacePainterWindow)?.ApplyHistoryShortcut(true);

        private void ApplyHistoryShortcut(bool redo)
        {
            if (edit == null || selectionJob != null || EditorApplication.isPlayingOrWillChangePlaymode || EditorGUIUtility.editingTextField) return;
            if (redo ? edit.CanRedo : edit.CanUndo) Run(() => Change(redo ? edit.Redo : edit.Undo));
        }

        private void UpdateSelectionOverlay()
        {
            if (selectionMaterial == null)
            {
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(VoxelSelectionOverlay.ShaderPath);
                if (shader == null || !shader.isSupported || ShaderUtil.ShaderHasError(shader)) throw new InvalidOperationException("Selection overlay shader is unavailable.");
                selectionMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                hoverMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                selectionMaterial.SetColor("_Color", new Color(1, .8f, .05f, 1));
                hoverMaterial.SetColor("_Color", Color.cyan);
            }
            var current = selectionJob?.Result ?? selected;
            if (!ReferenceEquals(overlaySource, current) || overlayCount != current.Count)
            {
                var generated = VoxelSelectionOverlay.Build(edit.Grid, current, showSelectionTint);
                VoxelSelectionOverlay.Destroy(selectionMeshes); selectionMeshes.AddRange(generated);
                selectionMaterial.SetFloat("_FillOpacity", showSelectionTint ? .04f : 0);
                overlaySource = current; overlayCount = current.Count;
            }
            if (overlayHover != hover)
            {
                VoxelSelectionOverlay.Destroy(hoverMeshes);
                if (hover >= 0) hoverMeshes.AddRange(VoxelSelectionOverlay.Build(edit.Grid, new[] { hover }));
                overlayHover = hover;
            }
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
            if (edit == null) { status = "Source saved, but preview reload failed: " + status; statusType = MessageType.Error; return; }
            status = warning ?? "Fuente guardada. Rebuild actualiza el prefab; los LODs descendientes no se regeneran automáticamente.";
            statusType = warning == null ? MessageType.Info : MessageType.Warning;
        }

        private void Rebuild()
        {
            ResolveRebuildTarget(out string path, out string manifestPath);
            if (manifestPath != null) VoxelProductionFamily.RebuildLevel(manifestPath, level, VoxelProductionEditor.Progress);
            else VoxelProductionExporter.Rebuild(path, VoxelProductionEditor.Progress);
            status = "Malla actualizada. Los LODs descendientes conservan sus archivos y requieren regeneración explícita para heredar los cambios.";
            statusType = MessageType.Info;
        }

        private void SaveAndRebuild()
        {
            ResolveRebuildTarget(out _, out _);
            if (edit == null) throw new InvalidOperationException("Load a valid source before rebuilding.");
            if (edit.PendingCells > 0)
            {
                SaveSource();
                if (edit == null || statusType == MessageType.Warning || statusType == MessageType.Error) return;
            }
            Rebuild();
        }

        private void ResolveRebuildTarget(out string path, out string manifestPath)
        {
            path = AssetDatabase.GUIDToAssetPath(prefabGuid);
            manifestPath = null;
            var link = VoxelProductionLink.Load(path);
            if (!string.IsNullOrEmpty(link.manifestGuid))
            {
                manifestPath = AssetDatabase.GUIDToAssetPath(link.manifestGuid);
                var manifest = VoxelProductionFamily.Load(manifestPath);
                if (level < 0 || level >= manifest.lods.Length || VoxelProductionFamily.SourcePath(manifest.lods[level]) != SourcePath)
                    throw new InvalidOperationException("The production source link changed. Reopen the editor from the intended LOD.");
            }
            else
            {
                if (link.SourcePath != SourcePath) throw new InvalidOperationException("The production source link changed. Reopen the surface editor.");
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
