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
        private readonly Vector2[] colors, surfaces;
        private readonly int[] vertices;

        internal VoxelPainterAppearancePreview(VoxelGrid grid, HashSet<int> selection, bool hideCavities, Action<float> progress = null)
        {
            Cells = selection.OrderBy(c => c).ToArray();
            Mesh = VoxelSemanticMesher.BuildSelectionPreview(grid, new HashSet<int>(Cells), hideCavities, progress);
            Mesh.hideFlags = HideFlags.HideAndDontSave;
            colors = Mesh.uv; surfaces = Mesh.uv4;
            var flags = Mesh.uv2;
            vertices = Enumerable.Range(0, flags.Length).Where(i => flags[i].x > .5f).ToArray();
        }

        internal void SetSurface(int id)
        {
            if (id < 0 || id > 255) throw new ArgumentOutOfRangeException(nameof(id));
            foreach (int i in vertices) surfaces[i].x = id;
            Mesh.uv4 = surfaces;
        }

        internal void SetColor(int id)
        {
            if (id < 0 || id > 255) throw new ArgumentOutOfRangeException(nameof(id));
            foreach (int i in vertices) colors[i].x = id;
            Mesh.uv = colors;
        }

        public void Dispose() { if (Mesh != null) UnityEngine.Object.DestroyImmediate(Mesh); Mesh = null; }
    }
}
