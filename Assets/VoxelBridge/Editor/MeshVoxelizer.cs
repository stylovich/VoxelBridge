using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    internal sealed class VoxelizationSettings
    {
        public int Resolution = 64;
        public float VoxelSize;
        public int ChunkCellSize = 256;
        public int Padding = 1;
        public bool FillInterior = true;
        public bool IncludeInactiveObjects = true;
        public VoxelColorMode ColorMode = VoxelColorMode.MaterialAndTexture;
        public Color32 SingleColor = new Color32(180, 180, 180, 255);
        public float AlphaCutoff = 0.1f;
    }

    internal sealed class VoxelizationResult
    {
        public VoxelGrid Grid;
        public Bounds SourceBounds;
        public int TriangleCount;
    }

    internal static class MeshVoxelizer
    {
        private sealed class MeshSource
        {
            public Vector3[] Vertices;
            public Vector2[] Uvs;
            public Mesh Mesh;
            public Material[] Materials;
            public bool OwnsMesh;
        }

        public static VoxelizationResult Voxelize(
            UnityEngine.Object source,
            VoxelizationSettings settings,
            Func<float, string, bool> cancelProgress = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            settings.Padding = Mathf.Clamp(settings.Padding, 0, 8);
            bool physicalSizeMode = settings.VoxelSize > 0f;
            if (!physicalSizeMode)
                settings.Resolution = Mathf.Clamp(settings.Resolution, 8, 256);
            if (!physicalSizeMode && settings.Resolution <= settings.Padding * 2)
                throw new ArgumentException("La resolución debe ser mayor que el padding de ambos lados.");

            List<MeshSource> sources = ExtractMeshes(source, settings.IncludeInactiveObjects);
            if (sources.Count == 0)
                throw new InvalidOperationException("El objeto seleccionado no contiene MeshFilter ni SkinnedMeshRenderer.");

            try
            {
                Bounds bounds = CalculateBounds(sources);
                float longest = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
                if (longest <= 1e-6f)
                    throw new InvalidOperationException("El modelo no tiene volumen utilizable.");

                float voxelSize;
                Vector3Int size;
                Vector3 origin;
                if (physicalSizeMode)
                {
                    VoxelGridPlan plan = VoxelGridPlanner.Create(
                        bounds, settings.VoxelSize, settings.Padding, settings.ChunkCellSize);
                    voxelSize = plan.VoxelSize;
                    size = plan.Size;
                    origin = plan.Origin;
                }
                else
                {
                    int interiorResolution = settings.Resolution - settings.Padding * 2;
                    voxelSize = longest / interiorResolution;
                    size = new Vector3Int(
                        Mathf.Clamp(Mathf.CeilToInt(bounds.size.x / voxelSize - 1e-5f) + settings.Padding * 2, 1, 256),
                        Mathf.Clamp(Mathf.CeilToInt(bounds.size.y / voxelSize - 1e-5f) + settings.Padding * 2, 1, 256),
                        Mathf.Clamp(Mathf.CeilToInt(bounds.size.z / voxelSize - 1e-5f) + settings.Padding * 2, 1, 256));
                    origin = bounds.min - Vector3.one * (settings.Padding * voxelSize);
                }
                var grid = new VoxelGrid(size, origin, voxelSize);
                var bestDistances = new float[grid.Occupied.Length];
                for (int i = 0; i < bestDistances.Length; i++) bestDistances[i] = float.PositiveInfinity;

                int triangleTotal = 0;
                foreach (MeshSource meshSource in sources)
                    for (int submesh = 0; submesh < meshSource.Mesh.subMeshCount; submesh++)
                        triangleTotal += (int)(meshSource.Mesh.GetIndexCount(submesh) / 3);

                int triangleDone = 0;
                using (var samplers = new MaterialSamplerCache(settings))
                {
                    foreach (MeshSource meshSource in sources)
                    {
                        for (int submesh = 0; submesh < meshSource.Mesh.subMeshCount; submesh++)
                        {
                            int[] triangles = meshSource.Mesh.GetTriangles(submesh);
                            Material material = meshSource.Materials != null && submesh < meshSource.Materials.Length
                                ? meshSource.Materials[submesh]
                                : null;
                            MaterialSampler sampler = samplers.Get(material);
                            for (int t = 0; t < triangles.Length; t += 3)
                            {
                                if ((triangleDone & 127) == 0 && cancelProgress != null &&
                                    cancelProgress((float)triangleDone / Mathf.Max(1, triangleTotal),
                                        $"Voxelizando triángulo {triangleDone:N0} de {triangleTotal:N0}"))
                                    throw new OperationCanceledException("Voxelización cancelada.");

                                int i0 = triangles[t], i1 = triangles[t + 1], i2 = triangles[t + 2];
                                Vector3 a = meshSource.Vertices[i0];
                                Vector3 b = meshSource.Vertices[i1];
                                Vector3 c = meshSource.Vertices[i2];
                                Vector2 uv0 = i0 < meshSource.Uvs.Length ? meshSource.Uvs[i0] : Vector2.zero;
                                Vector2 uv1 = i1 < meshSource.Uvs.Length ? meshSource.Uvs[i1] : Vector2.zero;
                                Vector2 uv2 = i2 < meshSource.Uvs.Length ? meshSource.Uvs[i2] : Vector2.zero;
                                RasterizeTriangle(grid, bestDistances, a, b, c, uv0, uv1, uv2,
                                    sampler, settings.AlphaCutoff);
                                triangleDone++;
                            }
                        }
                    }
                }
                bestDistances = null;

                if (grid.CountOccupied() == 0)
                    throw new InvalidOperationException("No se generaron vóxeles. Prueba una resolución mayor o revisa la transparencia del material.");

                if (settings.FillInterior)
                {
                    if (grid.Occupied.LongLength >= 8_000_000)
                        GC.Collect();
                    if (cancelProgress != null && cancelProgress(0.94f, "Rellenando el interior"))
                        throw new OperationCanceledException("Voxelización cancelada.");
                    FillInterior(grid);
                }

                cancelProgress?.Invoke(1f, "Voxelización terminada");
                return new VoxelizationResult { Grid = grid, SourceBounds = bounds, TriangleCount = triangleTotal };
            }
            finally
            {
                foreach (MeshSource meshSource in sources)
                    if (meshSource.OwnsMesh && meshSource.Mesh != null)
                        UnityEngine.Object.DestroyImmediate(meshSource.Mesh);
            }
        }

        internal static Bounds GetSourceBounds(
            UnityEngine.Object source, bool includeInactiveObjects = true)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            List<MeshSource> sources = ExtractMeshes(source, includeInactiveObjects);
            try
            {
                if (sources.Count == 0)
                    throw new InvalidOperationException("El objeto seleccionado no contiene mallas.");
                return CalculateBounds(sources);
            }
            finally
            {
                foreach (MeshSource meshSource in sources)
                    if (meshSource.OwnsMesh && meshSource.Mesh != null)
                        UnityEngine.Object.DestroyImmediate(meshSource.Mesh);
            }
        }

        private static List<MeshSource> ExtractMeshes(
            UnityEngine.Object source, bool includeInactiveObjects)
        {
            var result = new List<MeshSource>();
            if (source is Mesh mesh)
            {
                result.Add(CreateMeshSource(mesh, Matrix4x4.identity, null, false));
                return result;
            }

            if (!(source is GameObject root))
                throw new ArgumentException("Selecciona un GameObject, prefab, FBX/OBJ o Mesh.");

            bool isAsset = AssetDatabase.Contains(root);
            GameObject workingRoot = isAsset ? UnityEngine.Object.Instantiate(root) : root;
            if (isAsset)
            {
                workingRoot.hideFlags = HideFlags.HideAndDontSave;
                workingRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            }

            try
            {
                Matrix4x4 toRoot = workingRoot.transform.worldToLocalMatrix;
                foreach (MeshFilter filter in workingRoot.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh == null ||
                        (!includeInactiveObjects &&
                         !IsActiveWithinRoot(workingRoot.transform, filter.transform)))
                        continue;
                    var renderer = filter.GetComponent<MeshRenderer>();
                    result.Add(CreateMeshSource(filter.sharedMesh, toRoot * filter.transform.localToWorldMatrix,
                        renderer != null ? renderer.sharedMaterials : null, false));
                }

                foreach (SkinnedMeshRenderer renderer in workingRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (renderer.sharedMesh == null ||
                        (!includeInactiveObjects &&
                         !IsActiveWithinRoot(workingRoot.transform, renderer.transform)))
                        continue;
                    var baked = new Mesh { name = renderer.sharedMesh.name + "_VoxelBake" };
                    renderer.BakeMesh(baked);
                    result.Add(CreateMeshSource(baked, toRoot * renderer.transform.localToWorldMatrix,
                        renderer.sharedMaterials, true));
                }
            }
            finally
            {
                if (isAsset) UnityEngine.Object.DestroyImmediate(workingRoot);
            }
            return result;
        }

        internal static bool IsActiveWithinRoot(Transform root, Transform current)
        {
            if (root == null || current == null) return false;
            while (current != null)
            {
                if (!current.gameObject.activeSelf) return false;
                if (current == root) return true;
                current = current.parent;
            }
            return false;
        }

        private static MeshSource CreateMeshSource(Mesh mesh, Matrix4x4 transform, Material[] materials, bool ownsMesh)
        {
            Vector3[] vertices;
            Vector2[] uvs;
            try
            {
                vertices = mesh.vertices;
                uvs = mesh.uv;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"No se pudo leer la malla '{mesh.name}'. Activa Read/Write en sus Import Settings.", exception);
            }

            for (int i = 0; i < vertices.Length; i++) vertices[i] = transform.MultiplyPoint3x4(vertices[i]);
            return new MeshSource
            {
                Mesh = mesh,
                Vertices = vertices,
                Uvs = uvs ?? Array.Empty<Vector2>(),
                Materials = materials,
                OwnsMesh = ownsMesh
            };
        }

        private static Bounds CalculateBounds(List<MeshSource> sources)
        {
            bool initialized = false;
            Bounds bounds = default;
            foreach (MeshSource source in sources)
            {
                foreach (Vector3 vertex in source.Vertices)
                {
                    if (!initialized) { bounds = new Bounds(vertex, Vector3.zero); initialized = true; }
                    else bounds.Encapsulate(vertex);
                }
            }
            if (!initialized) throw new InvalidOperationException("Las mallas seleccionadas no contienen vértices.");
            return bounds;
        }

        private static void RasterizeTriangle(
            VoxelGrid grid, float[] bestDistances,
            Vector3 a, Vector3 b, Vector3 c,
            Vector2 uv0, Vector2 uv1, Vector2 uv2,
            MaterialSampler sampler, float alphaCutoff)
        {
            Vector3 min = Vector3.Min(a, Vector3.Min(b, c));
            Vector3 max = Vector3.Max(a, Vector3.Max(b, c));
            int minX = LowerCell(min.x, max.x, grid.Origin.x, grid.VoxelSize, grid.Size.x);
            int minY = LowerCell(min.y, max.y, grid.Origin.y, grid.VoxelSize, grid.Size.y);
            int minZ = LowerCell(min.z, max.z, grid.Origin.z, grid.VoxelSize, grid.Size.z);
            int maxX = UpperCell(min.x, max.x, grid.Origin.x, grid.VoxelSize, grid.Size.x);
            int maxY = UpperCell(min.y, max.y, grid.Origin.y, grid.VoxelSize, grid.Size.y);
            int maxZ = UpperCell(min.z, max.z, grid.Origin.z, grid.VoxelSize, grid.Size.z);
            Vector3 half = Vector3.one * (grid.VoxelSize * 0.5001f);

            for (int z = minZ; z <= maxZ; z++)
            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                Vector3 center = grid.Origin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * grid.VoxelSize;
                if (!TriangleIntersectsBox(a, b, c, center, half)) continue;
                Vector3 closest = ClosestPoint(a, b, c, center, out Vector3 barycentric);
                float distance = (closest - center).sqrMagnitude;
                int index = grid.Index(x, y, z);
                if (distance >= bestDistances[index]) continue;
                Vector2 uv = uv0 * barycentric.x + uv1 * barycentric.y + uv2 * barycentric.z;
                Color32 color = sampler.Sample(uv);
                if (color.a / 255f < alphaCutoff) continue;
                color.a = 255;
                grid.Occupied[index] = true;
                grid.Colors[index] = color;
                bestDistances[index] = distance;
            }
        }

        private static int LowerCell(float triangleMin, float triangleMax, float origin, float voxelSize, int gridSize)
        {
            float raw = (triangleMin - origin) / voxelSize;
            bool flat = triangleMax - triangleMin <= voxelSize * 1e-6f;
            float gridCenter = origin + gridSize * voxelSize * 0.5f;
            if (flat && triangleMin > gridCenter) raw -= 1e-5f;
            return Mathf.Clamp(Mathf.FloorToInt(raw), 0, gridSize - 1);
        }

        private static int UpperCell(float triangleMin, float triangleMax, float origin, float voxelSize, int gridSize)
        {
            // A non-flat upper bound is half-open. For a flat boundary face, move only faces
            // on the positive half inward; negative faces already fall into the interior cell.
            float raw = (triangleMax - origin) / voxelSize;
            bool flat = triangleMax - triangleMin <= voxelSize * 1e-6f;
            float gridCenter = origin + gridSize * voxelSize * 0.5f;
            if (!flat || triangleMax > gridCenter) raw -= 1e-5f;
            return Mathf.Clamp(Mathf.FloorToInt(raw), 0, gridSize - 1);
        }

        internal static bool TriangleIntersectsBox(Vector3 a, Vector3 b, Vector3 c, Vector3 center, Vector3 half)
        {
            a -= center; b -= center; c -= center;
            if (Mathf.Min(a.x, Mathf.Min(b.x, c.x)) > half.x || Mathf.Max(a.x, Mathf.Max(b.x, c.x)) < -half.x) return false;
            if (Mathf.Min(a.y, Mathf.Min(b.y, c.y)) > half.y || Mathf.Max(a.y, Mathf.Max(b.y, c.y)) < -half.y) return false;
            if (Mathf.Min(a.z, Mathf.Min(b.z, c.z)) > half.z || Mathf.Max(a.z, Mathf.Max(b.z, c.z)) < -half.z) return false;

            Vector3 e0 = b - a, e1 = c - b, e2 = a - c;
            if (!AxisOverlap(Vector3.Cross(e0, Vector3.right), a, b, c, half) ||
                !AxisOverlap(Vector3.Cross(e0, Vector3.up), a, b, c, half) ||
                !AxisOverlap(Vector3.Cross(e0, Vector3.forward), a, b, c, half) ||
                !AxisOverlap(Vector3.Cross(e1, Vector3.right), a, b, c, half) ||
                !AxisOverlap(Vector3.Cross(e1, Vector3.up), a, b, c, half) ||
                !AxisOverlap(Vector3.Cross(e1, Vector3.forward), a, b, c, half) ||
                !AxisOverlap(Vector3.Cross(e2, Vector3.right), a, b, c, half) ||
                !AxisOverlap(Vector3.Cross(e2, Vector3.up), a, b, c, half) ||
                !AxisOverlap(Vector3.Cross(e2, Vector3.forward), a, b, c, half)) return false;

            Vector3 normal = Vector3.Cross(e0, c - a);
            float planeDistance = Vector3.Dot(normal, a);
            float radius = half.x * Mathf.Abs(normal.x) + half.y * Mathf.Abs(normal.y) + half.z * Mathf.Abs(normal.z);
            return Mathf.Abs(planeDistance) <= radius + 1e-6f;
        }

        private static bool AxisOverlap(Vector3 axis, Vector3 a, Vector3 b, Vector3 c, Vector3 half)
        {
            if (axis.sqrMagnitude < 1e-12f) return true;
            float p0 = Vector3.Dot(a, axis), p1 = Vector3.Dot(b, axis), p2 = Vector3.Dot(c, axis);
            float min = Mathf.Min(p0, Mathf.Min(p1, p2));
            float max = Mathf.Max(p0, Mathf.Max(p1, p2));
            float radius = half.x * Mathf.Abs(axis.x) + half.y * Mathf.Abs(axis.y) + half.z * Mathf.Abs(axis.z);
            return min <= radius && max >= -radius;
        }

        private static Vector3 ClosestPoint(Vector3 a, Vector3 b, Vector3 c, Vector3 p, out Vector3 bary)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) { bary = new Vector3(1, 0, 0); return a; }
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) { bary = new Vector3(0, 1, 0); return b; }
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float v = d1 / (d1 - d3); bary = new Vector3(1 - v, v, 0); return a + v * ab;
            }
            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) { bary = new Vector3(0, 0, 1); return c; }
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float w = d2 / (d2 - d6); bary = new Vector3(1 - w, 0, w); return a + w * ac;
            }
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            {
                float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                bary = new Vector3(0, 1 - w, w); return b + w * (c - b);
            }
            float denominator = 1f / (va + vb + vc);
            float vFace = vb * denominator, wFace = vc * denominator;
            bary = new Vector3(1 - vFace - wFace, vFace, wFace);
            return a + ab * vFace + ac * wFace;
        }

        private static void FillInterior(VoxelGrid grid)
        {
            var outside = new bool[grid.Occupied.Length];
            var queue = new Queue<int>();
            void EnqueueEmpty(int x, int y, int z)
            {
                int index = grid.Index(x, y, z);
                if (grid.Occupied[index] || outside[index]) return;
                outside[index] = true;
                queue.Enqueue(index);
            }

            for (int x = 0; x < grid.Size.x; x++)
            for (int y = 0; y < grid.Size.y; y++)
            {
                EnqueueEmpty(x, y, 0); EnqueueEmpty(x, y, grid.Size.z - 1);
            }
            for (int x = 0; x < grid.Size.x; x++)
            for (int z = 0; z < grid.Size.z; z++)
            {
                EnqueueEmpty(x, 0, z); EnqueueEmpty(x, grid.Size.y - 1, z);
            }
            for (int y = 0; y < grid.Size.y; y++)
            for (int z = 0; z < grid.Size.z; z++)
            {
                EnqueueEmpty(0, y, z); EnqueueEmpty(grid.Size.x - 1, y, z);
            }

            int[] dx = { -1, 1, 0, 0, 0, 0 };
            int[] dy = { 0, 0, -1, 1, 0, 0 };
            int[] dz = { 0, 0, 0, 0, -1, 1 };
            while (queue.Count > 0)
            {
                int index = queue.Dequeue();
                grid.Coordinates(index, out int x, out int y, out int z);
                for (int n = 0; n < 6; n++)
                {
                    int nx = x + dx[n], ny = y + dy[n], nz = z + dz[n];
                    if (nx < 0 || ny < 0 || nz < 0 || nx >= grid.Size.x || ny >= grid.Size.y || nz >= grid.Size.z) continue;
                    EnqueueEmpty(nx, ny, nz);
                }
            }

            queue.Clear();
            queue.TrimExcess();
            for (int i = 0; i < grid.Occupied.Length; i++)
            {
                if (grid.Occupied[i]) queue.Enqueue(i);
            }

            while (queue.Count > 0)
            {
                int index = queue.Dequeue();
                grid.Coordinates(index, out int x, out int y, out int z);
                for (int n = 0; n < 6; n++)
                {
                    int nx = x + dx[n], ny = y + dy[n], nz = z + dz[n];
                    if (nx < 0 || ny < 0 || nz < 0 || nx >= grid.Size.x || ny >= grid.Size.y || nz >= grid.Size.z) continue;
                    int next = grid.Index(nx, ny, nz);
                    if (outside[next] || grid.Occupied[next]) continue;
                    grid.Occupied[next] = true;
                    grid.Colors[next] = grid.Colors[index];
                    queue.Enqueue(next);
                }
            }
        }

        private sealed class MaterialSamplerCache : IDisposable
        {
            private readonly VoxelizationSettings settings;
            private readonly Dictionary<Material, MaterialSampler> cache = new Dictionary<Material, MaterialSampler>();
            private MaterialSampler nullSampler;

            public MaterialSamplerCache(VoxelizationSettings settings) { this.settings = settings; }

            public MaterialSampler Get(Material material)
            {
                if (settings.ColorMode == VoxelColorMode.SingleColor)
                    return nullSampler ?? (nullSampler = new MaterialSampler(settings.SingleColor));
                if (material == null)
                    return nullSampler ?? (nullSampler = new MaterialSampler(new Color32(200, 200, 200, 255)));
                if (!cache.TryGetValue(material, out MaterialSampler sampler))
                {
                    sampler = new MaterialSampler(material, settings.ColorMode == VoxelColorMode.MaterialAndTexture);
                    cache.Add(material, sampler);
                }
                return sampler;
            }

            public void Dispose()
            {
                foreach (MaterialSampler sampler in cache.Values) sampler.Dispose();
                nullSampler?.Dispose();
                cache.Clear();
                nullSampler = null;
            }
        }

        private sealed class MaterialSampler : IDisposable
        {
            private readonly Color baseColor;
            private Color32[] pixels;
            private readonly int width;
            private readonly int height;
            private readonly Vector2 scale = Vector2.one;
            private readonly Vector2 offset = Vector2.zero;
            private readonly TextureWrapMode wrapMode = TextureWrapMode.Repeat;

            public MaterialSampler(Color32 color) { baseColor = color; }

            public MaterialSampler(Material material, bool includeTexture)
            {
                baseColor = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") :
                    material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white;
                if (!includeTexture) return;
                string property = material.HasProperty("_BaseColorMap") ? "_BaseColorMap" :
                    material.HasProperty("_MainTex") ? "_MainTex" : null;
                if (property == null || !(material.GetTexture(property) is Texture2D texture)) return;
                scale = material.GetTextureScale(property);
                offset = material.GetTextureOffset(property);
                wrapMode = texture.wrapMode;
                width = Mathf.Min(texture.width, 512);
                height = Mathf.Min(texture.height, 512);
                RenderTexture temporary = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Default);
                RenderTexture previous = RenderTexture.active;
                Texture2D readable = null;
                try
                {
                    Graphics.Blit(texture, temporary);
                    RenderTexture.active = temporary;
                    readable = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
                    readable.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                    readable.Apply(false, false);
                    pixels = readable.GetPixels32();
                }
                finally
                {
                    if (readable != null) UnityEngine.Object.DestroyImmediate(readable);
                    RenderTexture.active = previous;
                    RenderTexture.ReleaseTemporary(temporary);
                }
            }

            public Color32 Sample(Vector2 uv)
            {
                Color sampled = Color.white;
                if (pixels != null && pixels.Length > 0)
                {
                    uv = Vector2.Scale(uv, scale) + offset;
                    float u = Wrap(uv.x), v = Wrap(uv.y);
                    float fx = u * (width - 1), fy = v * (height - 1);
                    int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
                    int x1 = Mathf.Min(x0 + 1, width - 1), y1 = Mathf.Min(y0 + 1, height - 1);
                    float tx = fx - x0, ty = fy - y0;
                    Color c00 = pixels[x0 + y0 * width], c10 = pixels[x1 + y0 * width];
                    Color c01 = pixels[x0 + y1 * width], c11 = pixels[x1 + y1 * width];
                    sampled = Color.Lerp(Color.Lerp(c00, c10, tx), Color.Lerp(c01, c11, tx), ty);
                }
                return (Color32)(sampled * baseColor);
            }

            private float Wrap(float value)
            {
                return wrapMode == TextureWrapMode.Clamp ? Mathf.Clamp01(value) : Mathf.Repeat(value, 1f);
            }

            public void Dispose() => pixels = null;
        }
    }
}
