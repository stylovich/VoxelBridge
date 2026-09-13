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
        public VoxelConversionProfile ConversionProfile;
        public Vector3 RootScale = Vector3.one;
        public VoxelConversionTiming Timing;
    }

    internal sealed class VoxelizationResult
    {
        public VoxelGrid Grid;
        public Bounds SourceBounds;
        public int OccupiedVoxelCount;
        public float MaximumColorDistance;
        public VoxelSurfaceAssignmentReport SurfaceReport;
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
            public int[][] Triangles;
            public VoxelResolvedRule[] Rules;
            public Matrix4x4 Transform;
        }

        public static VoxelizationResult Voxelize(
            UnityEngine.Object source,
            VoxelizationSettings settings,
            Func<float, string, bool> cancelProgress = null)
        {
            using var preparationTiming = settings.Timing?.Measure("Preparation and resource extraction");
            if (source == null) throw new ArgumentNullException(nameof(source));
            settings.Padding = Mathf.Clamp(settings.Padding, 0, 8);
            bool physicalSizeMode = settings.VoxelSize > 0f;
            if (!physicalSizeMode)
                settings.Resolution = Mathf.Clamp(settings.Resolution, 8, 256);
            if (!physicalSizeMode && settings.Resolution <= settings.Padding * 2)
                throw new ArgumentException("Resolution must exceed the padding on both sides.");

            settings.ConversionProfile?.Validate();
            var mapper = settings.ConversionProfile != null ? new VoxelConversionColorMapper(settings.ConversionProfile) : null;
            var surfaceMatcher = settings.ConversionProfile != null && settings.ConversionProfile.assignSurfacesFromPbr
                ? new VoxelSurfaceMatcher(settings.ConversionProfile.surfaceMapping) : null;
            List<MeshSource> sources = ExtractMeshes(source, settings.IncludeInactiveObjects, settings.ConversionProfile,
                rootScale: settings.RootScale);
            if (sources.Count == 0)
                throw new InvalidOperationException("No triangle submeshes remain to voxelize after applying conversion rules.");

            try
            {
                Bounds bounds = CalculateBounds(sources);
                float longest = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
                if (longest <= 1e-6f)
                    throw new InvalidOperationException("The model has no usable volume.");

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
                if (mapper != null) VoxelSemanticMesher.ValidateGrid(size, origin, voxelSize);
                var grid = new VoxelGrid(size, origin, voxelSize, mapper != null);
                var decisions = surfaceMatcher == null ? null : new VoxelSurfaceDecision[grid.Occupied.Length];
                var pbrWarnings = new HashSet<string>();
                var bestDistances = new float[grid.Occupied.Length];
                for (int i = 0; i < bestDistances.Length; i++) bestDistances[i] = float.PositiveInfinity;

                int triangleTotal = 0;
                foreach (MeshSource meshSource in sources)
                    for (int submesh = 0; submesh < meshSource.Mesh.subMeshCount; submesh++)
                        triangleTotal += meshSource.Triangles[submesh].Length / 3;

                int triangleDone = 0;
                using (settings.Timing?.Measure("Voxelization and material sampling"))
                using (var samplers = new MaterialSamplerCache(settings))
                {
                    foreach (MeshSource meshSource in sources)
                    {
                        for (int submesh = 0; submesh < meshSource.Mesh.subMeshCount; submesh++)
                        {
                            int[] triangles = meshSource.Triangles[submesh];
                            if (triangles.Length == 0) continue;
                            Material material = meshSource.Materials != null && submesh < meshSource.Materials.Length
                                ? meshSource.Materials[submesh]
                                : null;
                            int surfaceId = meshSource.Rules[submesh].SurfaceId;
                            MaterialSampler sampler = samplers.Get(material, surfaceId < 0);
                            VoxelPbrSampler pbr = surfaceMatcher != null && surfaceId < 0 ? samplers.GetPbr(material) : null;
                            for (int t = 0; t < triangles.Length; t += 3)
                            {
                                if ((triangleDone & 127) == 0 && cancelProgress != null &&
                                    cancelProgress((float)triangleDone / Mathf.Max(1, triangleTotal),
                                        $"Voxelizing triangle {triangleDone:N0} of {triangleTotal:N0}"))
                                    throw new OperationCanceledException("Voxelization cancelled.");

                                int i0 = triangles[t], i1 = triangles[t + 1], i2 = triangles[t + 2];
                                Vector3 a = meshSource.Vertices[i0];
                                Vector3 b = meshSource.Vertices[i1];
                                Vector3 c = meshSource.Vertices[i2];
                                Vector2 uv0 = i0 < meshSource.Uvs.Length ? meshSource.Uvs[i0] : Vector2.zero;
                                Vector2 uv1 = i1 < meshSource.Uvs.Length ? meshSource.Uvs[i1] : Vector2.zero;
                                Vector2 uv2 = i2 < meshSource.Uvs.Length ? meshSource.Uvs[i2] : Vector2.zero;
                                RasterizeTriangle(grid, bestDistances, a, b, c, uv0, uv1, uv2,
                                    sampler, settings.AlphaCutoff, mapper, surfaceId, settings.ConversionProfile,
                                    pbr, meshSource.Uvs.Length == meshSource.Vertices.Length, surfaceMatcher, decisions, pbrWarnings,
                                    material != null ? material.name : "Missing material");
                                triangleDone++;
                            }
                        }
                    }
                }
                bestDistances = null;

                int occupiedVoxelCount = grid.CountOccupied();
                if (occupiedVoxelCount == 0)
                    throw new InvalidOperationException("No voxels were generated. Increase the resolution or check material transparency.");
                var surfaceReport = decisions == null ? null : VoxelSurfaceAssignmentReport.Create(
                    grid, decisions, settings.ConversionProfile, source.name, pbrWarnings);
                decisions = null;

                if (settings.FillInterior)
                {
                    using var fillTiming = settings.Timing?.Measure("Interior fill");
                    if (grid.Occupied.LongLength >= 8_000_000)
                        GC.Collect();
                    if (cancelProgress != null && cancelProgress(0.94f, "Filling interior"))
                        throw new OperationCanceledException("Voxelization cancelled.");
                    FillInterior(grid, settings.ConversionProfile?.surfacePalette);
                    occupiedVoxelCount = grid.CountOccupied();
                }

                cancelProgress?.Invoke(1f, "Voxelization complete");
                if (surfaceReport != null) surfaceReport.filledInteriorCells = occupiedVoxelCount - surfaceReport.sampledCells;
                return new VoxelizationResult
                {
                    Grid = grid,
                    SourceBounds = bounds,
                    OccupiedVoxelCount = occupiedVoxelCount,
                    MaximumColorDistance = mapper?.MaximumDistance ?? 0,
                    SurfaceReport = surfaceReport
                };
            }
            finally
            {
                foreach (MeshSource meshSource in sources)
                    if (meshSource.OwnsMesh && meshSource.Mesh != null)
                        UnityEngine.Object.DestroyImmediate(meshSource.Mesh);
            }
        }

        internal static Bounds GetSourceBounds(
            UnityEngine.Object source, bool includeInactiveObjects = true, VoxelConversionProfile profile = null,
            Vector3? rootScale = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            profile?.Validate();
            List<MeshSource> sources = ExtractMeshes(source, includeInactiveObjects, profile, rootScale: rootScale);
            try
            {
                if (sources.Count == 0)
                    throw new InvalidOperationException("No triangle submeshes remain to voxelize after applying conversion rules.");
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
            UnityEngine.Object source, bool includeInactiveObjects, VoxelConversionProfile profile = null,
            VoxelConversionAction action = VoxelConversionAction.Voxelize, Vector3? rootScale = null)
        {
            var result = new List<MeshSource>();
            if (source is Mesh mesh)
            {
                if (action == VoxelConversionAction.Voxelize)
                {
                    var item = CreateMeshSource(mesh, Matrix4x4.identity, null, false, null, null, profile, action);
                    if (item != null) result.Add(item);
                }
                return result;
            }

            if (!(source is GameObject root))
                throw new ArgumentException("Select a GameObject, prefab, FBX/OBJ or Mesh.");

            bool isAsset = AssetDatabase.Contains(root);
            GameObject workingRoot = isAsset ? UnityEngine.Object.Instantiate(root) : root;
            if (isAsset)
            {
                workingRoot.hideFlags = HideFlags.HideAndDontSave;
                workingRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            }

            try
            {
                Matrix4x4 toRoot = Matrix4x4.Scale(rootScale ?? Vector3.one) * workingRoot.transform.worldToLocalMatrix;
                foreach (MeshFilter filter in workingRoot.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh == null ||
                        (!includeInactiveObjects &&
                         !IsActiveWithinRoot(workingRoot.transform, filter.transform)))
                        continue;
                    var renderer = filter.GetComponent<MeshRenderer>();
                    MeshSource item = CreateMeshSource(filter.sharedMesh, toRoot * filter.transform.localToWorldMatrix,
                        renderer != null ? renderer.sharedMaterials : null, false,
                        workingRoot.transform, filter.transform, profile, action);
                    if (item != null) result.Add(item);
                }

                foreach (SkinnedMeshRenderer renderer in workingRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (renderer.sharedMesh == null ||
                        (!includeInactiveObjects &&
                         !IsActiveWithinRoot(workingRoot.transform, renderer.transform)))
                        continue;
                    bool selected = false;
                    Material[] materials = renderer.sharedMaterials;
                    for (int submesh = 0; submesh < renderer.sharedMesh.subMeshCount; submesh++)
                        selected |= VoxelResolvedRule.Resolve(workingRoot.transform, renderer.transform,
                            submesh < materials.Length ? materials[submesh] : null, profile).Action == action;
                    if (!selected) continue;
                    var baked = new Mesh { name = renderer.sharedMesh.name + "_VoxelBake" };
                    try
                    {
                        // Match the renderer-local mesh expected by localToWorldMatrix. The default
                        // bake path double-applies imported renderer scales on Synty skinned assets.
                        renderer.BakeMesh(baked, true);
                        MeshSource item = CreateMeshSource(baked, toRoot * renderer.transform.localToWorldMatrix,
                            materials, true, workingRoot.transform, renderer.transform, profile, action);
                        if (item != null) result.Add(item);
                        else UnityEngine.Object.DestroyImmediate(baked);
                    }
                    catch { UnityEngine.Object.DestroyImmediate(baked); throw; }
                }
            }
            catch
            {
                foreach (var item in result)
                    if (item.OwnsMesh) UnityEngine.Object.DestroyImmediate(item.Mesh);
                throw;
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

        private static MeshSource CreateMeshSource(Mesh mesh, Matrix4x4 transform, Material[] materials, bool ownsMesh,
            Transform root, Transform current, VoxelConversionProfile profile, VoxelConversionAction action)
        {
            transform = VoxelSourceOrientation.ToUnity(profile?.sourceAxes ?? VoxelSourceAxes.PreserveLocalAxes) * transform;
            var triangles = new int[mesh.subMeshCount][];
            var rules = new VoxelResolvedRule[mesh.subMeshCount];
            bool selected = false;
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                rules[i] = VoxelResolvedRule.Resolve(root, current, materials != null && i < materials.Length ? materials[i] : null, profile);
                if (rules[i].Action == action)
                {
                    if (mesh.GetTopology(i) != MeshTopology.Triangles) throw new InvalidOperationException("Conversion requires triangle submeshes.");
                    triangles[i] = mesh.GetTriangles(i);
                    if (transform.determinant < 0)
                        for (int t = 0; t < triangles[i].Length; t += 3)
                            (triangles[i][t + 1], triangles[i][t + 2]) = (triangles[i][t + 2], triangles[i][t + 1]);
                    selected |= triangles[i].Length > 0;
                }
                else triangles[i] = Array.Empty<int>();
            }
            if (!selected) return null;
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
                    $"Could not read mesh '{mesh.name}'. Enable Read/Write in its Import Settings.", exception);
            }

            for (int i = 0; i < vertices.Length; i++) vertices[i] = transform.MultiplyPoint3x4(vertices[i]);
            return new MeshSource
            {
                Mesh = mesh,
                Vertices = vertices,
                Uvs = uvs ?? Array.Empty<Vector2>(),
                Materials = materials,
                OwnsMesh = ownsMesh,
                Triangles = triangles, Rules = rules, Transform = transform
            };
        }

        internal static string ExportRetainedGeometry(UnityEngine.Object source, bool includeInactiveObjects,
            VoxelConversionProfile profile, string familyFolder, Vector3? rootScale = null)
        {
            List<MeshSource> sources = ExtractMeshes(source, includeInactiveObjects, profile, VoxelConversionAction.KeepOriginal, rootScale);
            GameObject root = null;
            try
            {
                if (sources.Count == 0) return null;
                root = new GameObject("Retained Geometry");
                int index = 0;
                foreach (var item in sources)
                for (int submesh = 0; submesh < item.Triangles.Length; submesh++)
                {
                    if (item.Triangles[submesh].Length == 0) continue;
                    Material material = item.Materials != null && submesh < item.Materials.Length ? item.Materials[submesh] : null;
                    if (material == null || !AssetDatabase.Contains(material))
                        throw new InvalidOperationException("Keep Original requires persistent material assets.");
                    var mesh = new Mesh { name = "Retained_" + index, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                    try
                    {
                        mesh.CombineMeshes(new[] { new CombineInstance { mesh = item.Mesh, subMeshIndex = submesh, transform = item.Transform } }, true, true);
                        // CombineMeshes handles reflected winding itself; do not reverse it again.
                        // CombineMeshes can retain unused vertices from other submeshes. Bounds must describe only drawn triangles.
                        var bounds = new Bounds(item.Vertices[item.Triangles[submesh][0]], Vector3.zero);
                        foreach (int vertex in item.Triangles[submesh]) bounds.Encapsulate(item.Vertices[vertex]);
                        mesh.bounds = bounds;
                        AssetDatabase.CreateAsset(mesh, familyFolder + $"/Retained_{index}.asset");
                        var child = new GameObject(item.Mesh.name + "_Submesh" + submesh);
                        child.transform.SetParent(root.transform, false);
                        child.AddComponent<MeshFilter>().sharedMesh = mesh;
                        child.AddComponent<MeshRenderer>().sharedMaterial = material;
                    }
                    finally { if (!AssetDatabase.Contains(mesh)) UnityEngine.Object.DestroyImmediate(mesh); }
                    index++;
                }
                string path = familyFolder + "/RetainedGeometry.prefab";
                if (PrefabUtility.SaveAsPrefabAsset(root, path) == null) throw new InvalidOperationException("Could not save retained geometry.");
                return AssetDatabase.AssetPathToGUID(path);
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                foreach (var item in sources) if (item.OwnsMesh) UnityEngine.Object.DestroyImmediate(item.Mesh);
            }
        }

        private static Bounds CalculateBounds(List<MeshSource> sources)
        {
            bool initialized = false;
            Bounds bounds = default;
            foreach (MeshSource source in sources)
            {
                foreach (int[] triangles in source.Triangles)
                foreach (int index in triangles)
                {
                    Vector3 vertex = source.Vertices[index];
                    if (!initialized) { bounds = new Bounds(vertex, Vector3.zero); initialized = true; }
                    else bounds.Encapsulate(vertex);
                }
            }
            if (!initialized) throw new InvalidOperationException("The selected meshes contain no vertices.");
            return bounds;
        }

        private static void RasterizeTriangle(
            VoxelGrid grid, float[] bestDistances,
            Vector3 a, Vector3 b, Vector3 c,
            Vector2 uv0, Vector2 uv1, Vector2 uv2,
            MaterialSampler sampler, float alphaCutoff, VoxelConversionColorMapper mapper,
            int surfaceId, VoxelConversionProfile profile, VoxelPbrSampler pbr, bool hasUv0,
            VoxelSurfaceMatcher surfaceMatcher, VoxelSurfaceDecision[] decisions, HashSet<string> warnings, string materialName)
        {
            Vector3 min = Vector3.Min(a, Vector3.Min(b, c));
            Vector3 max = Vector3.Max(a, Vector3.Max(b, c));
            CellRange(min.x, max.x, grid.Origin.x, grid.VoxelSize, grid.Size.x, out int minX, out int maxX);
            CellRange(min.y, max.y, grid.Origin.y, grid.VoxelSize, grid.Size.y, out int minY, out int maxY);
            CellRange(min.z, max.z, grid.Origin.z, grid.VoxelSize, grid.Size.z, out int minZ, out int maxZ);
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
                bool existingGlass = mapper != null && grid.Occupied[index] &&
                    mapper.IsGlass(VoxelSemanticEncoding.SurfaceId(grid.SemanticIds[index]));
                // An opaque sample can replace glass even when farther from the cell center.
                // RGB and cells already owned by opaque geometry keep the distance fast path.
                if (distance >= bestDistances[index] && !existingGlass) continue;
                Vector2 uv = uv0 * barycentric.x + uv1 * barycentric.y + uv2 * barycentric.z;
                Color32 color = sampler.Sample(uv);
                bool belowCutoff = color.a / 255f < alphaCutoff;
                color.a = 255;
                if (mapper != null)
                {
                    int resolvedSurface = surfaceId;
                    var decision = surfaceId >= 0 ? VoxelSurfaceDecision.Explicit : VoxelSurfaceDecision.Unassigned;
                    if (surfaceId < 0 && profile.detectEmission && sampler.TrySampleEmission(uv, profile.emissionThreshold, out Color32 emission))
                    {
                        if (!sampler.IsConstantColor) color = emission;
                        resolvedSurface = profile.emissiveSurfaceId; decision = VoxelSurfaceDecision.EmissiveFallback;
                    }
                    else if (surfaceId < 0 && surfaceMatcher != null)
                    {
                        if (pbr.TrySample(uv, hasUv0, out float metallic, out float smoothness, out string reason))
                        {
                            var match = surfaceMatcher.Match(metallic, smoothness);
                            resolvedSurface = match.SurfaceId; decision = match.Decision;
                        }
                        else
                        {
                            decision = VoxelSurfaceDecision.Unsupported;
                            string warning = materialName + ": " + reason;
                            if (!warnings.Contains(warning))
                            {
                                if (warnings.Count < 64) warnings.Add(warning);
                                else warnings.Add("Additional unsupported material warnings omitted (limit 64).");
                            }
                        }
                    }
                    // Glass opacity belongs to the semantic palette, not to geometric alpha clipping.
                    bool incomingGlass = mapper.IsGlass(resolvedSurface);
                    if (belowCutoff && !incomingGlass) continue;
                    // One semantic pair per cell: preserve opaque barriers at mixed window/frame
                    // intersections. Within the same render class, retain the nearest sample.
                    if (incomingGlass && grid.Occupied[index] &&
                        (!existingGlass || distance >= bestDistances[index])) continue;
                    grid.SemanticIds[index] = mapper.Map(color, resolvedSurface);
                    if (decisions != null) decisions[index] = decision;
                }
                else
                {
                    if (belowCutoff) continue;
                    grid.Colors[index] = color;
                }
                grid.Occupied[index] = true;
                bestDistances[index] = distance;
            }
        }

        private static void CellRange(float triangleMin, float triangleMax, float origin, float voxelSize,
            int gridSize, out int lower, out int upper)
        {
            float rawMin = (triangleMin - origin) / voxelSize;
            float rawMax = (triangleMax - origin) / voxelSize;
            bool flat = triangleMax - triangleMin <= voxelSize * 1e-6f;
            float gridCenter = origin + gridSize * voxelSize * 0.5f;
            // A non-flat upper bound is half-open. For a flat boundary face, move only faces
            // on the positive half inward; negative faces already fall into the interior cell.
            if (flat && triangleMin > gridCenter) rawMin -= 1e-5f;
            if (!flat || triangleMax > gridCenter) rawMax -= 1e-5f;
            lower = Mathf.Clamp(Mathf.FloorToInt(rawMin), 0, gridSize - 1);
            // The boundary bias must not erase an interval thinner than that bias.
            // Keep its lower candidate cell; the triangle/box test still decides coverage.
            upper = Mathf.Clamp(Mathf.FloorToInt(rawMax), lower, gridSize - 1);
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

        internal static void FillInterior(VoxelGrid grid, VoxelSurfacePalette surfacePalette = null)
        {
            // Glass remains occupied geometry, but does not seal spaces visible through it.
            // Match the mesher's render-class policy, independently of the current opacity.
            bool[] glass = grid.IsSemantic ? VoxelSemanticMesher.GlassIds(surfacePalette) : null;
            var outside = new bool[grid.Occupied.Length];
            var queue = new Queue<int>();
            void EnqueueExterior(int x, int y, int z)
            {
                int index = grid.Index(x, y, z);
                if (outside[index] || (grid.Occupied[index] && !VoxelSemanticMesher.IsGlass(grid, index, glass))) return;
                outside[index] = true;
                queue.Enqueue(index);
            }

            for (int x = 0; x < grid.Size.x; x++)
            for (int y = 0; y < grid.Size.y; y++)
            {
                EnqueueExterior(x, y, 0); EnqueueExterior(x, y, grid.Size.z - 1);
            }
            for (int x = 0; x < grid.Size.x; x++)
            for (int z = 0; z < grid.Size.z; z++)
            {
                EnqueueExterior(x, 0, z); EnqueueExterior(x, grid.Size.y - 1, z);
            }
            for (int y = 0; y < grid.Size.y; y++)
            for (int z = 0; z < grid.Size.z; z++)
            {
                EnqueueExterior(0, y, z); EnqueueExterior(grid.Size.x - 1, y, z);
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
                    EnqueueExterior(nx, ny, nz);
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
                    if (grid.IsSemantic) grid.SemanticIds[next] = grid.SemanticIds[index];
                    else grid.Colors[next] = grid.Colors[index];
                    queue.Enqueue(next);
                }
            }
        }

        private sealed class MaterialSamplerCache : IDisposable
        {
            private readonly VoxelizationSettings settings;
            private readonly Dictionary<Material, MaterialSampler> cache = new Dictionary<Material, MaterialSampler>();
            private MaterialSampler nullSampler;
            private readonly Dictionary<Material, VoxelPbrSampler> pbrCache = new();
            private VoxelPbrSampler nullPbrSampler;

            public MaterialSamplerCache(VoxelizationSettings settings) { this.settings = settings; }

            public MaterialSampler Get(Material material, bool allowEmission)
            {
                if (settings.ColorMode == VoxelColorMode.SingleColor && settings.ConversionProfile?.assignSurfacesFromPbr != true)
                    return nullSampler ?? (nullSampler = new MaterialSampler(settings.SingleColor));
                if (material == null)
                    return nullSampler ?? (nullSampler = new MaterialSampler(settings.ColorMode == VoxelColorMode.SingleColor
                        ? settings.SingleColor : new Color32(200, 200, 200, 255)));
                if (!cache.TryGetValue(material, out MaterialSampler sampler))
                {
                    sampler = settings.ColorMode == VoxelColorMode.SingleColor ? new MaterialSampler(settings.SingleColor) :
                        new MaterialSampler(material, settings.ColorMode == VoxelColorMode.MaterialAndTexture);
                    cache.Add(material, sampler);
                }
                if (allowEmission && settings.ConversionProfile != null && settings.ConversionProfile.detectEmission)
                    sampler.ConfigureEmission(material);
                return sampler;
            }

            public void Dispose()
            {
                foreach (var sampler in pbrCache.Values) sampler.Dispose();
                pbrCache.Clear(); nullPbrSampler?.Dispose(); nullPbrSampler = null;
                foreach (MaterialSampler sampler in cache.Values) sampler.Dispose();
                nullSampler?.Dispose();
                cache.Clear();
                nullSampler = null;
            }

            internal VoxelPbrSampler GetPbr(Material material)
            {
                if (material == null) return nullPbrSampler ??= new VoxelPbrSampler(null);
                if (!pbrCache.TryGetValue(material, out var sampler))
                { sampler = new VoxelPbrSampler(material); pbrCache.Add(material, sampler); }
                return sampler;
            }
        }

        private sealed class MaterialSampler : IDisposable
        {
            private readonly Color baseColor;
            internal bool IsConstantColor { get; }
            private Color32[] pixels;
            private readonly int width;
            private readonly int height;
            private readonly Vector2 scale = Vector2.one;
            private readonly Vector2 offset = Vector2.zero;
            private readonly TextureWrapMode wrapMode = TextureWrapMode.Repeat;
            private MaterialSampler emissionSampler;
            private bool emissionConfigured;

            public MaterialSampler(Color32 color) { baseColor = color; IsConstantColor = true; }

            public MaterialSampler(Material material, bool includeTexture, bool emission = false)
            {
                baseColor = emission ? material.GetColor(material.shader.name == "HDRP/Lit" ? "_EmissiveColor" : "_EmissionColor") :
                    material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") :
                    material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white;
                if (!includeTexture) return;
                string property = emission ? (material.shader.name == "HDRP/Lit" ? "_EmissiveColorMap" : "_EmissionMap") :
                    material.HasProperty("_BaseColorMap") ? "_BaseColorMap" :
                    material.HasProperty("_MainTex") ? "_MainTex" : null;
                if (property == null || !(material.GetTexture(property) is Texture2D texture)) return;
                scale = material.GetTextureScale(property);
                offset = material.GetTextureOffset(property);
                wrapMode = texture.wrapMode;
                width = Mathf.Min(texture.width, 512);
                height = Mathf.Min(texture.height, 512);
                RenderTexture temporary = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32,
                    emission ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.Default);
                RenderTexture previous = RenderTexture.active;
                Texture2D readable = null;
                try
                {
                    Graphics.Blit(texture, temporary);
                    RenderTexture.active = temporary;
                    readable = new Texture2D(width, height, TextureFormat.RGBA32, false, emission);
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

            public void ConfigureEmission(Material material)
            {
                if (emissionConfigured) return;
                emissionConfigured = true;
                string shader = material.shader.name;
                bool hdrp = shader == "HDRP/Lit";
                bool standard = shader == "Standard" || shader == "Standard (Specular setup)";
                if (!hdrp && !standard)
                {
                    Debug.LogWarning($"Voxel Bridge cannot infer emission from shader '{shader}' on '{material.name}'. Assign an explicit SurfaceID if needed.");
                    return;
                }
                if (standard && !material.IsKeywordEnabled("_EMISSION")) return;
                Color tint = material.GetColor(hdrp ? "_EmissiveColor" : "_EmissionColor");
                if (Mathf.Max(tint.r, Mathf.Max(tint.g, tint.b)) <= 0) return;
                bool sampleMap = !hdrp || material.IsKeywordEnabled("_EMISSIVE_COLOR_MAP");
                if (hdrp && ((sampleMap && material.GetFloat("_UVEmissive") != 0) || material.GetFloat("_AlbedoAffectEmissive") != 0))
                {
                    Debug.LogWarning($"Voxel Bridge requires UV0 emission without Albedo Affect Emissive on '{material.name}'. Assign an explicit SurfaceID.");
                    return;
                }
                emissionSampler = new MaterialSampler(material, sampleMap, true);
            }

            public bool TrySampleEmission(Vector2 uv, float threshold, out Color32 color)
            {
                color = default;
                if (emissionSampler == null) return false;
                Color emission = emissionSampler.SampleColor(uv);
                float peak = Mathf.Max(emission.r, Mathf.Max(emission.g, emission.b));
                if (!float.IsFinite(peak) || peak < threshold) return false;
                // Preserve hue while discarding HDR intensity; production intensity belongs to the surface palette.
                emission /= Mathf.Max(1f, peak);
                emission.a = 1;
                color = emission.gamma;
                return true;
            }

            public Color32 Sample(Vector2 uv) => SampleColor(uv);

            private Color SampleColor(Vector2 uv)
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
                return sampled * baseColor;
            }

            private float Wrap(float value)
            {
                return wrapMode switch
                {
                    TextureWrapMode.Clamp => Mathf.Clamp01(value),
                    TextureWrapMode.Mirror => Mathf.PingPong(value, 1f),
                    TextureWrapMode.MirrorOnce => Mathf.Clamp01(Mathf.Abs(value)),
                    _ => Mathf.Repeat(value, 1f)
                };
            }

            public void Dispose() { pixels = null; emissionSampler?.Dispose(); emissionSampler = null; }
        }
    }
}
