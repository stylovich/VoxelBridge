using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    internal sealed partial class VoxelSurfacePainterWindow
    {
        [SerializeField] private string colorSearch = "";
        [SerializeField] private Vector2 sidebarScroll;
        private VoxelGrid legendGrid;
        private int legendRevision = -1;
        private int[] legendIds = Array.Empty<int>();
        private float SidebarWidth => Mathf.Clamp(position.width * .24f, 208, 272);

        internal static int ColorGridColumns(float width) => Mathf.Max(1, Mathf.FloorToInt(width / 24));

        internal static VoxelColorDefinition[] FilterColorChoices(VoxelColorDefinition[] colors, string query)
        {
            query = (query ?? "").Trim();
            return colors.Where(c => c != null && (query.Length == 0 || c.Id.ToString("000").Contains(query) ||
                (c.DisplayName ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).OrderBy(c => c.Id).ToArray();
        }

        internal static int[] UsedSurfaceIds(VoxelGrid grid)
        {
            if (grid == null || !grid.IsSemantic) throw new ArgumentException("A semantic grid is required.");
            var seen = new bool[256];
            for (int i = 0; i < grid.Occupied.Length; i++)
                if (grid.Occupied[i]) seen[VoxelSemanticEncoding.SurfaceId(grid.SemanticIds[i])] = true;
            return Enumerable.Range(0, seen.Length).Where(i => seen[i]).ToArray();
        }

        private void DrawSidebar()
        {
            if (editColor)
            {
                GUILayout.Label("Color Palette", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(new GUIContent("Search", "Filtrar por nombre o ColorID, sin cambiar la selección de voxels."), GUILayout.Width(42));
                    string search = EditorGUILayout.TextField(colorSearch);
                    if (search != colorSearch) { colorSearch = search; sidebarScroll = Vector2.zero; }
                }
            }
            sidebarScroll = EditorGUILayout.BeginScrollView(sidebarScroll, false, false);
            try
            {
                if (viewMode == VoxelPainterViewMode.SurfaceID && appearancePreview == null) DrawSurfaceLegend();
                if (editColor) DrawColorGrid();
            }
            finally { EditorGUILayout.EndScrollView(); }
        }

        private void DrawColorGrid()
        {
            VoxelColorDefinition[] choices;
            try { choices = FilterColorChoices(edit.AllowedColors(), colorSearch); }
            catch (Exception exception) { EditorGUILayout.HelpBox(exception.Message, MessageType.Warning); return; }
            if (choices.Length == 0) { GUILayout.Label("No matching colors", EditorStyles.miniLabel); return; }
            int columns = ColorGridColumns(SidebarWidth - 28);
            int rows = (choices.Length + columns - 1) / columns;
            Rect grid = GUILayoutUtility.GetRect(columns * 24, rows * 24, GUILayout.ExpandWidth(false));
            for (int i = 0; i < choices.Length; i++)
            {
                var entry = choices[i];
                var cell = new Rect(grid.x + i % columns * 24, grid.y + i / columns * 24, 23, 23);
                EditorGUI.DrawRect(cell, entry.Id == colorId ? Color.white : new Color(.12f, .12f, .12f));
                edit.Colors.TryGetColor(entry.Id, out var color);
                EditorGUI.DrawRect(new Rect(cell.x + 3, cell.y + 3, 17, 17), color);
                if (GUI.Button(cell, new GUIContent("", $"{entry.Id:000} · {entry.DisplayName}\n#{color.r:X2}{color.g:X2}{color.b:X2}"), GUIStyle.none))
                {
                    colorId = entry.Id;
                    GUI.FocusControl(null);
                    Run(UpdateAppearanceCandidate);
                }
            }
            GUILayout.Label($"{choices.Length} colors · ColorID {colorId:000}", EditorStyles.miniLabel);
        }

        private void DrawSurfaceLegend()
        {
            GUILayout.Space(4);
            GUILayout.Label("SurfaceID Legend", EditorStyles.boldLabel);
            GUILayout.Label(reductionActive ? (reductionOriginal || ReductionStale ? "Full draft (includes hidden)" : "Reduced result (includes hidden)") :
                isolation == null ? "Used in model (includes hidden)" : "Used in isolated group", EditorStyles.miniLabel);
            var grid = ViewGrid;
            if (!ReferenceEquals(legendGrid, grid) || legendRevision != edit.SurfaceRevision)
            { legendIds = UsedSurfaceIds(grid); legendGrid = grid; legendRevision = edit.SurfaceRevision; }
            foreach (int id in legendIds)
            {
                if (!edit.Surfaces.TryGetSurface(id, out var surface)) continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    Rect swatch = GUILayoutUtility.GetRect(18, 18, GUILayout.Width(18));
                    EditorGUI.DrawRect(swatch, DiagnosticSurfaceColor(id));
                    GUILayout.Label(new GUIContent($"{id:000} · {surface.DisplayName}",
                        $"Metallic {surface.Metallic:0.##} · Smoothness {surface.Smoothness:0.##} · Emission {surface.Emission:0.##}\nColor de diagnóstico, no ColorID."), EditorStyles.miniLabel);
                }
            }
        }
    }
}
