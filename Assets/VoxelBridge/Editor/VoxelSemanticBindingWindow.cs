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
            public int VoxelCount;
            public int ColorId = -1;
            public int SurfaceId = VoxelPaletteConstants.DefaultId;
            public int SuggestedColorId = -1;
            public float MatchDistance = -1f;
        }

        [SerializeField] private UnityEngine.Object voxAsset;
        [SerializeField] private VoxelColorPalette colorPalette;
        [SerializeField] private VoxelSurfacePalette surfacePalette;
        [SerializeField] private VoxelColorMappingProfile mappingProfile;
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
            window.minSize = new Vector2(860f, 460f);
            window.UseSelectedVox();
            window.Show();
        }

        [MenuItem("Assets/Voxel Bridge/Vincular IDs semánticos", false, 2050)]
        private static void OpenForSelection() => OpenWindow();

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
                "Asigna un ColorID y un SurfaceID globales a cada slot utilizado. Un perfil de " +
                "mapeo limita los colores candidatos y propone la coincidencia perceptual más cercana.",
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

            EditorGUI.BeginChangeCheck();
            mappingProfile = (VoxelColorMappingProfile)EditorGUILayout.ObjectField(
                new GUIContent("Perfil de mapeo",
                    "Selecciona los ColorIDs candidatos y los umbrales del mapeo OKLab."),
                mappingProfile, typeof(VoxelColorMappingProfile), false);
            if (EditorGUI.EndChangeCheck())
            {
                if (mappingProfile != null && mappingProfile.ColorPalette != null)
                    colorPalette = mappingProfile.ColorPalette;
                ApplyProfileToUnmapped();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Crear perfil para esta paleta")) CreateMappingProfile();
                using (new EditorGUI.DisabledScope(mappingProfile == null || rows.Count == 0))
                {
                    if (GUILayout.Button("Asignar slots sin ColorID")) ApplyProfileToUnmapped();
                }
                if (mappingProfile != null && GUILayout.Button("Seleccionar perfil"))
                {
                    Selection.activeObject = mappingProfile;
                    EditorGUIUtility.PingObject(mappingProfile);
                }
            }

            if (!string.IsNullOrEmpty(status))
                EditorGUILayout.HelpBox(status, statusType);
            if (hasUnsupportedIndexMap)
                EditorGUILayout.HelpBox(
                    "El archivo contiene un IMAP. No se puede vincular sin conocer " +
                    "el remapeo aplicado por el editor que guardó el archivo.", MessageType.Error);

            if (rows.Count == 0) return;
            DrawBindings();

            bool profileValid = TryValidateSelectedProfile(out string profileError);
            bool complete = !hasUnsupportedIndexMap && profileValid &&
                            rows.All(row =>
                                colorPalette.TryGetColor(row.ColorId, out _) &&
                                surfacePalette.TryGetSurface(row.SurfaceId, out _));
            using (new EditorGUI.DisabledScope(!complete))
            {
                if (GUILayout.Button("Guardar vinculación semántica", GUILayout.Height(30f)))
                    SaveBindings();
            }
            if (!profileValid)
                EditorGUILayout.HelpBox(profileError, MessageType.Error);
            else if (!complete && !hasUnsupportedIndexMap)
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
                GUILayout.Space(138f);
                GUILayout.Label("Color original", EditorStyles.miniBoldLabel, GUILayout.Width(88f));
                GUILayout.Label("ColorID asignado", EditorStyles.miniBoldLabel, GUILayout.MinWidth(190f));
                GUILayout.Label("Distancia", EditorStyles.miniBoldLabel, GUILayout.Width(76f));
                GUILayout.Label("SurfaceID", EditorStyles.miniBoldLabel, GUILayout.MinWidth(180f));
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (BindingRow row in rows)
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    GUILayout.Label($"Slot {row.Slot:D3}\n{row.VoxelCount:N0} voxels", GUILayout.Width(100f));
                    DrawSwatch(row.SourceColor, 30f);
                    GUILayout.Label("→", GUILayout.Width(16f));
                    Color32 mappedColor = TryGetMappedColor(row, out Color32 resolved)
                        ? resolved
                        : new Color32(32, 32, 32, 255);
                    DrawSwatch(mappedColor, 30f);

                    int colorIndex = Array.FindIndex(colors, entry => entry.Id == row.ColorId) + 1;
                    EditorGUI.BeginChangeCheck();
                    int selectedColor = EditorGUILayout.Popup(
                        Mathf.Max(0, colorIndex), colorLabels, GUILayout.MinWidth(190f));
                    if (EditorGUI.EndChangeCheck())
                    {
                        row.ColorId = selectedColor == 0 ? -1 : colors[selectedColor - 1].Id;
                        if (row.ColorId < 0) row.SuggestedColorId = -1;
                        UpdateDistance(row);
                    }

                    GUILayout.Label(BuildDistanceLabel(row), GUILayout.Width(76f));

                    int surfaceIndex = Array.FindIndex(surfaces, entry => entry.Id == row.SurfaceId) + 1;
                    int selectedSurface = EditorGUILayout.Popup(
                        Mathf.Max(0, surfaceIndex), surfaceLabels, GUILayout.MinWidth(180f));
                    row.SurfaceId = selectedSurface == 0 ? -1 : surfaces[selectedSurface - 1].Id;
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private static void DrawSwatch(Color32 color, float size)
        {
            Rect swatch = GUILayoutUtility.GetRect(size, size,
                GUILayout.Width(size), GUILayout.Height(size));
            EditorGUI.DrawRect(swatch, color);
        }

        private string BuildDistanceLabel(BindingRow row)
        {
            if (row.ColorId < 0)
                return row.SuggestedColorId >= 0 && mappingProfile != null
                    ? $"> {mappingProfile.MaximumAutomaticDistance:0.#}"
                    : "—";
            if (row.MatchDistance < 0f) return "—";
            string prefix = mappingProfile != null &&
                            row.MatchDistance > mappingProfile.WarningDistance ? "⚠ " : string.Empty;
            return prefix + row.MatchDistance.ToString("0.00");
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
            int distantMappings = mappingProfile == null ? 0 : rows.Count(row =>
                row.MatchDistance > mappingProfile.WarningDistance);
            string risk = duplicateRgbWithDifferentSurfaces
                ? "\n\nHay colores RGB idénticos con superficies distintas. La conservación de esos " +
                  "slots al guardar nuevamente en MagicaVoxel todavía no está certificada."
                : string.Empty;
            if (distantMappings > 0)
                risk += $"\n\n{distantMappings} asignación(es) superan el umbral de advertencia del perfil.";
            if (!EditorUtility.DisplayDialog("Guardar vinculación semántica",
                    "Se actualizará la paleta RGBA del .vox y su sidecar. " +
                    "Es recomendable mantener ambos archivos bajo control de versiones." + risk,
                    "Guardar", "Cancelar"))
                return;

            if (VoxelSemanticBindingService.TryBind(
                    path, bindings, colorPalette, surfacePalette, mappingProfile, out string message))
            {
                Reload();
                SetStatus(message, duplicateRgbWithDifferentSurfaces || distantMappings > 0
                    ? MessageType.Warning
                    : MessageType.Info);
            }
            else SetStatus(message, MessageType.Error);
        }

        private void Reload()
        {
            string nextPath = AssetDatabase.GetAssetPath(voxAsset);
            bool assetChanged = !string.Equals(loadedPath, nextPath, StringComparison.Ordinal);
            loadedPath = nextPath;
            rows.Clear();
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

            try
            {
                string absolutePath = VoxelLodPipeline.AssetPathToAbsolute(loadedPath);
                VoxelSemanticVoxDocument document = VoxelSemanticVoxDocument.Read(absolutePath);
                hasUnsupportedIndexMap = document.HasUnsupportedIndexMap;

                VoxelSemanticMetadata existing = null;
                if (VoxelImporterIntegration.TryLoadMetadata(
                        loadedPath, out VoxelBridgeMetadata metadata, out _))
                    existing = metadata.semantic;
                if (assetChanged && existing != null)
                {
                    if (VoxelSemanticTransport.TryLoadPalettes(existing,
                            out VoxelColorPalette storedColors,
                            out VoxelSurfacePalette storedSurfaces, out _))
                    {
                        colorPalette = storedColors;
                        surfacePalette = storedSurfaces;
                    }
                    mappingProfile = LoadStoredProfile(existing) ?? mappingProfile;
                }

                if (colorPalette == null || surfacePalette == null)
                {
                    SetStatus("Asigna las paletas globales de color y superficie.", MessageType.Error);
                    return;
                }

                var existingBySlot = (existing?.slots ?? Array.Empty<VoxelSemanticSlotMetadata>())
                    .Where(entry => entry != null)
                    .GroupBy(entry => entry.slot)
                    .ToDictionary(group => group.Key, group => group.First());
                VoxelColorDefinition[] candidates = ResolveAutomaticCandidates(out _);

                foreach (byte slot in document.UsedSlots)
                {
                    Color32 sourceColor = document.Palette[slot - 1];
                    var row = new BindingRow
                    {
                        Slot = slot,
                        SourceColor = sourceColor,
                        VoxelCount = document.SlotUsageCounts[slot]
                    };
                    if (existingBySlot.TryGetValue(slot, out VoxelSemanticSlotMetadata entry))
                    {
                        row.ColorId = entry.colorId;
                        row.SurfaceId = entry.surfaceId;
                        UpdateDistance(row);
                    }
                    else
                    {
                        ApplyCandidate(row, candidates, allowPerceptual: mappingProfile != null);
                    }
                    rows.Add(row);
                }

                RefreshStatus();
            }
            catch (Exception exception)
            {
                rows.Clear();
                SetStatus("No se pudo leer el archivo: " + exception.Message, MessageType.Error);
            }
        }

        private void ApplyProfileToUnmapped()
        {
            if (mappingProfile == null)
            {
                RefreshStatus();
                return;
            }
            if (!TryValidateSelectedProfile(out string error) ||
                !mappingProfile.TryGetAllowedColors(out VoxelColorDefinition[] candidates, out error))
            {
                SetStatus(error, MessageType.Error);
                return;
            }

            foreach (BindingRow row in rows)
            {
                if (row.ColorId >= 0) continue;
                ApplyCandidate(row, candidates, allowPerceptual: true);
            }
            RefreshStatus();
        }

        private void ApplyCandidate(BindingRow row, VoxelColorDefinition[] candidates,
            bool allowPerceptual)
        {
            row.SuggestedColorId = -1;
            row.MatchDistance = -1f;
            if (candidates == null || candidates.Length == 0) return;

            VoxelColorDefinition[] exact = candidates
                .Where(entry => ((Color32)entry.Color).Equals(row.SourceColor))
                .ToArray();
            if (exact.Length == 1)
            {
                row.ColorId = exact[0].Id;
                row.SuggestedColorId = row.ColorId;
                row.MatchDistance = 0f;
                return;
            }
            if (!allowPerceptual || !VoxelColorPerceptualMatcher.TryFindNearest(
                    row.SourceColor, candidates, out VoxelColorMatch match))
                return;

            row.SuggestedColorId = match.ColorId;
            row.MatchDistance = match.Distance;
            if (match.Distance <= mappingProfile.MaximumAutomaticDistance)
                row.ColorId = match.ColorId;
        }

        private VoxelColorDefinition[] ResolveAutomaticCandidates(out string error)
        {
            error = null;
            if (mappingProfile != null)
            {
                if (!TryValidateSelectedProfile(out error) ||
                    !mappingProfile.TryGetAllowedColors(out VoxelColorDefinition[] selected, out error))
                    return Array.Empty<VoxelColorDefinition>();
                return selected;
            }
            return colorPalette?.Entries?.Where(entry => entry != null).ToArray() ??
                   Array.Empty<VoxelColorDefinition>();
        }

        private bool TryValidateSelectedProfile(out string error)
        {
            if (mappingProfile == null)
            {
                error = null;
                return true;
            }
            if (!mappingProfile.TryValidate(out error)) return false;
            if (mappingProfile.ColorPalette != colorPalette)
            {
                error = "El perfil de mapeo y la ventana utilizan paletas de colores diferentes.";
                return false;
            }
            return true;
        }

        private void UpdateDistance(BindingRow row)
        {
            row.MatchDistance = -1f;
            if (row.ColorId < 0 || !colorPalette.TryGetColor(row.ColorId, out Color32 color)) return;
            var candidate = new[] { new VoxelColorDefinition(row.ColorId, "Mapped", color) };
            if (VoxelColorPerceptualMatcher.TryFindNearest(row.SourceColor, candidate,
                    out VoxelColorMatch match))
                row.MatchDistance = match.Distance;
        }

        private bool TryGetMappedColor(BindingRow row, out Color32 color)
        {
            color = default;
            return row.ColorId >= 0 && colorPalette != null &&
                   colorPalette.TryGetColor(row.ColorId, out color);
        }

        private void RefreshStatus()
        {
            if (rows.Count == 0) return;
            if (!TryValidateSelectedProfile(out string profileError))
            {
                SetStatus(profileError, MessageType.Error);
                return;
            }

            int unmapped = rows.Count(row =>
                !colorPalette.TryGetColor(row.ColorId, out _));
            int warnings = mappingProfile == null ? 0 : rows.Count(row =>
                row.ColorId >= 0 && row.MatchDistance > mappingProfile.WarningDistance);
            int rejected = mappingProfile == null ? 0 : rows.Count(row =>
                row.ColorId < 0 && row.SuggestedColorId >= 0);
            if (unmapped == 0 && warnings == 0)
                SetStatus("La tabla está completa y lista para validarse.", MessageType.Info);
            else if (unmapped == 0)
                SetStatus($"Tabla completa con {warnings} coincidencia(s) por encima del umbral de advertencia.",
                    MessageType.Warning);
            else
                SetStatus($"{unmapped} slot(s) permanecen sin ColorID. " +
                    $"{rejected} superan la distancia automática máxima.", MessageType.Warning);
        }

        private void CreateMappingProfile()
        {
            if (colorPalette == null)
            {
                EditorUtility.DisplayDialog(
                    "Voxel Bridge", "Asigna primero la paleta global de colores.", "Cerrar");
                return;
            }
            string path = EditorUtility.SaveFilePanelInProject(
                "Crear perfil de mapeo de color", "VoxelColorMappingProfile", "asset",
                "Selecciona una ubicación para el perfil.", VoxelPaletteAssetUtility.PaletteFolder);
            if (string.IsNullOrEmpty(path)) return;

            var profile = CreateInstance<VoxelColorMappingProfile>();
            profile.Initialize(colorPalette);
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            mappingProfile = profile;
            Selection.activeObject = profile;
            EditorGUIUtility.PingObject(profile);
            ApplyProfileToUnmapped();
        }

        private static VoxelColorMappingProfile LoadStoredProfile(VoxelSemanticMetadata metadata)
        {
            string path = string.IsNullOrWhiteSpace(metadata.colorMappingProfileGuid)
                ? null
                : AssetDatabase.GUIDToAssetPath(metadata.colorMappingProfileGuid);
            if (string.IsNullOrWhiteSpace(path)) path = metadata.colorMappingProfileAssetPath;
            return string.IsNullOrWhiteSpace(path)
                ? null
                : AssetDatabase.LoadAssetAtPath<VoxelColorMappingProfile>(path);
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
