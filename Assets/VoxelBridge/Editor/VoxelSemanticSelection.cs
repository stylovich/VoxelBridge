using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    internal enum VoxelSelectionMatch { ColorID, SurfaceID, SimilarColor }

    internal static class VoxelSemanticSelection
    {
        // Every yielded value accounts for one visited cell; -1 means no match.
        // Similarity is always measured against the seed, never the last neighbor.
        internal static IEnumerator<int> Create(VoxelGrid grid, Camera camera, VoxelColorPalette colors,
            int seed, VoxelSelectionMatch match, float tolerance, bool connected, bool visibleOnly)
        {
            if (grid == null || grid.SemanticIds == null || grid.SemanticIds.Length != grid.Occupied.Length)
                throw new ArgumentException("A semantic grid is required.");
            if (seed < 0 || seed >= grid.Occupied.Length || !grid.Occupied[seed]) throw new ArgumentException("Choose an occupied seed voxel.");
            if (!Enum.IsDefined(typeof(VoxelSelectionMatch), match) || !float.IsFinite(tolerance) || tolerance < 0 || tolerance > 100)
                throw new ArgumentException("Invalid matching settings.");
            if (visibleOnly && camera == null) throw new ArgumentNullException(nameof(camera));
            ushort pair = grid.SemanticIds[seed];
            int colorId = VoxelSemanticEncoding.ColorId(pair), surfaceId = VoxelSemanticEncoding.SurfaceId(pair);
            var allowedColors = new bool[256];
            allowedColors[colorId] = true;
            if (match == VoxelSelectionMatch.SimilarColor)
            {
                if (colors == null || !colors.TryValidate(out _) || !colors.TryGetColor(colorId, out Color32 seedColor))
                    throw new ArgumentException("A valid color palette containing the seed color is required.");
                foreach (var color in colors.Entries)
                    allowedColors[color.Id] = VoxelColorPerceptualMatcher.Distance(seedColor, color.Color) <= tolerance;
            }
            return connected ? Connected().GetEnumerator() : All().GetEnumerator();

            bool Matches(int cell)
            {
                if (!grid.Occupied[cell]) return false;
                ushort candidate = grid.SemanticIds[cell];
                bool matches = match == VoxelSelectionMatch.SurfaceID
                    ? VoxelSemanticEncoding.SurfaceId(candidate) == surfaceId
                    : allowedColors[VoxelSemanticEncoding.ColorId(candidate)];
                return matches && (!visibleOnly || IsVisible(grid, camera, cell));
            }

            IEnumerable<int> All()
            {
                for (int cell = 0; cell < grid.Occupied.Length; cell++) yield return Matches(cell) ? cell : -1;
            }

            IEnumerable<int> Connected()
            {
                var visited = new BitArray(grid.Occupied.Length);
                var queue = new Queue<int>(); queue.Enqueue(seed); visited[seed] = true;
                while (queue.Count > 0)
                {
                    int cell = queue.Dequeue();
                    if (!Matches(cell)) { yield return -1; continue; }
                    yield return cell;
                    grid.Coordinates(cell, out int x, out int y, out int z);
                    var point = new Vector3Int(x, y, z);
                    for (int axis = 0; axis < 3; axis++) for (int direction = -1; direction <= 1; direction += 2)
                    {
                        var next = point; next[axis] += direction;
                        if (!Inside(grid, next)) continue;
                        int index = grid.Index(next.x, next.y, next.z);
                        if (visited[index]) continue;
                        visited[index] = true; queue.Enqueue(index);
                    }
                }
            }
        }

        internal static bool IsVisible(VoxelGrid grid, Camera camera, int cell)
        {
            if (!grid.Occupied[cell]) return false;
            grid.Coordinates(cell, out int x, out int y, out int z);
            var point = new Vector3Int(x, y, z);
            Vector3 center = grid.Origin + ((Vector3)point + Vector3.one * .5f) * grid.VoxelSize;
            for (int axis = 0; axis < 3; axis++) for (int side = -1; side <= 1; side += 2)
            {
                var neighbor = point; neighbor[axis] += side;
                if (Inside(grid, neighbor) && grid.Occupied[grid.Index(neighbor.x, neighbor.y, neighbor.z)]) continue;
                Vector3 normal = Vector3.zero; normal[axis] = side;
                Vector3 face = center + normal * (.5f * grid.VoxelSize);
                Vector3 towardCamera = camera.orthographic ? -camera.transform.forward : camera.transform.position - face;
                if (Vector3.Dot(normal, towardCamera) <= 0) continue;
                Vector3 projected = camera.WorldToViewportPoint(face);
                if (projected.z < camera.nearClipPlane || projected.z > camera.farClipPlane ||
                    projected.x < 0 || projected.x > 1 || projected.y < 0 || projected.y > 1) continue;
                if (VoxelSurfaceEdit.Pick(grid, camera.ViewportPointToRay(projected), out int hit) && hit == cell) return true;
            }
            return false;
        }

        private static bool Inside(VoxelGrid grid, Vector3Int p) => p.x >= 0 && p.y >= 0 && p.z >= 0 &&
            p.x < grid.Size.x && p.y < grid.Size.y && p.z < grid.Size.z;
    }
}
