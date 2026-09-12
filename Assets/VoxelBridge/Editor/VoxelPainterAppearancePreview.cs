using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    // Owns only temporary geometry; the edit buffer and its history remain untouched.
    internal sealed class VoxelPainterAppearancePreview : IDisposable
    {
        internal Mesh Mesh { get; private set; }
        internal int[] Cells { get; }
        private Vector2[] colors, surfaces;
        private int[] vertices;
        private readonly VoxelGrid source;
        private readonly VoxelSurfacePalette palette;
        private readonly bool hideCavities;
        private readonly Action<float> progress;
        private int selectionClass, colorOverride = -1;

        internal VoxelPainterAppearancePreview(VoxelGrid grid, HashSet<int> selection, bool hideCavities, Action<float> progress = null,
            VoxelSurfacePalette palette = null)
        {
            source = grid; this.palette = palette; this.hideCavities = hideCavities; this.progress = progress;
            Cells = selection.OrderBy(c => c).ToArray();
            var glass = VoxelSemanticMesher.GlassIds(palette);
            selectionClass = Cells.Select(c => VoxelSemanticMesher.IsGlass(grid, c, glass) ? 1 : 0).Distinct().Count() > 1
                ? -1 : Cells.Length > 0 && VoxelSemanticMesher.IsGlass(grid, Cells[0], glass) ? 1 : 0;
            Mesh = VoxelSemanticMesher.BuildSelectionPreview(grid, new HashSet<int>(Cells), hideCavities, progress, palette);
            Mesh.hideFlags = HideFlags.HideAndDontSave;
            CacheVertices();
        }

        private void CacheVertices()
        {
            colors = Mesh.uv; surfaces = Mesh.uv4;
            var flags = Mesh.uv2;
            vertices = Enumerable.Range(0, flags.Length).Where(i => flags[i].x > .5f).ToArray();
        }

        internal void SetSurface(int id)
        {
            if (id < 0 || id > 255) throw new ArgumentOutOfRangeException(nameof(id));
            if (palette != null)
            {
                if (!palette.TryGetSurface(id, out var surface) || !surface.SupportsVoxelRendering)
                    throw new ArgumentException("Choose a supported surface.", nameof(id));
                int targetClass = surface.RenderClass == VoxelSurfaceRenderClass.Transparent ? 1 : 0;
                if (selectionClass != targetClass)
                {
                    // A render-class change alters visibility and submesh ownership. Work on a temporary
                    // full grid; never mutate the edit buffer or merely relabel an opaque mesh as glass.
                    var grid = new VoxelGrid(source.Size, source.Origin, source.VoxelSize, true);
                    Array.Copy(source.Occupied, grid.Occupied, grid.Occupied.Length);
                    Array.Copy(source.SemanticIds, grid.SemanticIds, grid.SemanticIds.Length);
                    foreach (int cell in Cells) grid.SemanticIds[cell] = VoxelSemanticEncoding.Pack(
                        colorOverride >= 0 ? colorOverride : VoxelSemanticEncoding.ColorId(grid.SemanticIds[cell]), id);
                    var candidate = VoxelSemanticMesher.BuildSelectionPreview(grid, new HashSet<int>(Cells), hideCavities, progress, palette);
                    candidate.hideFlags = HideFlags.HideAndDontSave;
                    UnityEngine.Object.DestroyImmediate(Mesh); Mesh = candidate;
                    selectionClass = targetClass;
                    CacheVertices();
                    return;
                }
            }
            foreach (int i in vertices) surfaces[i].x = id;
            Mesh.uv4 = surfaces;
        }

        internal void SetColor(int id)
        {
            if (id < 0 || id > 255) throw new ArgumentOutOfRangeException(nameof(id));
            colorOverride = id;
            foreach (int i in vertices) colors[i].x = id;
            Mesh.uv = colors;
        }

        public void Dispose() { if (Mesh != null) UnityEngine.Object.DestroyImmediate(Mesh); Mesh = null; }
    }
}
