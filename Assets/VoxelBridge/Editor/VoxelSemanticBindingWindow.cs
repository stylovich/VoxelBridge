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
        [Serializable]
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

        [SerializeField] private List<BindingRow> rows = new();
        private int bindingRevision;
        private string loadedPath;
        private string status;
        private MessageType statusType = MessageType.Info;
        private bool hasUnsupportedIndexMap;

        [MenuItem("Tools/Voxel Bridge/Bind Semantic IDs")]
        private static void OpenWindow()
        {
            var window = GetWindow<VoxelSemanticBindingWindow>("VOX Semantic IDs");
            window.minSize = new Vector2(860f, 460f);
            window.UseSelectedVox();
            window.Show();
        }

        [MenuItem("Assets/Voxel Bridge/Bind Semantic IDs", false, 2050)]
        private static void OpenForSelection() => OpenWindow();

        [MenuItem("Assets/Voxel Bridge/Bind Semantic IDs", true)]
        private static bool ValidateOpenForSelection() =>
            IsVoxPath(AssetDatabase.GetAssetPath(Selection.activeObject));

        private void OnEnable()
        {
            Undo.undoRedoPerformed += OnUndoRedo;
            LoadCanonicalPalettes();
            if (voxAsset == null) UseSelectedVox();
            Reload();
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
            Undo.ClearUndo(this);
        }

        private void OnUndoRedo()
        {
            foreach (BindingRow row in rows) UpdateDistance(row);
            RefreshStatus();
            Repaint();
        }

        private void OnSelectionChange()
        {
            string path = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (!IsVoxPath(path)) return;
            if (path == loadedPath) return;
            voxAsset = Selection.activeObject;
            Reload();
            Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField(".vox Semantic Bindings", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Asigna un ColorID y un SurfaceID globales a cada slot utilizado. Un perfil de " +
                "mapeo limita los colores candidatos y propone la coincidencia perceptual más cercana.",
                MessageType.Info);

            EditorGUI.BeginChangeCheck();
            voxAsset = EditorGUILayout.ObjectField(".vox File", voxAsset,
                typeof(UnityEngine.Object), false);
            if (EditorGUI.EndChangeCheck()) Reload();

            EditorGUI.BeginChangeCheck();
            colorPalette = (VoxelColorPalette)EditorGUILayout.ObjectField(
                "Color Palette", colorPalette, typeof(VoxelColorPalette), false);
            surfacePalette = (VoxelSurfacePalette)EditorGUILayout.ObjectField(
                "Surface Palette", surfacePalette, typeof(VoxelSurfacePalette), false);
            if (EditorGUI.EndChangeCheck()) Reload();

            EditorGUI.BeginChangeCheck();
            mappingProfile = (VoxelColorMappingProfile)EditorGUILayout.ObjectField(
                new GUIContent("Mapping Profile",
                    "Selecciona los ColorIDs candidatos y los umbrales del mapeo OKLab."),
                mappingProfile, typeof(VoxelColorMappingProfile), false);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.ClearUndo(this);
                bindingRevision++;
                if (mappingProfile != null && mappingProfile.ColorPalette != null)
                    colorPalette = mappingProfile.ColorPalette;
                ApplyProfileToUnmapped();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Create Profile for This Palette")) CreateMappingProfile();
                using (new EditorGUI.DisabledScope(mappingProfile == null || rows.Count == 0))
                {
                    if (GUILayout.Button("Assign Unmapped Color Slots")) ApplyProfileToUnmapped();
                }
                if (mappingProfile != null && GUILayout.Button("Select Profile"))
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
            EditorGUILayout.HelpBox(
                "Edit > Undo / Redo permite deshacer y rehacer asignaciones pendientes (Ctrl+Z / Ctrl+Y). " +
                "Guardar o cambiar de archivo, paleta o perfil reinicia este historial; no revierte archivos guardados.",
                MessageType.Info);
            DrawBindings();

            bool profileValid = TryValidateSelectedProfile(out string profileError);
            bool complete = !hasUnsupportedIndexMap && profileValid &&
                            rows.All(row =>
                                colorPalette.TryGetColor(row.ColorId, out _) &&
                                surfacePalette.TryGetSurface(row.SurfaceId, out _));
            using (new EditorGUI.DisabledScope(!complete))
            {
                if (GUILayout.Button("Save Semantic Bindings", GUILayout.Height(30f)))
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
            string[] surfaceLabels = surfaces.Select(entry =>
                $"{entry.Id:D3} · {entry.DisplayName}").Prepend("Unassigned").ToArray();

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField($"Used Slots: {rows.Count}", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(138f);
                GUILayout.Label("Original Color", EditorStyles.miniBoldLabel, GUILayout.Width(88f));
                GUILayout.Label("Assigned ColorID", EditorStyles.miniBoldLabel, GUILayout.MinWidth(190f));
                GUILayout.Label("Distance", EditorStyles.miniBoldLabel, GUILayout.Width(76f));
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

                    VoxelColorDefinition selected = colors.FirstOrDefault(entry => entry.Id == row.ColorId);
                    string colorLabel = selected == null ? "Unassigned" : $"{selected.Id:D3} · {selected.DisplayName}";
                    Rect colorRect = GUILayoutUtility.GetRect(new GUIContent(colorLabel),
                        EditorStyles.popup, GUILayout.MinWidth(190f));
                    if (GUI.Button(colorRect, colorLabel, EditorStyles.popup))
                    {
                        int revision = bindingRevision;
                        int slot = row.Slot;
                        PopupWindow.Show(colorRect, new ColorSelector(colors, row.ColorId, row.SourceColor,
                            id =>
                            {
                                if (this != null && revision == bindingRevision) AssignColor(slot, id);
                            }));
                    }

                    GUILayout.Label(BuildDistanceLabel(row), GUILayout.Width(76f));

                    int surfaceIndex = Array.FindIndex(surfaces, entry => entry.Id == row.SurfaceId) + 1;
                    EditorGUI.BeginChangeCheck();
                    int selectedSurface = EditorGUILayout.Popup(
                        Mathf.Max(0, surfaceIndex), surfaceLabels, GUILayout.MinWidth(180f));
                    if (EditorGUI.EndChangeCheck())
                        AssignSurface(row.Slot, selectedSurface == 0 ? -1 : surfaces[selectedSurface - 1].Id);
                }
            }
            EditorGUILayout.EndScrollView();
        }

        internal void AssignColor(int slot, int colorId)
        {
            BindingRow row = rows.Find(entry => entry.Slot == slot);
            if (row == null || row.ColorId == colorId) return;
            if (colorId != -1 && (colorPalette == null || !colorPalette.TryGetColor(colorId, out _))) return;
            Undo.IncrementCurrentGroup();
            Undo.RecordObject(this, "Assign Voxel Color");
            row.ColorId = colorId;
            if (colorId < 0) row.SuggestedColorId = -1;
            UpdateDistance(row);
            RefreshStatus();
        }

        internal void AssignSurface(int slot, int surfaceId)
        {
            BindingRow row = rows.Find(entry => entry.Slot == slot);
            if (row == null || row.SurfaceId == surfaceId) return;
            if (surfaceId != -1 && (surfacePalette == null || !surfacePalette.TryGetSurface(surfaceId, out _))) return;
            Undo.IncrementCurrentGroup();
            Undo.RecordObject(this, "Assign Voxel Surface");
            row.SurfaceId = surfaceId;
            RefreshStatus();
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
                    SetStatus($"ColorID {row.ColorId} does not exist in the global palette.", MessageType.Error);
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
            if (!EditorUtility.DisplayDialog("Save Semantic Bindings",
                    "Se actualizará la paleta RGBA del .vox y su sidecar. Los slots con el mismo " +
                    "ColorID y SurfaceID se consolidarán sin cambiar la posición de los voxels. " +
                    "Es recomendable mantener ambos archivos bajo control de versiones." + risk,
                    "Save", "Cancel"))
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
            Undo.ClearUndo(this);
            bindingRevision++;
            string nextPath = AssetDatabase.GetAssetPath(voxAsset);
            bool assetChanged = !string.Equals(loadedPath, nextPath, StringComparison.Ordinal);
            loadedPath = nextPath;
            rows.Clear();
            hasUnsupportedIndexMap = false;
            if (voxAsset == null)
            {
                SetStatus("Select a .vox file generated by Voxel Bridge.", MessageType.Info);
                return;
            }
            if (!IsVoxPath(loadedPath))
            {
                SetStatus("The selected asset is not a .vox file.", MessageType.Error);
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
                    SetStatus("Assign the global color and surface palettes.", MessageType.Error);
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
                SetStatus("Could not read file: " + exception.Message, MessageType.Error);
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

            if (rows.Any(row => row.ColorId < 0))
            {
                Undo.IncrementCurrentGroup();
                Undo.RecordObject(this, "Map Unassigned Voxel Colors");
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
                error = "The mapping profile and the window use different color palettes.";
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
                SetStatus("All slots are mapped and ready for validation.", MessageType.Info);
            else if (unmapped == 0)
                SetStatus($"All slots are mapped; {warnings} match(es) exceed the warning threshold.",
                    MessageType.Warning);
            else
                SetStatus($"{unmapped} slot(s) have no ColorID. " +
                    $"{rejected} exceed the maximum automatic distance.", MessageType.Warning);
        }

        private void CreateMappingProfile()
        {
            if (colorPalette == null)
            {
                EditorUtility.DisplayDialog(
                    "Voxel Bridge", "Asigna primero la paleta global de colores.", "Close");
                return;
            }
            string path = EditorUtility.SaveFilePanelInProject(
                "Create Color Mapping Profile", "VoxelColorMappingProfile", "asset",
                "Selecciona una ubicación para el perfil.", VoxelPaletteAssetUtility.PaletteFolder);
            if (string.IsNullOrEmpty(path)) return;

            var profile = CreateInstance<VoxelColorMappingProfile>();
            profile.Initialize(colorPalette);
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            Undo.ClearUndo(this);
            bindingRevision++;
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

        private sealed class ColorSelector : PopupWindowContent
        {
            private const float RowHeight = 24f;
            private readonly VoxelColorDefinition[] colors;
            private readonly Action<int> select;
            private readonly Color32 sourceColor;
            private Vector2 listScroll;
            private int highlighted;

            public ColorSelector(VoxelColorDefinition[] colors, int selectedId,
                Color32 sourceColor, Action<int> select)
            {
                this.colors = colors;
                this.sourceColor = sourceColor;
                this.select = select;
                highlighted = Array.FindIndex(colors, entry => entry.Id == selectedId) + 1;
                listScroll.y = Mathf.Max(0, highlighted * RowHeight - 120f);
            }

            public override Vector2 GetWindowSize() => new(340f, 392f);

            public override void OnGUI(Rect rect)
            {
                Event current = Event.current;
                if (current.type == EventType.KeyDown)
                {
                    if (current.keyCode == KeyCode.Escape)
                    {
                        editorWindow.Close();
                        current.Use();
                        return;
                    }
                    if (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter)
                    {
                        Choose(highlighted);
                        current.Use();
                        return;
                    }
                    if (current.keyCode == KeyCode.DownArrow || current.keyCode == KeyCode.UpArrow)
                    {
                        highlighted = Mathf.Clamp(highlighted +
                            (current.keyCode == KeyCode.DownArrow ? 1 : -1), 0, colors.Length);
                        listScroll.y = Mathf.Clamp(listScroll.y,
                            (highlighted + 1) * RowHeight - 288f, highlighted * RowHeight);
                        current.Use();
                        editorWindow.Repaint();
                    }
                }

                GUILayout.Label("Select Color", EditorStyles.boldLabel);
                Rect viewport = GUILayoutUtility.GetRect(320f, 288f, GUILayout.ExpandWidth(true));
                listScroll = GUI.BeginScrollView(viewport, listScroll,
                    new Rect(0f, 0f, viewport.width - 16f, (colors.Length + 1) * RowHeight));
                for (int index = 0; index <= colors.Length; index++)
                {
                    Rect row = new(0f, index * RowHeight, viewport.width - 16f, RowHeight);
                    bool hovered = row.Contains(current.mousePosition) && viewport.Contains(
                        current.mousePosition - listScroll + viewport.position);
                    if (hovered && (current.type == EventType.MouseMove || current.type == EventType.MouseDown))
                    {
                        highlighted = index;
                        editorWindow.Repaint();
                    }
                    if (index == highlighted) EditorGUI.DrawRect(row, new Color(0.22f, 0.45f, 0.7f, 0.4f));
                    if (index > 0)
                        EditorGUI.DrawRect(new Rect(5f, row.y + 3f, 28f, 18f), colors[index - 1].Color);
                    GUI.Label(new Rect(40f, row.y, row.width - 40f, RowHeight),
                        index == 0 ? "Unassigned" : $"{colors[index - 1].Id:D3} · {colors[index - 1].DisplayName}");
                    if (hovered && current.type == EventType.MouseDown && current.button == 0)
                    {
                        Choose(index);
                        current.Use();
                        break;
                    }
                }
                GUI.EndScrollView();
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawSwatch(sourceColor, 42f);
                    GUILayout.Label("→", GUILayout.Width(16f));
                    if (highlighted > 0)
                    {
                        VoxelColorDefinition candidate = colors[highlighted - 1];
                        DrawSwatch(candidate.Color, 42f);
                        GUILayout.Label($"{candidate.Id:D3} · {candidate.DisplayName}\n" +
                            $"#{ColorUtility.ToHtmlStringRGBA(candidate.Color)}");
                    }
                    else GUILayout.Label("Unassigned");
                }
            }

            public override void OnOpen() => editorWindow.wantsMouseMove = true;

            private void Choose(int index)
            {
                select(index == 0 ? -1 : colors[index - 1].Id);
                editorWindow.Close();
            }
        }
    }
}
