using System;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    // In-memory prediction only. No source, asset database, manifest or edit-history writes.
    internal sealed class VoxelPainterReductionPreview : IDisposable
    {
        internal VoxelGrid Reduced { get; }
        internal Mesh Mesh { get; private set; }
        internal Mesh LossMesh { get; private set; }
        internal byte[] ChangedFaces { get; }
        internal int ChangedCells { get; }
        private readonly VoxelGrid source;
        private readonly bool hideCavities;

        internal VoxelPainterReductionPreview(VoxelGrid source, float size, int padding, int chunkSize,
            bool hideCavities, Action<float> progress = null)
        {
            if (source == null || !source.IsSemantic || !float.IsFinite(size) || size <= source.VoxelSize)
                throw new ArgumentException("Preview requires a semantic draft and a coarser target size.");
            this.source = source; this.hideCavities = hideCavities;
            try
            {
                Reduced = VoxelGridDownsampler.Downsample(source, size, padding, chunkSize,
                    hideCavities, v => progress?.Invoke(v * .5f));
                ChangedFaces = FindChangedFaces(source, Reduced, hideCavities, v => progress?.Invoke(.5f + v * .25f));
                foreach (byte faces in ChangedFaces) if (faces != 0) ChangedCells++;
                Mesh = VoxelSemanticMesher.Build(Reduced, hideCavities, v => progress?.Invoke(.75f + v * .25f));
                Mesh.hideFlags = HideFlags.HideAndDontSave;
                progress?.Invoke(1);
            }
            catch { Dispose(); throw; }
        }

        internal static byte[] FindChangedFaces(VoxelGrid source, VoxelGrid reduced, bool hideCavities,
            Action<float> progress = null)
        {
            // Compare only contributions the real reducer regards as exposed and direction-compatible.
            // Buried fill, sealed cavities and lost silhouette are not material-loss predictions.
            var sourceExterior = hideCavities ? VoxelSemanticMesher.FindExterior(source, v => progress?.Invoke(v)) : null;
            var targetExterior = hideCavities ? VoxelSemanticMesher.FindExterior(reduced, v => progress?.Invoke(.2f + v)) : null;
            var changed = new byte[source.Occupied.Length];
            for (int i = 0; i < source.Occupied.Length; i++)
            {
                if ((i & 65535) == 0) progress?.Invoke(.4f + .6f * i / source.Occupied.Length);
                if (!source.Occupied[i]) continue;
                int target = VoxelGridDownsampler.MapTargetIndex(source, reduced, reduced.VoxelSize, i);
                if (source.SemanticIds[i] == reduced.SemanticIds[target]) continue;
                changed[i] = (byte)(VoxelGridDownsampler.ExposedFaces(source, i, sourceExterior) &
                    VoxelGridDownsampler.ExposedFaces(reduced, target, targetExterior));
            }
            progress?.Invoke(1);
            return changed;
        }

        internal void BuildLossMesh(Action<float> progress = null)
        {
            if (LossMesh != null) return;
            var candidate = VoxelSemanticMesher.BuildReductionLossPreview(source, ChangedFaces, hideCavities, progress);
            candidate.hideFlags = HideFlags.HideAndDontSave;
            LossMesh = candidate;
        }

        public void Dispose()
        {
            if (Mesh != null) UnityEngine.Object.DestroyImmediate(Mesh);
            if (LossMesh != null) UnityEngine.Object.DestroyImmediate(LossMesh);
            Mesh = null; LossMesh = null;
        }
    }
}
