using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace LocalModels.VoxelBridge
{
    internal static class VoxelSemanticMesher
    {
        // Per-volume safety limits shared by standalone and chunked production meshes.
        internal const int MaximumCells = 8_000_000;
        internal const int MaximumQuads = 500_000;

        internal static void ValidateGrid(Vector3Int size, Vector3 origin, float voxelSize)
        {
            if (size.x <= 0 || size.y <= 0 || size.z <= 0 ||
                size.x > MaximumCells || size.y > MaximumCells || size.z > MaximumCells ||
                (long)size.x * size.y > MaximumCells / size.z)
                throw new InvalidDataException($"Production meshing supports at most {MaximumCells:N0} grid cells. Use a smaller source or a coarser voxel size.");
            if (!float.IsFinite(voxelSize) || voxelSize <= 0 ||
                !Finite(origin) || !Finite(origin + (Vector3)size * voxelSize))
                throw new InvalidDataException("The grid scale or origin is invalid.");
        }

        private static bool Finite(Vector3 value) =>
            float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        // The caller owns and must destroy the returned mesh if it is not saved as an asset.
        public static Mesh Build(VoxelGrid grid, bool hideEnclosedCavities,
            Action<float> progress = null, int maximumQuads = MaximumQuads)
        {
            if (grid == null || !grid.IsSemantic)
                throw new ArgumentException("Meshing requires a semantic voxel grid.", nameof(grid));
            ValidateGrid(grid.Size, grid.Origin, grid.VoxelSize);
            if (maximumQuads < 1 || maximumQuads > MaximumQuads)
                throw new ArgumentOutOfRangeException(nameof(maximumQuads));
            bool[] exterior = hideEnclosedCavities ? FindExterior(grid, progress) : null;
            return BuildRegion(grid, exterior, Vector3Int.zero, grid.Size, progress, maximumQuads, false);
        }

        // The extra UV channel partitions selection boundaries for temporary authoring previews.
        internal static Mesh BuildSelectionPreview(VoxelGrid grid, HashSet<int> selection, bool hideEnclosedCavities,
            Action<float> progress = null)
        {
            if (grid == null || !grid.IsSemantic) throw new ArgumentException("A semantic grid is required.");
            ValidateGrid(grid.Size, grid.Origin, grid.VoxelSize);
            if (selection == null || selection.Count == 0 || selection.Count > VoxelSurfaceEdit.MaximumSelection)
                throw new ArgumentException("Choose a bounded, nonempty selection.");
            foreach (int cell in selection)
                if (cell < 0 || cell >= grid.Occupied.Length || !grid.Occupied[cell]) throw new ArgumentException("Invalid preview selection.");
            bool[] exterior = hideEnclosedCavities ? FindExterior(grid, progress) : null;
            return BuildRegion(grid, exterior, Vector3Int.zero, grid.Size, progress, MaximumQuads, false, selection);
        }

        internal static Mesh[] BuildChunks(VoxelGrid grid, int chunkSize, bool hideEnclosedCavities,
            Action<float> progress = null)
        {
            if (grid == null || !grid.IsSemantic) throw new ArgumentException("A semantic grid is required.");
            ValidateGrid(grid.Size, grid.Origin, grid.VoxelSize);
            if (chunkSize < 16 || chunkSize > 256) throw new ArgumentOutOfRangeException(nameof(chunkSize));
            bool[] exterior = hideEnclosedCavities ? FindExterior(grid, progress) : null;
            var meshes = new List<Mesh>();
            int quads = 0;
            try
            {
                for (int z = 0; z < grid.Size.z; z += chunkSize)
                for (int y = 0; y < grid.Size.y; y += chunkSize)
                for (int x = 0; x < grid.Size.x; x += chunkSize)
                {
                    var offset = new Vector3Int(x, y, z);
                    Vector3Int size = Vector3Int.Min(Vector3Int.one * chunkSize, grid.Size - offset);
                    Mesh mesh = BuildRegion(grid, exterior, offset, size, progress, MaximumQuads - quads, true);
                    mesh.name = $"Chunk_{x / chunkSize}_{y / chunkSize}_{z / chunkSize}";
                    meshes.Add(mesh);
                    quads += mesh.vertexCount / 4;
                }
                if (quads == 0) throw new InvalidDataException("The voxel grid contains no visible geometry.");
                return meshes.ToArray();
            }
            catch
            {
                foreach (Mesh mesh in meshes) UnityEngine.Object.DestroyImmediate(mesh);
                throw;
            }
        }

        private static Mesh BuildRegion(VoxelGrid grid, bool[] exterior, Vector3Int offset, Vector3Int size,
            Action<float> progress, int maximumQuads, bool allowEmpty, HashSet<int> selection = null)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var tangents = new List<Vector4>();
            var colors = new List<Vector2>();
            var surfaces = new List<Vector2>();
            var triangles = new List<int>();
            var selectedVertices = selection == null ? null : new List<Vector2>();
            int totalSlices = size.x + size.y + size.z + 3;
            int completedSlices = 0;

            for (int axis = 0; axis < 3; axis++)
            {
                int u = (axis + 1) % 3, v = (axis + 2) % 3;
                int width = size[u], height = size[v];
                var mask = new int[width * height];
                for (int slice = -1; slice < size[axis]; slice++)
                {
                    progress?.Invoke(0.2f + 0.8f * completedSlices++ / totalSlices);
                    var p = offset;
                    p[axis] += slice;
                    for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        p[u] = offset[u] + x; p[v] = offset[v] + y;
                        int a = p[axis] >= 0 ? grid.Index(p.x, p.y, p.z) : -1;
                        p[axis]++;
                        int b = p[axis] < grid.Size[axis] ? grid.Index(p.x, p.y, p.z) : -1;
                        p[axis]--;
                        bool solidA = a >= 0 && grid.Occupied[a];
                        bool solidB = b >= 0 && grid.Occupied[b];
                        int value = 0;
                        if (solidA != solidB && (solidA ? slice >= 0 : slice + 1 < size[axis]))
                        {
                            int air = solidA ? b : a;
                            if (exterior == null || air < 0 || exterior[air])
                            {
                                int cell = solidA ? a : b;
                                int selectedFlag = selection != null && selection.Contains(cell) ? 65536 : 0;
                                value = (grid.SemanticIds[cell] + selectedFlag + 1) * (solidA ? 1 : -1);
                            }
                        }
                        mask[x + width * y] = value;
                    }

                    for (int y = 0; y < height; y++)
                    for (int x = 0; x < width;)
                    {
                        int key = mask[x + width * y];
                        if (key == 0) { x++; continue; }
                        int w = 1;
                        while (x + w < width && mask[x + w + width * y] == key) w++;
                        int h = 1;
                        while (y + h < height)
                        {
                            int row = 0;
                            while (row < w && mask[x + row + width * (y + h)] == key) row++;
                            if (row != w) break;
                            h++;
                        }
                        if (vertices.Count / 4 >= maximumQuads)
                            throw new InvalidDataException($"The mesh exceeds the {maximumQuads:N0}-quad safety limit. No output was saved.");
                        p[axis] = offset[axis] + slice + 1; p[u] = offset[u] + x; p[v] = offset[v] + y;
                        Vector3 corner = grid.Origin + (Vector3)p * grid.VoxelSize;
                        Vector3 du = Vector3.zero, dv = Vector3.zero, normal = Vector3.zero;
                        du[u] = w * grid.VoxelSize; dv[v] = h * grid.VoxelSize;
                        int sign = key > 0 ? 1 : -1;
                        normal[axis] = sign;
                        int start = vertices.Count;
                        vertices.Add(corner); vertices.Add(corner + du);
                        vertices.Add(corner + du + dv); vertices.Add(corner + dv);
                        ushort semantic = (ushort)((Math.Abs(key) - 1) & 65535);
                        Vector4 tangent = Vector4.zero;
                        tangent[u] = 1; tangent.w = sign;
                        for (int i = 0; i < 4; i++)
                        {
                            normals.Add(normal); tangents.Add(tangent);
                            colors.Add(new Vector2(VoxelSemanticEncoding.ColorId(semantic), 0));
                            surfaces.Add(new Vector2(VoxelSemanticEncoding.SurfaceId(semantic), 0));
                            selectedVertices?.Add(new Vector2((Math.Abs(key) - 1 & 65536) != 0 ? 1 : 0, 0));
                        }
                        triangles.Add(start);
                        triangles.Add(start + (sign > 0 ? 1 : 2));
                        triangles.Add(start + (sign > 0 ? 2 : 1));
                        triangles.Add(start);
                        triangles.Add(start + (sign > 0 ? 2 : 3));
                        triangles.Add(start + (sign > 0 ? 3 : 2));
                        for (int j = 0; j < h; j++)
                        for (int i = 0; i < w; i++) mask[x + i + width * (y + j)] = 0;
                        x += w;
                    }
                }
            }
            if (vertices.Count == 0 && !allowEmpty) throw new InvalidDataException("The voxel grid contains no visible geometry.");
            var mesh = new Mesh { name = "SemanticLOD0", indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            try
            {
                mesh.SetVertices(vertices); mesh.SetNormals(normals); mesh.SetTangents(tangents);
                mesh.SetUVs(0, colors); mesh.SetUVs(3, surfaces);
                if (selectedVertices != null) mesh.SetUVs(1, selectedVertices);
                mesh.SetTriangles(triangles, 0);
                mesh.RecalculateBounds();
                return mesh;
            }
            catch { UnityEngine.Object.DestroyImmediate(mesh); throw; }
        }

        private static bool[] FindExterior(VoxelGrid grid, Action<float> progress)
        {
            int count = grid.Occupied.Length;
            var exterior = new bool[count];
            var queue = new int[count];
            int tail = 0;
            void Visit(int index)
            {
                if (grid.Occupied[index] || exterior[index]) return;
                exterior[index] = true;
                queue[tail++] = index;
            }
            Vector3Int size = grid.Size;
            for (int z = 0; z < size.z; z++)
            {
                progress?.Invoke(0);
                for (int y = 0; y < size.y; y++)
                { Visit(grid.Index(0, y, z)); Visit(grid.Index(size.x - 1, y, z)); }
                for (int x = 0; x < size.x; x++)
                { Visit(grid.Index(x, 0, z)); Visit(grid.Index(x, size.y - 1, z)); }
            }
            for (int y = 0; y < size.y; y++)
            for (int x = 0; x < size.x; x++)
            { Visit(grid.Index(x, y, 0)); Visit(grid.Index(x, y, size.z - 1)); }
            for (int head = 0; head < tail; head++)
            {
                if ((head & 65535) == 0) progress?.Invoke(0.2f * head / count);
                int index = queue[head];
                grid.Coordinates(index, out int x, out int y, out int z);
                if (x > 0) Visit(index - 1);
                if (x + 1 < size.x) Visit(index + 1);
                if (y > 0) Visit(index - size.x);
                if (y + 1 < size.y) Visit(index + size.x);
                if (z > 0) Visit(index - size.x * size.y);
                if (z + 1 < size.z) Visit(index + size.x * size.y);
            }
            return exterior;
        }
    }
}
