using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    internal sealed class VoxelSemanticBindingWindow : EditorWindow
    {
        private sealed class BindingRow
        {
            public int Slot;
            public Color32 SourceColor;
            public int ColorId = -1;
            public int SurfaceId = VoxelPaletteConstants.DefaultId;
        }

        [SerializeField] private UnityEngine.Object voxAsset;
        [SerializeField] private VoxelColorPalette colorPalette;
        [SerializeField] private VoxelSurfacePalette surfacePalette;
        [SerializeField] private Vector2 scroll;

        private readonly List<BindingRow> rows = new();
        private string loadedPath;
        private string status;
        private MessageType statusType = MessageType.Info;
        private bool hasUnsupportedIndexMap;

        [MenuItem("Tools/Voxel Bridge/Vincular IDs semánticos")]
        private static void OpenWindow()
        {
            var window = GetWindow<VoxelSemanticBindingWindow>("IDs semánticos VOX");
            window.minSize = new Vector2(620f, 420f);
            window.UseSelectedVox();
            window.Show();
        }

        [MenuItem("Assets/Voxel Bridge/Vincular IDs semánticos", false, 2050)]
        private static void OpenForSelection()
        {
            OpenWindow();
        }

        [MenuItem("Assets/Voxel Bridge/Vincular IDs semánticos", true)]
        private static bool ValidateOpenForSelection() =>
            IsVoxPath(AssetDatabase.GetAssetPath(Selection.activeObject));

        private void OnEnable()
        {
            LoadCanonicalPalettes();
            if (voxAsset == null) UseSelectedVox();
            Reload();
        }

        private void OnSelectionChange()
        {
            string path = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (!IsVoxPath(path)) return;
            voxAsset = Selection.activeObject;
            Reload();
            Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Vinculación semántica de .vox", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Asigna un ColorID y un SurfaceID globales a cada slot utilizado por el archivo. " +
                "El sidecar conserva la relación y valida la paleta al volver a leer el volumen.",
                MessageType.Info);

            EditorGUI.BeginChangeCheck();
            voxAsset = EditorGUILayout.ObjectField("Archivo .vox", voxAsset,
                typeof(UnityEngine.Object), false);
            if (EditorGUI.EndChangeCheck()) Reload();

            EditorGUI.BeginChangeCheck();
            colorPalette = (VoxelColorPalette)EditorGUILayout.ObjectField(
                "Paleta de colores", colorPalette, typeof(VoxelColorPalette), false);
            surfacePalette = (VoxelSurfacePalette)EditorGUILayout.ObjectField(
                "Paleta de superficies", surfacePalette, typeof(VoxelSurfacePalette), false);
            if (EditorGUI.EndChangeCheck()) Reload();

            if (!string.IsNullOrEmpty(status))
                EditorGUILayout.HelpBox(status, statusType);
            if (hasUnsupportedIndexMap)
                EditorGUILayout.HelpBox(
                    "El archivo contiene un IMAP. No se puede vincular sin conocer " +
                    "el remapeo aplicado por el editor que guardó el archivo.", MessageType.Error);

            if (rows.Count == 0) return;
            DrawBindings();

            bool complete = !hasUnsupportedIndexMap &&
                            rows.All(row => row.ColorId >= 0 && row.SurfaceId >= 0);
            using (new EditorGUI.DisabledScope(!complete))
            {
                if (GUILayout.Button("Guardar vinculación semántica", GUILayout.Height(30f)))
                    SaveBindings();
            }
            if (!complete && !hasUnsupportedIndexMap)
                EditorGUILayout.HelpBox(
                    "Todos los slots deben tener ColorID y SurfaceID antes de guardar.",
                    MessageType.Warning);
        }

        private void DrawBindings()
        {
            VoxelColorDefinition[] colors = colorPalette?.Entries?
                .Where(entry => entry != null).OrderBy(entry => entry.Id).ToArray() ??
                Array.Empty<VoxelColorDefinition>();
            VoxelSurfaceDefinition[] surfaces = surfacePalette?.Entries?
                .Where(entry => entry != null).OrderBy(entry => entry.Id).ToArray() ??
                Array.Empty<VoxelSurfaceDefinition>();
            string[] colorLabels = colors.Select(entry =>
                $"{entry.Id:D3} · {entry.DisplayName}").Prepend("Sin asignar").ToArray();
            string[] surfaceLabels = surfaces.Select(entry =>
                $"{entry.Id:D3} · {entry.DisplayName}").Prepend("Sin asignar").ToArray();

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField($"Slots utilizados: {rows.Count}", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(143f);
                GUILayout.Label("ColorID", EditorStyles.miniBoldLabel, GUILayout.MinWidth(185f));
                GUILayout.Label("SurfaceID", EditorStyles.miniBoldLabel, GUILayout.MinWidth(185f));
            }
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (BindingRow row in rows)
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    Rect swatch = GUILayoutUtility.GetRect(30f, 30f,
                        GUILayout.Width(30f), GUILayout.Height(30f));
                    EditorGUI.DrawRect(swatch, row.SourceColor);
                    GUILayout.Label($"Slot {row.Slot:D3}\n#{ColorUtility.ToHtmlStringRGBA(row.SourceColor)}",
                        GUILayout.Width(105f));

                    int colorIndex = Array.FindIndex(colors, entry => entry.Id == row.ColorId) + 1;
                    int selectedColor = EditorGUILayout.Popup(
                        Mathf.Max(0, colorIndex), colorLabels, GUILayout.MinWidth(185f));
                    row.ColorId = selectedColor == 0 ? -1 : colors[selectedColor - 1].Id;

                    int surfaceIndex = Array.FindIndex(surfaces, entry => entry.Id == row.SurfaceId) + 1;
                    int selectedSurface = EditorGUILayout.Popup(
                        Mathf.Max(0, surfaceIndex), surfaceLabels, GUILayout.MinWidth(185f));
                    row.SurfaceId = selectedSurface == 0 ? -1 : surfaces[selectedSurface - 1].Id;
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private void SaveBindings()
        {
            string path = AssetDatabase.GetAssetPath(voxAsset);
            var bindings = new List<VoxelSemanticSlotMetadata>(rows.Count);
            foreach (BindingRow row in rows)
            {
                if (!colorPalette.TryGetColor(row.ColorId, out Color32 color))
                {
                    SetStatus($"El ColorID {row.ColorId} no existe en la paleta global.", MessageType.Error);
                    return;
                }
                bindings.Add(new VoxelSemanticSlotMetadata
                {
                    slot = row.Slot,
                    colorId = row.ColorId,
                    surfaceId = row.SurfaceId,
                    displayColor = color
                });
            }

            bool duplicateRgbWithDifferentSurfaces = bindings
                .GroupBy(entry => entry.displayColor)
                .Any(group => group.Select(entry => entry.surfaceId).Distinct().Count() > 1);
            string risk = duplicateRgbWithDifferentSurfaces
                ? "\n\nHay colores RGB idénticos con superficies distintas. La conservación de esos " +
                  "slots al guardar nuevamente en MagicaVoxel todavía no está certificada."
                : string.Empty;
            if (!EditorUtility.DisplayDialog("Guardar vinculación semántica",
                    "Se actualizará la paleta RGBA del .vox y su sidecar. " +
                    "Es recomendable mantener ambos archivos bajo control de versiones." + risk,
                    "Guardar", "Cancelar"))
                return;

            if (VoxelSemanticBindingService.TryBind(
                    path, bindings, colorPalette, surfacePalette, out string message))
            {
                Reload();
                SetStatus(message, duplicateRgbWithDifferentSurfaces
                    ? MessageType.Warning
                    : MessageType.Info);
            }
            else SetStatus(message, MessageType.Error);
        }

        private void Reload()
        {
            rows.Clear();
            loadedPath = AssetDatabase.GetAssetPath(voxAsset);
            hasUnsupportedIndexMap = false;
            if (voxAsset == null)
            {
                SetStatus("Selecciona un archivo .vox generado por Voxel Bridge.", MessageType.Info);
                return;
            }
            if (!IsVoxPath(loadedPath))
            {
                SetStatus("El asset seleccionado no es un archivo .vox.", MessageType.Error);
                return;
            }
            if (colorPalette == null || surfacePalette == null)
            {
                SetStatus("Asigna las paletas globales de color y superficie.", MessageType.Error);
                return;
            }

            try
            {
                string absolutePath = VoxelLodPipeline.AssetPathToAbsolute(loadedPath);
                VoxelSemanticVoxDocument document = VoxelSemanticVoxDocument.Read(absolutePath);
                hasUnsupportedIndexMap = document.HasUnsupportedIndexMap;

                VoxelSemanticMetadata existing = null;
                if (VoxelImporterIntegration.TryLoadMetadata(
                        loadedPath, out VoxelBridgeMetadata metadata, out _))
                    existing = metadata.semantic;
                var existingBySlot = (existing?.slots ?? Array.Empty<VoxelSemanticSlotMetadata>())
                    .Where(entry => entry != null)
                    .GroupBy(entry => entry.slot)
                    .ToDictionary(group => group.Key, group => group.First());

                foreach (byte slot in document.UsedSlots)
                {
                    Color32 sourceColor = document.Palette[slot - 1];
                    var row = new BindingRow { Slot = slot, SourceColor = sourceColor };
                    if (existingBySlot.TryGetValue(slot, out VoxelSemanticSlotMetadata entry))
                    {
                        row.ColorId = entry.colorId;
                        row.SurfaceId = entry.surfaceId;
                    }
                    else
                    {
                        int[] exact = colorPalette.Entries
                            .Where(entry => entry != null && ((Color32)entry.Color).Equals(sourceColor))
                            .Select(entry => entry.Id).ToArray();
                        if (exact.Length == 1) row.ColorId = exact[0];
                    }
                    rows.Add(row);
                }

                int unmapped = rows.Count(row => row.ColorId < 0);
                SetStatus(unmapped == 0
                        ? "La tabla está completa y lista para validarse."
                        : $"{unmapped} slot(s) no coinciden con un ColorID global y requieren asignación.",
                    unmapped == 0 ? MessageType.Info : MessageType.Warning);
            }
            catch (Exception exception)
            {
                rows.Clear();
                SetStatus("No se pudo leer el archivo: " + exception.Message, MessageType.Error);
            }
        }

        private void UseSelectedVox()
        {
            string path = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (IsVoxPath(path)) voxAsset = Selection.activeObject;
        }

        private void LoadCanonicalPalettes()
        {
            colorPalette ??= AssetDatabase.LoadAssetAtPath<VoxelColorPalette>(
                VoxelPaletteAssetUtility.ColorPalettePath);
            surfacePalette ??= AssetDatabase.LoadAssetAtPath<VoxelSurfacePalette>(
                VoxelPaletteAssetUtility.SurfacePalettePath);
        }

        private void SetStatus(string message, MessageType type)
        {
            status = message;
            statusType = type;
            Repaint();
        }

        private static bool IsVoxPath(string path) =>
            !string.IsNullOrWhiteSpace(path) &&
            path.EndsWith(".vox", StringComparison.OrdinalIgnoreCase);
    }
}
