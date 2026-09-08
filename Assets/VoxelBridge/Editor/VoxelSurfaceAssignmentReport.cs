using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    [Serializable]
    internal sealed class VoxelSurfaceAssignmentReport
    {
        [Serializable] internal sealed class Entry { public string decision; public int surfaceId; public int cells; }
        public int formatVersion = 1;
        public string scope = "Conversion snapshot: final surface cells before interior fill. Later VOX edits are not reflected.";
        public string sourceName;
        public string mappingFingerprint;
        public int[] candidateSurfaceIds;
        public float maximumDistance, minimumSeparation;
        public int sampledCells, filledInteriorCells;
        public List<Entry> entries = new();
        public string[] warnings;

        internal static VoxelSurfaceAssignmentReport Create(VoxelGrid grid, VoxelSurfaceDecision[] decisions,
            VoxelConversionProfile profile, string sourceName, IEnumerable<string> warnings)
        {
            var report = new VoxelSurfaceAssignmentReport
            {
                sourceName = sourceName, mappingFingerprint = VoxelConversionProfile.Fingerprint(profile),
                candidateSurfaceIds = profile.surfaceMapping.candidateSurfaceIds.OrderBy(id => id).ToArray(),
                maximumDistance = profile.surfaceMapping.maximumDistance, minimumSeparation = profile.surfaceMapping.minimumSeparation,
                warnings = warnings.OrderBy(value => value, StringComparer.Ordinal).ToArray()
            };
            var counts = new Dictionary<int, int>();
            for (int i = 0; i < grid.Occupied.Length; i++)
            {
                if (!grid.Occupied[i]) continue;
                report.sampledCells++;
                int key = (int)decisions[i] * 256 + VoxelSemanticEncoding.SurfaceId(grid.SemanticIds[i]);
                counts.TryGetValue(key, out int count); counts[key] = count + 1;
            }
            foreach (var pair in counts.OrderBy(pair => pair.Key)) report.entries.Add(new Entry
            { decision = ((VoxelSurfaceDecision)(pair.Key / 256)).ToString(), surfaceId = pair.Key % 256, cells = pair.Value });
            return report;
        }

        internal string Summary => string.Join(", ", entries.GroupBy(e => e.decision).Select(g => $"{g.Key}: {g.Sum(e => e.cells):N0}"));
    }
}
