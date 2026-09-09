using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge
{
    internal sealed class VoxelCombineSource
    {
        internal GameObject Instance;
        internal string VoxPath;
        internal VoxelBridgeMetadata Metadata;
        internal Matrix4x4 LocalToOutput;
        internal VoxelCellTransform Cells;
    }

    // A signed permutation and integer translation: no sampling or rounding of occupied cells.
    internal readonly struct VoxelCellTransform
    {
        private readonly Vector3Int x, y, z, offset;

        internal VoxelCellTransform(VoxelBridgeMetadata metadata, Matrix4x4 transform, float unit)
        {
            x = Axis(transform.MultiplyVector(Vector3.right * metadata.voxelSize) / unit);
            y = Axis(transform.MultiplyVector(Vector3.up * metadata.voxelSize) / unit);
            z = Axis(transform.MultiplyVector(Vector3.forward * metadata.voxelSize) / unit);
            if (Vector3.Dot(x, y) != 0 || Vector3.Dot(x, z) != 0 || Vector3.Dot(y, z) != 0)
                throw new InvalidDataException("The source transform contains shear or collapsed axes.");
            offset = Integral(transform.MultiplyPoint3x4(metadata.gridOrigin) / unit);
            for (int i = 1; i < 8; i++)
            {
                var corner = new Vector3Int((i & 1) == 0 ? 0 : metadata.unityGridSize.x,
                    (i & 2) == 0 ? 0 : metadata.unityGridSize.y, (i & 4) == 0 ? 0 : metadata.unityGridSize.z);
                var transformed = Integral(transform.MultiplyPoint3x4(metadata.gridOrigin + (Vector3)corner * metadata.voxelSize) / unit);
                if (transformed != offset + x * corner.x + y * corner.y + z * corner.z)
                    throw new InvalidDataException("The transform drifts off the voxel grid across the source bounds.");
            }
            offset += Vector3Int.Min(x, Vector3Int.zero) + Vector3Int.Min(y, Vector3Int.zero) + Vector3Int.Min(z, Vector3Int.zero);
        }

        internal Vector3Int Map(int cx, int cy, int cz) => offset + x * cx + y * cy + z * cz;

        internal void Bounds(Vector3Int size, out Vector3Int min, out Vector3Int max)
        {
            min = max = Map(0, 0, 0);
            for (int i = 1; i < 8; i++)
            {
                var cell = Map((i & 1) == 0 ? 0 : size.x - 1, (i & 2) == 0 ? 0 : size.y - 1, (i & 4) == 0 ? 0 : size.z - 1);
                min = Vector3Int.Min(min, cell); max = Vector3Int.Max(max, cell);
            }
        }

        private static Vector3Int Axis(Vector3 value)
        {
            var axis = Integral(value);
            if (Mathf.Abs(axis.x) + Mathf.Abs(axis.y) + Mathf.Abs(axis.z) != 1)
                throw new InvalidDataException("The effective voxel size must equal the output profile base size. Only axis-aligned rotations and reflections are supported.");
            return axis;
        }

        private static Vector3Int Integral(Vector3 value)
        {
            for (int i = 0; i < 3; i++)
                if (float.IsNaN(value[i]) || float.IsInfinity(value[i]) || Mathf.Abs(value[i]) > 8_000_000 ||
                    Mathf.Abs(value[i] - Mathf.Round(value[i])) > 0.001f)
                    throw new InvalidDataException("The source is not aligned to the world voxel grid (tolerance: 0.001 cell). Snap its position and use axis-aligned rotations with a compatible scale.");
            return Vector3Int.RoundToInt(value);
        }
    }

    internal sealed class VoxelCombineAnalysis
    {
        internal VoxelCombineSource[] Sources;
        internal VoxelGrid Grid;
        internal Vector3 Pivot;
        internal int Overlaps, Conflicts, PairCount;
        internal readonly List<string> ConflictExamples = new List<string>();
    }

    internal sealed class VoxelSourceSnap
    {
        internal GameObject Instance;
        internal Transform Parent;
        internal UnityEngine.SceneManagement.Scene Scene;
        internal bool Active;
        internal Matrix4x4 ParentMatrix;
        internal Vector3 OriginalPosition, OriginalLocalPosition, OriginalScale, Position, LocalPosition;
        internal Quaternion OriginalRotation, OriginalLocalRotation, Rotation, LocalRotation;
        internal float Unit;
        internal bool Changed => !OriginalLocalPosition.Equals(LocalPosition) || !OriginalLocalRotation.Equals(LocalRotation);
    }

    internal static class VoxelFamilyCombiner
    {
        internal static GameObject[] Collect(GameObject[] selection, bool ignoreInactive)
        {
            if (selection == null || selection.Length == 0) throw new InvalidDataException("Select scene instances or a scene parent.");
            var sources = new HashSet<GameObject>();
            foreach (var selected in selection)
            {
                if (selected == null || EditorUtility.IsPersistent(selected) || !selected.scene.IsValid() ||
                    PrefabStageUtility.GetPrefabStage(selected) != null)
                    throw new InvalidDataException("Use scene instances, not Project assets or Prefab Mode contents.");
                var root = PrefabUtility.GetNearestPrefabInstanceRoot(selected);
                if (root != null && VoxelProductionLink.HasLink(VoxelProductionLink.PrefabPath(root))) Visit(root);
                else Visit(selected);
            }
            if (sources.Count < 2) throw new InvalidDataException("At least two semantic production instances are required.");
            if (sources.Select(s => s.scene.handle).Distinct().Count() != 1)
                throw new InvalidDataException("Combine sources from one scene only. Cross-scene streaming groups are not supported.");
            return sources.OrderBy(s => HierarchyKey(s.transform), StringComparer.Ordinal).ToArray();

            void Visit(GameObject node)
            {
                if (ignoreInactive && !node.activeInHierarchy) return;
                string path = VoxelProductionLink.PrefabPath(node);
                if (PrefabUtility.GetNearestPrefabInstanceRoot(node) == node && VoxelProductionLink.HasLink(path))
                { sources.Add(node); return; }
                if (node.GetComponent<Renderer>() != null)
                    throw new InvalidDataException($"'{node.name}' is not a linked semantic production instance. Convert it first or exclude it from the selection.");
                foreach (Transform child in node.transform) Visit(child.gameObject);
            }
        }

        private static string HierarchyKey(Transform node) =>
            (node.parent == null ? "" : HierarchyKey(node.parent) + "/") + node.GetSiblingIndex().ToString("D8");

        internal static Quaternion NearestGridRotation(Quaternion rotation)
        {
            // Search the 24 proper cube orientations, not rounded Euler components.
            var axes = new[] { Vector3.forward, Vector3.back, Vector3.right, Vector3.left, Vector3.up, Vector3.down };
            Quaternion best = Quaternion.identity;
            float bestScore = -1;
            foreach (var forward in axes) foreach (var up in axes)
            {
                if (Vector3.Dot(forward, up) != 0) continue;
                Quaternion candidate = Quaternion.LookRotation(forward, up);
                float score = Mathf.Abs(Quaternion.Dot(rotation, candidate));
                if (score <= bestScore) continue;
                bestScore = score; best = candidate;
            }
            return best;
        }

        internal static VoxelSourceSnap[] PreviewSnap(GameObject[] selection, VoxelStyleProfile profile, bool ignoreInactive)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before snapping sources.");
            if (profile == null || string.IsNullOrEmpty(AssetDatabase.GetAssetPath(profile)) || !profile.TryValidate(out _))
                throw new InvalidDataException("Assign a valid saved voxel style profile.");
            var roots = Collect(selection, ignoreInactive);
            if (roots.Any(a => roots.Any(b => a != b && a.transform.IsChildOf(b.transform))))
                throw new InvalidDataException("Select independent production roots, not nested source instances together.");
            return roots.Select(root =>
            {
                Transform t = root.transform;
                Vector3 position = t.position;
                float unit = profile.BaseVoxelSize;
                for (int i = 0; i < 3; i++) position[i] = Mathf.Round(position[i] / unit) * unit;
                Quaternion rotation = NearestGridRotation(t.rotation);
                var item = new VoxelSourceSnap { Instance = root, Parent = t.parent, Scene = root.scene, Active = root.activeInHierarchy,
                    ParentMatrix = t.parent == null ? Matrix4x4.identity : t.parent.localToWorldMatrix,
                    OriginalPosition = t.position, OriginalRotation = t.rotation,
                    OriginalLocalPosition = t.localPosition, OriginalLocalRotation = t.localRotation, OriginalScale = t.localScale,
                    Position = position, Rotation = rotation, Unit = unit,
                    LocalPosition = t.parent == null ? position : t.parent.InverseTransformPoint(position),
                    LocalRotation = t.parent == null ? rotation : Quaternion.Inverse(t.parent.rotation) * rotation };
                ValidateSnap(item, item.ParentMatrix * Matrix4x4.TRS(item.LocalPosition, item.LocalRotation, item.OriginalScale));
                return item;
            }).ToArray();
        }

        private static void ValidateSnap(VoxelSourceSnap item, Matrix4x4 matrix)
        {
            string path = VoxelProductionLink.PrefabPath(item.Instance);
            var link = VoxelProductionLink.Load(path);
            ValidateInstance(item.Instance, path, link);
            if (!VoxelImporterIntegration.TryLoadMetadata(link.SourcePath, out var metadata, out string error))
                throw new InvalidDataException(error);
            if (metadata.formatVersion != 4 || metadata.lodIndex != 0 || metadata.semantic == null)
                throw new InvalidDataException("Snapping requires a semantic production LOD0.");
            VoxelSemanticMesher.ValidateGrid(metadata.unityGridSize, metadata.gridOrigin, metadata.voxelSize);
            try { _ = new VoxelCellTransform(metadata, matrix, item.Unit); }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException($"'{item.Instance.name}': position and rotation snapping cannot align this source. " +
                    "Check its effective voxel size, source grid origin and parent scale. Scale is never changed automatically. " + ex.Message, ex);
            }
        }

        internal static int ApplySnap(VoxelSourceSnap[] plan, VoxelStyleProfile profile)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before snapping sources.");
            if (plan == null || plan.Length == 0 || profile == null || !profile.TryValidate(out _))
                throw new InvalidOperationException("Create a valid snap preview first.");
            // Validate the whole preview before recording Undo or changing any source.
            foreach (var item in plan)
            {
                if (item.Instance == null || profile.BaseVoxelSize != item.Unit || item.Instance.scene != item.Scene ||
                    item.Instance.activeInHierarchy != item.Active)
                    throw new InvalidOperationException("The sources or profile changed. Preview the snap again.");
                Transform t = item.Instance.transform;
                Matrix4x4 parent = t.parent == null ? Matrix4x4.identity : t.parent.localToWorldMatrix;
                if (t.parent != item.Parent || !parent.Equals(item.ParentMatrix) ||
                    !t.localPosition.Equals(item.OriginalLocalPosition) || !t.localRotation.Equals(item.OriginalLocalRotation) ||
                    !t.localScale.Equals(item.OriginalScale))
                    throw new InvalidOperationException("A source transform changed. Preview the snap again.");
                ValidateSnap(item, parent * Matrix4x4.TRS(item.LocalPosition, item.LocalRotation, item.OriginalScale));
            }
            var changed = plan.Where(p => p.Changed).ToArray();
            if (changed.Length == 0) return 0;
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Snap Voxel Sources to Grid");
            try
            {
                Undo.RecordObjects(changed.Select(p => (Object)p.Instance.transform).ToArray(), "Snap Voxel Sources to Grid");
                foreach (var item in changed)
                {
                    Transform t = item.Instance.transform;
                    t.localPosition = item.LocalPosition; t.localRotation = item.LocalRotation;
                    ValidateSnap(item, t.localToWorldMatrix);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(t);
                }
                Undo.FlushUndoRecordObjects();
                Undo.CollapseUndoOperations(group);
                SceneView.RepaintAll();
                return changed.Length;
            }
            catch { Undo.FlushUndoRecordObjects(); Undo.RevertAllDownToGroup(group); throw; }
        }

        internal static VoxelCombineAnalysis Analyze(GameObject[] selection, VoxelStyleProfile profile,
            bool ignoreInactive = true, Action<float> progress = null)
        {
            if (profile == null || string.IsNullOrEmpty(AssetDatabase.GetAssetPath(profile)))
                throw new InvalidDataException("Assign a saved voxel style profile.");
            if (!profile.TryValidate(out string error)) throw new InvalidDataException(error);
            var instances = Collect(selection, ignoreInactive);
            float unit = profile.BaseVoxelSize;
            Vector3 pivot = instances[0].transform.position;
            for (int axis = 0; axis < 3; axis++) pivot[axis] = Mathf.Round(pivot[axis] / unit) * unit;
            var result = new VoxelCombineAnalysis { Pivot = pivot, Sources = new VoxelCombineSource[instances.Length] };
            Vector3Int min = default, max = default;
            for (int i = 0; i < instances.Length; i++)
            {
                progress?.Invoke(0.1f * i / instances.Length);
                var instance = instances[i];
                string prefabPath = VoxelProductionLink.PrefabPath(instance);
                var link = VoxelProductionLink.Load(prefabPath);
                string voxPath = link.SourcePath;
                if (!VoxelImporterIntegration.TryLoadMetadata(voxPath, out var metadata, out error)) throw new InvalidDataException(error);
                if (metadata.formatVersion != 4 || metadata.semantic == null || metadata.lodIndex != 0)
                    throw new InvalidDataException($"'{instance.name}' requires a semantic LOD0 source.");
                VoxelSemanticMesher.ValidateGrid(metadata.unityGridSize, metadata.gridOrigin, metadata.voxelSize);
                ValidateInstance(instance, prefabPath, link);
                if (i > 0 && (metadata.semantic.colorPaletteGuid != result.Sources[0].Metadata.semantic.colorPaletteGuid ||
                    metadata.semantic.surfacePaletteGuid != result.Sources[0].Metadata.semantic.surfacePaletteGuid))
                    throw new InvalidDataException($"'{instance.name}' uses different global palettes. Remap its bindings explicitly before combining.");
                var source = new VoxelCombineSource { Instance = instance, VoxPath = voxPath, Metadata = metadata,
                    LocalToOutput = Matrix4x4.Translate(-pivot) * instance.transform.localToWorldMatrix };
                try { source.Cells = new VoxelCellTransform(metadata, source.LocalToOutput, unit); }
                catch (InvalidDataException ex) { throw new InvalidDataException($"'{instance.name}': {ex.Message}", ex); }
                source.Cells.Bounds(metadata.unityGridSize, out var sourceMin, out var sourceMax);
                min = i == 0 ? sourceMin : Vector3Int.Min(min, sourceMin);
                max = i == 0 ? sourceMax : Vector3Int.Max(max, sourceMax);
                result.Sources[i] = source;
            }
            var size = max - min + Vector3Int.one;
            VoxelSemanticMesher.ValidateGrid(size, (Vector3)min * unit, unit);
            result.Grid = new VoxelGrid(size, (Vector3)min * unit, unit, true);
            for (int sourceIndex = 0; sourceIndex < result.Sources.Length; sourceIndex++)
            {
                var source = result.Sources[sourceIndex];
                var grid = VoxelProductionExporter.ReadGrid(source.VoxPath, out _, out var colors, out var surfaces, out _);
                VoxelPaletteLutBuilder.TryBuildColorPixels(colors, out _, out string colorHash, out _);
                VoxelPaletteLutBuilder.TryBuildSurfacePixels(surfaces, out _, out string surfaceHash, out _);
                if (source.Metadata.semantic.colorPaletteHash != colorHash || source.Metadata.semantic.surfacePaletteHash != surfaceHash)
                    throw new InvalidDataException($"'{source.Instance.name}' references an older palette revision. Review and save its Semantic Bindings before combining.");
                for (int i = 0; i < grid.Occupied.Length; i++)
                {
                    if ((i & 65535) == 0) progress?.Invoke(0.1f + 0.9f * (sourceIndex + (float)i / grid.Occupied.Length) / result.Sources.Length);
                    if (!grid.Occupied[i]) continue;
                    grid.Coordinates(i, out int x, out int y, out int z);
                    Vector3Int target = source.Cells.Map(x, y, z) - min;
                    int index = result.Grid.Index(target.x, target.y, target.z);
                    if (result.Grid.Occupied[index])
                    {
                        result.Overlaps++;
                        if (result.Grid.SemanticIds[index] != grid.SemanticIds[i])
                        {
                            result.Conflicts++;
                            if (result.ConflictExamples.Count < 6)
                                result.ConflictExamples.Add($"{source.Instance.name}: {Describe(result.Grid.SemanticIds[index])} > {Describe(grid.SemanticIds[i])}");
                        }
                        continue;
                    }
                    result.Grid.Occupied[index] = true;
                    result.Grid.SemanticIds[index] = grid.SemanticIds[i];
                }
            }
            int occupied = result.Grid.CountOccupied();
            if (occupied == 0) throw new InvalidDataException("The combined volume is empty.");
            if (occupied > VoxelLodBatchOptions.DefaultMaximumImportedVoxelCount)
                throw new InvalidDataException("The combined volume exceeds the 4,000,000 occupied-voxel import limit. Use a smaller group.");
            var pairs = new HashSet<ushort>();
            for (int i = 0; i < result.Grid.Occupied.Length; i++) if (result.Grid.Occupied[i]) pairs.Add(result.Grid.SemanticIds[i]);
            result.PairCount = pairs.Count;
            if (pairs.Count > 255) throw new InvalidDataException($"The combined VOX requires {pairs.Count} color/surface pairs; the limit is 255. Use a smaller group or remap bindings.");
            progress?.Invoke(1);
            return result;
        }

        private static string Describe(ushort id) => $"ColorID {VoxelSemanticEncoding.ColorId(id)} / SurfaceID {VoxelSemanticEncoding.SurfaceId(id)}";

        private static void ValidateInstance(GameObject instance, string prefabPath, VoxelProductionLink link)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Transform actual = string.IsNullOrEmpty(link.manifestGuid) ? instance.transform : instance.transform.Find("LOD0");
            Transform expected = string.IsNullOrEmpty(link.manifestGuid) ? asset.transform : asset.transform.Find("LOD0");
            if (actual == null || expected == null) throw new InvalidDataException($"'{instance.name}' has no production LOD0.");
            string[] meshGuids;
            if (string.IsNullOrEmpty(link.manifestGuid)) meshGuids = new[] { link.meshGuid };
            else
            {
                var manifest = VoxelProductionFamily.Load(AssetDatabase.GUIDToAssetPath(link.manifestGuid));
                if (manifest.lods[0].sourceGuid != link.sourceGuid)
                    throw new InvalidDataException("The production link and LOD0 manifest reference different sources.");
                meshGuids = manifest.lods[0].meshGuids;
            }
            var owned = new HashSet<Mesh>((meshGuids ?? Array.Empty<string>()).Select(guid =>
                AssetDatabase.LoadAssetAtPath<Mesh>(AssetDatabase.GUIDToAssetPath(guid))));
            if (owned.Count == 0 || owned.Contains(null)) throw new InvalidDataException("A production chunk mesh is missing. Restore it before combining.");
            var a = actual.GetComponentsInChildren<MeshRenderer>(true);
            var b = expected.GetComponentsInChildren<MeshRenderer>(true);
            if (a.Length != b.Length) throw new InvalidDataException($"'{instance.name}' has modified LOD0 renderers. Edit the source VOX and rebuild first.");
            for (int i = 0; i < a.Length; i++)
            {
                var af = a[i].GetComponent<MeshFilter>(); var bf = b[i].GetComponent<MeshFilter>();
                if (af == null || bf == null || af.sharedMesh != bf.sharedMesh || a[i].enabled != b[i].enabled ||
                    !a[i].sharedMaterials.SequenceEqual(b[i].sharedMaterials) ||
                    a[i].gameObject.activeSelf != b[i].gameObject.activeSelf)
                    throw new InvalidDataException($"'{instance.name}' has modified LOD0 geometry or materials. Edit the source VOX and rebuild first.");
                // Compare hierarchy-local matrices to avoid precision loss from large world translations.
                Matrix4x4 am = RelativeMatrix(instance.transform, a[i].transform);
                Matrix4x4 bm = RelativeMatrix(asset.transform, b[i].transform);
                if (RelativeActive(instance.transform, a[i].transform) != RelativeActive(asset.transform, b[i].transform))
                    throw new InvalidDataException($"'{instance.name}' has disabled LOD0 children. Restore their generated active state.");
                for (int n = 0; n < 16; n++) if (Mathf.Abs(am[n] - bm[n]) > 0.00001f)
                    throw new InvalidDataException($"'{instance.name}' has transformed LOD0 children. Transform the prefab root instead.");
                Transform retained = actual.Find(VoxelRetainedGeometry.ChildName);
                if (retained == null || !a[i].transform.IsChildOf(retained))
                {
                    if (bm != Matrix4x4.identity || !b[i].enabled || !RelativeActive(asset.transform, b[i].transform))
                        throw new InvalidDataException($"'{instance.name}' has transformed or disabled voxel chunks. Restore their generated state.");
                    if (!owned.Remove(af.sharedMesh))
                        throw new InvalidDataException($"'{instance.name}' has extra or replaced production meshes. Restore its generated LOD0 structure.");
                }
            }
            if (owned.Count > 0) throw new InvalidDataException($"'{instance.name}' is missing production chunk renderers.");
        }

        private static Matrix4x4 RelativeMatrix(Transform root, Transform child)
        {
            Matrix4x4 matrix = Matrix4x4.identity;
            for (Transform node = child; node != root; node = node.parent)
                matrix = Matrix4x4.TRS(node.localPosition, node.localRotation, node.localScale) * matrix;
            return matrix;
        }

        private static bool RelativeActive(Transform root, Transform child)
        {
            for (var node = child; node != root; node = node.parent) if (!node.gameObject.activeSelf) return false;
            return true;
        }

        internal static VoxelLodBuildResult Build(VoxelCombineAnalysis analysis, VoxelStyleProfile profile,
            string outputFolder, string name, bool generateLods, bool acceptConflicts, Action<float> progress = null)
        {
            if (profile == null || !profile.TryValidate(out _))
                throw new InvalidDataException("A valid voxel style profile is required.");
            if (analysis.Grid.VoxelSize != profile.BaseVoxelSize)
                throw new InvalidDataException("The voxel profile changed. Analyze the combination again.");
            if (analysis.Conflicts > 0 && !acceptConflicts) throw new InvalidOperationException("Conflicting overlaps require explicit confirmation.");
            if (!VoxelLodPipeline.IsAssetFolder(outputFolder)) throw new ArgumentException("Choose an Assets output folder.");
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Enter a family name.");
            name = VoxelLodPipeline.MakeSafeFileName(name);
            VoxelLodPipeline.EnsureAssetFolder(outputFolder);
            string folder = AssetDatabase.GenerateUniqueAssetPath(outputFolder + "/" + name + "_VoxelLOD");
            VoxelLodPipeline.EnsureAssetFolder(folder);
            string manifestPath = folder + "/" + name + ".voxset.json";
            try
            {
                VoxelLodBatchRecovery.MarkFamilyIncomplete(folder, name);
                progress?.Invoke(0);
                string retained = CopyRetained(analysis, folder);
                var metadata = JsonUtility.FromJson<VoxelBridgeMetadata>(JsonUtility.ToJson(analysis.Sources[0].Metadata));
                // The union is already expressed in its own world-aligned basis.
                metadata.sourceAxes = VoxelSourceAxes.PreserveLocalAxes;
                metadata.baseVoxelSize = profile.BaseVoxelSize;
                metadata.retainedGeometryGuid = retained;
                // A combined asset can contain colors selected through several mapping profiles.
                metadata.semantic.colorMappingProfileGuid = null;
                metadata.semantic.colorMappingProfileAssetPath = null;
                string sourcePath = folder + "/" + name + "_LOD0.vox";
                var grid = analysis.Grid;
                var bounds = new Bounds(grid.Origin + (Vector3)grid.Size * grid.VoxelSize * 0.5f, (Vector3)grid.Size * grid.VoxelSize);
                var manifest = new VoxelLodSetManifest { familyId = Guid.NewGuid().ToString("N"), sourceName = name,
                    baseVoxelSize = profile.BaseVoxelSize, chunkCellSize = profile.ChunkCellSize,
                    profileAssetPath = AssetDatabase.GetAssetPath(profile), retainedGeometryGuid = retained,
                    lods = new[] { new VoxelLodEntry { voxAssetPath = sourcePath } } };
                VoxelLodPipeline.WriteGrid(sourcePath, grid, bounds, name, null, manifest.familyId, manifestPath,
                    0, 1, VoxelLodGenerationMode.SourceMesh, null, profile, metadata);
                string sidecar = VoxelImporterIntegration.GetMetadataAssetPath(sourcePath);
                AssetDatabase.ImportAsset(sidecar, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(sourcePath, ImportAssetOptions.ForceSynchronousImport);
                if (!VoxelImporterIntegration.ApplyAndReimport(sourcePath, out string error, forceReimport: true))
                    throw new InvalidDataException(error);
                VoxelLodPipeline.SaveManifest(manifestPath, manifest);
                int count = generateLods ? profile.LodCount : 1;
                VoxelProductionFamily.BuildConvertedFamily(manifestPath, profile, p => progress?.Invoke(p / count));
                for (int i = 1; i < count; i++)
                {
                    int level = i;
                    VoxelProductionFamily.DeriveLevel(manifestPath, i, VoxelLodGenerationMode.ReduceParent,
                        progress: p => progress?.Invoke((level + p) / count));
                }
                manifest = VoxelProductionFamily.Load(manifestPath);
                VoxelLodBatchRecovery.CompleteFamily(folder);
                return new VoxelLodBuildResult(manifestPath, manifest.prefabAssetPath,
                    manifest.lods.Select(VoxelProductionFamily.SourcePath).ToArray());
            }
            catch { AssetDatabase.DeleteAsset(folder); throw; }
        }

        private static string CopyRetained(VoxelCombineAnalysis analysis, string folder)
        {
            GameObject root = null;
            var meshes = new Dictionary<Mesh, Mesh>();
            try
            {
                foreach (var source in analysis.Sources)
                {
                    var asset = VoxelRetainedGeometry.Resolve(source.Metadata.retainedGeometryGuid);
                    if (asset == null) continue;
                    if (root == null) root = new GameObject(VoxelRetainedGeometry.ChildName);
                    var placement = new GameObject(source.Instance.name);
                    placement.transform.SetParent(root.transform, false);
                    var matrix = source.LocalToOutput;
                    Vector3 scale = new Vector3(matrix.GetColumn(0).magnitude, matrix.GetColumn(1).magnitude, matrix.GetColumn(2).magnitude);
                    if (matrix.determinant < 0) scale.x = -scale.x;
                    placement.transform.localPosition = matrix.GetColumn(3);
                    placement.transform.localRotation = Quaternion.LookRotation(matrix.GetColumn(2), matrix.GetColumn(1));
                    placement.transform.localScale = scale;
                    var copy = Object.Instantiate(asset, placement.transform, false);
                    foreach (var component in copy.GetComponentsInChildren<Component>(true))
                        if (component != null && !(component is Transform) && !(component is MeshFilter) && !(component is MeshRenderer))
                            Object.DestroyImmediate(component);
                    foreach (var filter in copy.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (!meshes.TryGetValue(filter.sharedMesh, out Mesh owned))
                        {
                            owned = Object.Instantiate(filter.sharedMesh);
                            owned.name = "Retained_" + meshes.Count;
                            AssetDatabase.CreateAsset(owned, folder + "/" + owned.name + ".asset");
                            meshes.Add(filter.sharedMesh, owned);
                        }
                        filter.sharedMesh = owned;
                    }
                }
                if (root == null) return null;
                string path = folder + "/RetainedGeometry.prefab";
                if (PrefabUtility.SaveAsPrefabAsset(root, path) == null) throw new IOException("Could not save combined retained geometry.");
                return AssetDatabase.AssetPathToGUID(path);
            }
            finally { if (root != null) Object.DestroyImmediate(root); }
        }

        internal static GameObject Place(VoxelCombineAnalysis analysis, VoxelLodBuildResult result, bool disableSources)
        {
            if (analysis.Sources.Any(s => s.Instance == null)) throw new InvalidOperationException("A source instance was removed.");
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Place Combined Voxel Family");
            try
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(result.PrefabAssetPath);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, analysis.Sources[0].Instance.scene);
                Undo.RegisterCreatedObjectUndo(instance, "Place Combined Voxel Family");
                instance.transform.position = analysis.Pivot;
                PrefabUtility.RecordPrefabInstancePropertyModifications(instance.transform);
                if (disableSources) foreach (var source in analysis.Sources)
                {
                    Undo.RecordObject(source.Instance, "Disable Voxel Source");
                    source.Instance.SetActive(false);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(source.Instance);
                }
                Undo.CollapseUndoOperations(group);
                return instance;
            }
            catch { Undo.RevertAllDownToGroup(group); throw; }
        }
    }
}
