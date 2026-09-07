using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    // Temporary face geometry only: no sampling, hidden shared faces, or source mutations.
    internal static class VoxelSelectionOverlay
    {
        internal const string ShaderPath = "Assets/VoxelBridge/Editor/Shaders/VoxelSelectionOverlay.shader";
        private const int FacesPerMesh = 16383;

        internal static List<Mesh> Build(VoxelGrid grid, IReadOnlyCollection<int> cells)
        {
            if (cells.Count > VoxelSurfaceEdit.MaximumSelection) throw new ArgumentException("Selection limit exceeded.");
            var result = new List<Mesh>();
            var vertices = new List<Vector3>();
            var uv = new List<Vector2>();
            var indices = new List<int>();
            try
            {
                foreach (int cell in cells)
                {
                    if (cell < 0 || cell >= grid.Occupied.Length || !grid.Occupied[cell]) throw new ArgumentException("Invalid selected cell.");
                    grid.Coordinates(cell, out int x, out int y, out int z);
                    var position = new Vector3Int(x, y, z);
                    for (int axis = 0; axis < 3; axis++) for (int side = -1; side <= 1; side += 2)
                    {
                        var neighbor = position; neighbor[axis] += side;
                        if (neighbor.x >= 0 && neighbor.y >= 0 && neighbor.z >= 0 &&
                            neighbor.x < grid.Size.x && neighbor.y < grid.Size.y && neighbor.z < grid.Size.z &&
                            grid.Occupied[grid.Index(neighbor.x, neighbor.y, neighbor.z)]) continue;
                        Vector3 origin = grid.Origin + (Vector3)position * grid.VoxelSize;
                        origin[axis] += ((side > 0 ? 1 : 0) + side * .0005f) * grid.VoxelSize;
                        Vector3 u = Vector3.zero, v = Vector3.zero;
                        u[(axis + 1) % 3] = grid.VoxelSize; v[(axis + 2) % 3] = grid.VoxelSize;
                        int start = vertices.Count;
                        vertices.Add(origin); vertices.Add(origin + u); vertices.Add(origin + u + v); vertices.Add(origin + v);
                        uv.Add(Vector2.zero); uv.Add(Vector2.right); uv.Add(Vector2.one); uv.Add(Vector2.up);
                        indices.Add(start); indices.Add(start + 1); indices.Add(start + 2);
                        indices.Add(start); indices.Add(start + 2); indices.Add(start + 3);
                        if (vertices.Count == FacesPerMesh * 4) Flush();
                    }
                }
                Flush();
                return result;
            }
            catch { Destroy(result); throw; }

            void Flush()
            {
                if (vertices.Count == 0) return;
                var mesh = new Mesh { name = "Voxel Selection Faces", hideFlags = HideFlags.HideAndDontSave };
                result.Add(mesh);
                mesh.SetVertices(vertices); mesh.SetUVs(0, uv); mesh.SetTriangles(indices, 0);
                mesh.UploadMeshData(false);
                vertices.Clear(); uv.Clear(); indices.Clear();
            }
        }

        internal static void Destroy(List<Mesh> meshes)
        {
            foreach (var mesh in meshes) if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
            meshes.Clear();
        }
    }
}
