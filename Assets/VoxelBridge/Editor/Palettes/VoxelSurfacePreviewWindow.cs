using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace LocalModels.VoxelBridge
{
    internal sealed class VoxelSurfacePreviewWindow : EditorWindow
    {
        [SerializeField] private VoxelSurfacePalette palette;
        [SerializeField] private int surfaceId;
        [SerializeField] private int shape;
        [SerializeField] private Color referenceColor = new(.65f, .65f, .65f, 1);
        [SerializeField] private Vector2 orbit = new(15, -25);
        private PreviewRenderUtility preview;
        private Material material;
        private Mesh sphere, cube;
        private int dragControl, dirtyCount = -1;

        internal static void Open(VoxelSurfacePalette source, int id)
        {
            var window = GetWindow<VoxelSurfacePreviewWindow>("Surface Preview");
            window.palette = source;
            window.surfaceId = id;
            window.minSize = new Vector2(340, 380);
            window.Show();
            window.Repaint();
        }

        private void OnEnable()
        {
            Undo.undoRedoPerformed += Repaint;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= Repaint;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            ReleasePreview();
        }

        private void OnInspectorUpdate()
        {
            int current = palette == null ? 0 : EditorUtility.GetDirtyCount(palette);
            if (current == dirtyCount) return;
            dirtyCount = current;
            Repaint();
        }

        private void OnPlayModeChanged(PlayModeStateChange _) { ReleasePreview(); Repaint(); }
        private void OnLostFocus() => ReleaseDrag();

        private void OnGUI()
        {
            palette = (VoxelSurfacePalette)EditorGUILayout.ObjectField("Palette", palette, typeof(VoxelSurfacePalette), false);
            if (palette == null) { EditorGUILayout.HelpBox("Seleccionar una paleta de superficies.", MessageType.Info); return; }
            if (!palette.TryValidate(out string error)) { EditorGUILayout.HelpBox(error, MessageType.Error); return; }
            if (!palette.TryGetSurface(surfaceId, out var surface)) surface = palette.Entries[0];
            surfaceId = surface.Id;
            if (EditorGUILayout.DropdownButton(new GUIContent($"{surface.Id:000} · {surface.DisplayName}"), FocusType.Keyboard))
            {
                var menu = new GenericMenu();
                foreach (var entry in palette.Entries)
                {
                    int id = entry.Id;
                    menu.AddItem(new GUIContent($"{id:000} · {entry.DisplayName}"), id == surfaceId,
                        () => { surfaceId = id; Repaint(); });
                }
                menu.ShowAsContext();
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                shape = GUILayout.Toolbar(shape, new[] { "Sphere", "Cube" }, GUILayout.Width(140));
                referenceColor = EditorGUILayout.ColorField(new GUIContent("View Color", "Color de referencia; no modifica ColorID ni la paleta."), referenceColor, true, false, false);
            }
            EditorGUILayout.LabelField($"Metallic {surface.Metallic:0.##}   Smoothness {surface.Smoothness:0.##}");
            EditorGUILayout.LabelField($"Emission {surface.Emission:0.##}   Occlusion {surface.OcclusionMultiplier:0.##}");
            EditorGUILayout.HelpBox("Referencia HDRP opaca con iluminación fija. Arrastrar para rotar. No reproduce exposición, bloom ni el entorno de la escena; la oclusión no es visible sin geometría o mapas que la produzcan.", MessageType.None);
            if (surface.RenderClass != VoxelSurfaceRenderClass.Opaque)
            { EditorGUILayout.HelpBox("La vista sólo admite superficies opacas; no simula esta clase de render.", MessageType.Info); return; }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            { EditorGUILayout.HelpBox("La vista está disponible fuera de Play Mode.", MessageType.Info); return; }
            Rect rect = GUILayoutUtility.GetRect(100, 100, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            HandleNavigation(rect);
            if (Event.current.type != EventType.Repaint || rect.width < 2 || rect.height < 2) return;
            try { DrawPreview(rect, surface); }
            catch (Exception exception)
            {
                ReleasePreview();
                GUI.Label(rect, exception.Message, EditorStyles.wordWrappedLabel);
            }
        }

        private void HandleNavigation(Rect rect)
        {
            int control = GUIUtility.GetControlID(FocusType.Passive);
            var e = Event.current;
            if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition) && (e.button == 0 || e.button == 1))
            { dragControl = control; GUIUtility.hotControl = control; e.Use(); }
            else if (dragControl == control && e.type == EventType.MouseDrag)
            {
                orbit.x = Mathf.Clamp(orbit.x + e.delta.y * .5f, -85, 85);
                orbit.y = Mathf.Repeat(orbit.y + e.delta.x * .5f + 180, 360) - 180;
                e.Use(); Repaint();
            }
            else if (dragControl == control && e.type == EventType.MouseUp)
            { ReleaseDrag(); e.Use(); }
        }

        private void ReleaseDrag()
        {
            if (dragControl != 0 && GUIUtility.hotControl == dragControl) GUIUtility.hotControl = 0;
            dragControl = 0;
        }

        private void EnsurePreview()
        {
            if (preview != null) return;
            var shader = Shader.Find("HDRP/Lit");
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("HDRP/Lit is unavailable for this preview.");
            sphere = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
            cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            if (sphere == null || cube == null) throw new InvalidOperationException("Unity preview primitives are unavailable.");
            material = new Material(shader) { name = "Voxel Surface Preview (Temporary)", hideFlags = HideFlags.HideAndDontSave };
            preview = new PreviewRenderUtility();
            preview.camera.fieldOfView = 35;
            preview.camera.nearClipPlane = .1f;
            preview.camera.farClipPlane = 20;
            preview.camera.clearFlags = CameraClearFlags.SolidColor;
            preview.camera.backgroundColor = new Color(.12f, .13f, .15f);
            preview.ambientColor = new Color(.25f, .25f, .25f);
            ConfigureLight(preview.lights[0], 4, new Vector3(25, 25, 0));
            ConfigureLight(preview.lights[1], 2, new Vector3(335, 315, 0));
        }

        private static void ConfigureLight(Light light, float lux, Vector3 rotation)
        {
            var hd = light.GetComponent<HDAdditionalLightData>();
            if (hd == null) hd = light.gameObject.AddComponent<HDAdditionalLightData>();
            hd.angularDiameter = 20;
            light.lightUnit = LightUnit.Lux;
            light.intensity = lux;
            light.color = Color.white;
            light.useColorTemperature = false;
            light.shadows = LightShadows.None;
            light.transform.rotation = Quaternion.Euler(rotation);
        }

        internal static void ApplySurface(Material target, VoxelSurfaceDefinition surface, Color color)
        {
            if (target == null || target.shader == null || target.shader.name != "HDRP/Lit")
                throw new ArgumentException("The preview requires an HDRP/Lit material.", nameof(target));
            if ((target.hideFlags & HideFlags.DontSave) != HideFlags.DontSave || EditorUtility.IsPersistent(target))
                throw new ArgumentException("The preview must not modify a persistent material.", nameof(target));
            if (surface == null || surface.RenderClass != VoxelSurfaceRenderClass.Opaque)
                throw new ArgumentException("The preview supports opaque surfaces only.", nameof(surface));
            if (!IsUnit(surface.Metallic) || !IsUnit(surface.Smoothness) || !IsUnit(surface.Emission) ||
                !IsUnit(surface.OcclusionMultiplier) || !IsUnit(color.r) || !IsUnit(color.g) || !IsUnit(color.b))
                throw new ArgumentException("Preview values must be finite and within 0..1.");
            color.a = 1;
            target.SetColor("_BaseColor", color);
            target.SetFloat("_Metallic", surface.Metallic);
            target.SetFloat("_Smoothness", surface.Smoothness);
            target.SetFloat("_AORemapMin", surface.OcclusionMultiplier);
            target.SetFloat("_AORemapMax", surface.OcclusionMultiplier);
            target.SetFloat("_UseEmissiveIntensity", 0);
            target.SetColor("_EmissiveColor", color.linear * surface.Emission);
            target.SetFloat("_EmissiveExposureWeight", 0);
            HDMaterial.ValidateMaterial(target);
        }

        private static bool IsUnit(float value) => float.IsFinite(value) && value >= 0 && value <= 1;

        private void DrawPreview(Rect rect, VoxelSurfaceDefinition surface)
        {
            EnsurePreview();
            ApplySurface(material, surface, referenceColor);
            var rotation = Quaternion.Euler(orbit.x, orbit.y, 0);
            Mesh selectedMesh = shape == 0 ? sphere : cube;
            preview.camera.aspect = rect.width / rect.height;
            float distance = FramingDistance(selectedMesh.bounds, shape == 0, preview.camera.aspect);
            preview.camera.fieldOfView = 35;
            preview.camera.farClipPlane = distance + selectedMesh.bounds.extents.magnitude * 4;
            preview.camera.transform.SetPositionAndRotation(selectedMesh.bounds.center + rotation * new Vector3(0, 0, -distance), rotation);
            preview.BeginPreview(rect, GUIStyle.none);
            Texture texture;
            try
            {
                preview.DrawMesh(selectedMesh, Matrix4x4.identity, material, 0);
                preview.Render(true, false);
            }
            finally { texture = preview.EndPreview(); }
            GUI.DrawTexture(rect, texture, ScaleMode.StretchToFill, false);
        }

        internal static float FramingDistance(Bounds bounds, bool sphere, float aspect)
        {
            float radius = sphere ? Mathf.Max(bounds.extents.x, Mathf.Max(bounds.extents.y, bounds.extents.z)) : bounds.extents.magnitude;
            float halfAngle = Mathf.Atan(Mathf.Tan(17.5f * Mathf.Deg2Rad) * Mathf.Min(1, Mathf.Max(.01f, aspect)));
            return Mathf.Max(.01f, radius) / Mathf.Sin(halfAngle) * 1.12f;
        }

        private void ReleasePreview()
        {
            ReleaseDrag();
            preview?.Cleanup(); preview = null;
            if (material != null) DestroyImmediate(material);
            material = null;
            sphere = null; cube = null; // Shared Unity primitives are never destroyed.
        }
    }
}
