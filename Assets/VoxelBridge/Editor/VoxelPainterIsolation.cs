using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    // One transient selection snapshot. Original grid indices remain valid for editing and history.
    internal sealed class VoxelPainterIsolation : IDisposable
    {
        private readonly VoxelGrid source;
        private readonly int[] cells;
        internal IReadOnlyCollection<int> Cells => cells;
        internal VoxelGrid Grid { get; }
        internal Mesh Mesh { get; private set; }

        internal VoxelPainterIsolation(VoxelGrid source, IReadOnlyCollection<int> selection, Action<float> progress = null)
        {
            if (source == null || !source.IsSemantic) throw new ArgumentException("A semantic grid is required.");
            if (selection == null || selection.Count == 0 || selection.Count > VoxelSurfaceEdit.MaximumSelection)
                throw new ArgumentException("Choose a non-empty selection within the selection limit.");
            VoxelSemanticMesher.ValidateGrid(source.Size, source.Origin, source.VoxelSize);
            cells = selection.Distinct().ToArray();
            foreach (int cell in cells)
                if (cell < 0 || cell >= source.Occupied.Length || !source.Occupied[cell])
                    throw new ArgumentException("The isolation contains an empty or invalid cell.");
            this.source = source;
            Grid = new VoxelGrid(source.Size, source.Origin, source.VoxelSize, true);
            foreach (int cell in cells) Grid.Occupied[cell] = true;
            Refresh(progress);
        }

        internal void Refresh(Action<float> progress = null)
        {
            foreach (int cell in cells) Grid.SemanticIds[cell] = source.SemanticIds[cell];
            // Cut faces and enclosed regions must remain inspectable inside the isolated volume.
            Mesh generated = VoxelSemanticMesher.Build(Grid, false, progress);
            generated.hideFlags = HideFlags.HideAndDontSave;
            generated.name = "Isolated Voxel Preview";
            if (Mesh != null) UnityEngine.Object.DestroyImmediate(Mesh);
            Mesh = generated;
        }

        public void Dispose()
        {
            if (Mesh != null) UnityEngine.Object.DestroyImmediate(Mesh);
            Mesh = null;
        }
    }
}
