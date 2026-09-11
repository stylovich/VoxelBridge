using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge
{
    internal static class VoxelProductionFamily
    {
        internal static string Create(string sourcePath, VoxelStyleProfile profile, string outputFolder,
            Action<float> progress = null)
        {
            ValidateProfile(profile);
            if (!VoxelLodPipeline.IsAssetFolder(outputFolder)) throw new ArgumentException("Choose an Assets folder.");
            VoxelProductionExporter.ReadGrid(sourcePath, out var metadata, out _, out _, out _);
            if (metadata.lodIndex != 0) throw new InvalidDataException("Start a production family from semantic LOD0.");
            string sourceGuid = AssetDatabase.AssetPathToGUID(sourcePath);
            if (!string.IsNullOrEmpty(metadata.lodSetAssetPath) &&
                VoxelLodPipeline.TryReadManifest(metadata.lodSetAssetPath, out var owner) && owner.productionMeshes)
            {
                owner = Load(metadata.lodSetAssetPath);
                if (owner.lods[0].sourceGuid != sourceGuid)
                    throw new InvalidDataException("The source does not belong to the linked production family.");
                return metadata.lodSetAssetPath;
            }
            string existing = null;
            if (AssetDatabase.IsValidFolder(outputFolder))
                foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { outputFolder }))
                {
                    string prefabPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (!VoxelProductionLink.HasLink(prefabPath)) continue;
                    var link = VoxelProductionLink.Load(prefabPath);
                    if (link.sourceGuid != sourceGuid || string.IsNullOrEmpty(link.manifestGuid)) continue;
                    if (existing != null) throw new InvalidDataException("Several production families use this source. Select the intended prefab.");
                    existing = AssetDatabase.GUIDToAssetPath(link.manifestGuid);
                }
            if (existing != null) { Load(existing); return existing; }
            VoxelLodPipeline.EnsureAssetFolder(outputFolder);
            string name = VoxelLodPipeline.MakeSafeFileName(metadata.sourceName);
            string folder = AssetDatabase.GenerateUniqueAssetPath(outputFolder + "/" + name + "_ProductionLODs");
            VoxelLodPipeline.EnsureAssetFolder(folder);
            string path = folder + "/" + name + ".voxset.json";
            try
            {
                AssetDatabase.ImportAsset(sourcePath, ImportAssetOptions.ForceSynchronousImport);
                var manifest = new VoxelLodSetManifest
                {
                    sourceAxes = metadata.sourceAxes,
                    productionMeshes = true, familyId = Guid.NewGuid().ToString("N"), sourceName = metadata.sourceName,
                    sourceAssetPath = metadata.sourceAssetPath, baseVoxelSize = metadata.voxelSize,
                    retainedGeometryGuid = metadata.retainedGeometryGuid,
                    initialVoxelMultiplier = 1, chunkCellSize = profile.ChunkCellSize,
                    profileAssetPath = AssetDatabase.GetAssetPath(profile),
                    profileGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(profile)),
                    prefabAssetPath = folder + "/" + name + ".prefab",
                    lods = new[] { new VoxelLodEntry { lodIndex = 0, multiplier = 1, voxAssetPath = sourcePath,
                        sourceGuid = AssetDatabase.AssetPathToGUID(sourcePath) } }
                };
                VoxelLodPipeline.SaveManifest(path, manifest);
                RebuildLevel(path, 0, progress);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(manifest.prefabAssetPath);
                VoxelProductionLink.Store(prefab, sourcePath, null, path);
                manifest = Load(path);
                manifest.prefabGuid = AssetDatabase.AssetPathToGUID(manifest.prefabAssetPath);
                VoxelLodPipeline.SaveManifest(path, manifest);
                return path;
            }
            catch { AssetDatabase.DeleteAsset(folder); throw; }
        }

        internal static VoxelLodSetManifest Load(string manifestPath)
        {
            if (!VoxelLodPipeline.TryReadManifest(manifestPath, out var manifest) || !manifest.productionMeshes ||
                manifest.lods == null || manifest.lods.Length == 0 || manifest.lods.Length > 8)
                throw new InvalidDataException("A production LOD manifest is required.");
            for (int i = 0; i < manifest.lods.Length; i++)
                if (manifest.lods[i] == null || manifest.lods[i].lodIndex != i ||
                    string.IsNullOrEmpty(manifest.lods[i].sourceGuid))
                    throw new InvalidDataException("Production levels must be contiguous and have source GUIDs.");
            if (!string.IsNullOrEmpty(manifest.prefabGuid))
            {
                manifest.prefabAssetPath = AssetDatabase.GUIDToAssetPath(manifest.prefabGuid);
                if (string.IsNullOrEmpty(manifest.prefabAssetPath)) throw new InvalidDataException("The production prefab is missing. Restore its asset and .meta file.");
            }
            return manifest;
        }

        // The caller owns the incomplete family transaction and removes it if any level fails.
        internal static string BuildConvertedFamily(string manifestPath, VoxelStyleProfile profile,
            Action<float> progress = null)
        {
            ValidateProfile(profile);
            if (!VoxelLodPipeline.TryReadManifest(manifestPath, out var manifest) || manifest.productionMeshes ||
                manifest.lods == null || manifest.lods.Length == 0 || !string.IsNullOrEmpty(manifest.prefabAssetPath))
                throw new InvalidDataException("Production initialization requires a new converted family without a prefab.");
            string folder = Path.GetDirectoryName(manifestPath).Replace('\\', '/');
            manifest.productionMeshes = true;
            manifest.profileGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(profile));
            manifest.prefabAssetPath = folder + "/" + VoxelLodPipeline.MakeSafeFileName(manifest.sourceName) + ".prefab";
            if (File.Exists(VoxelLodPipeline.AssetPathToAbsolute(manifest.prefabAssetPath)))
                throw new InvalidDataException("The production prefab output already exists.");
            foreach (var entry in manifest.lods)
                entry.sourceGuid = AssetDatabase.AssetPathToGUID(entry.voxAssetPath);
            VoxelLodPipeline.SaveManifest(manifestPath, manifest);
            for (int i = 0; i < manifest.lods.Length; i++)
            {
                int level = i;
                progress?.Invoke((float)level / manifest.lods.Length);
                RebuildLevel(manifestPath, level, value => progress?.Invoke((level + value) / manifest.lods.Length));
            }
            manifest = Load(manifestPath);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(manifest.prefabAssetPath);
            VoxelProductionLink.Store(prefab, SourcePath(manifest.lods[0]), null, manifestPath);
            manifest.prefabGuid = AssetDatabase.AssetPathToGUID(manifest.prefabAssetPath);
            VoxelLodPipeline.SaveManifest(manifestPath, manifest);
            return manifest.prefabAssetPath;
        }

        internal static string SourcePath(VoxelLodEntry entry)
        {
            string path = AssetDatabase.GUIDToAssetPath(entry.sourceGuid);
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".vox", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(VoxelLodPipeline.AssetPathToAbsolute(path)))
                throw new InvalidDataException($"LOD{entry.lodIndex} source is missing. Restore its VOX and .meta file.");
            return path;
        }

        internal static string SourceHash(VoxelLodEntry entry)
        {
            string path = SourcePath(entry);
            return Hash128.Compute(AssetDatabase.GetAssetDependencyHash(path) + ":" +
                AssetDatabase.GetAssetDependencyHash(VoxelImporterIntegration.GetMetadataAssetPath(path))).ToString();
        }

        internal static bool IsDerivedStale(VoxelLodSetManifest manifest, int level)
        {
            for (int i = level; i > 0; i--)
            {
                if (manifest.lods[i].generationMode == VoxelLodGenerationMode.SourceMesh) break;
                if (manifest.lods[i].parentSourceHash != SourceHash(manifest.lods[i - 1])) return true;
            }
            return false;
        }

        internal static VoxelStyleProfile Profile(VoxelLodSetManifest manifest)
        {
            var profile = AssetDatabase.LoadAssetAtPath<VoxelStyleProfile>(AssetDatabase.GUIDToAssetPath(manifest.profileGuid));
            ValidateProfile(profile);
            return profile;
        }

        private static void ValidateProfile(VoxelStyleProfile profile)
        {
            if (profile == null || string.IsNullOrEmpty(AssetDatabase.GetAssetPath(profile)))
                throw new InvalidDataException("Assign a saved voxel style profile.");
            if (!profile.TryValidate(out string error)) throw new InvalidDataException(error);
        }

        internal static float ReductionVoxelSize(VoxelLodSetManifest manifest, VoxelStyleProfile profile,
            int level, float parentSize, out int multiplier)
        {
            if (level < 1 || level > manifest.lods.Length || level >= profile.LodCount)
                throw new ArgumentException("The profile must define a next LOD for this source.");
            multiplier = checked(Math.Max(1, manifest.initialVoxelMultiplier) * profile.GetLodMultiplier(level));
            float size = manifest.baseVoxelSize * multiplier;
            if (!float.IsFinite(size) || size <= parentSize)
                throw new InvalidOperationException("The target voxel size must exceed the previous level's size.");
            return size;
        }

        internal static void DeriveLevel(string manifestPath, int level, VoxelLodGenerationMode mode,
            bool replaceExisting = false, Action<float> progress = null)
        {
            var manifest = Load(manifestPath);
            var profile = Profile(manifest);
            if (level < 1 || level > manifest.lods.Length || level >= profile.LodCount ||
                (mode != VoxelLodGenerationMode.DuplicateParent && mode != VoxelLodGenerationMode.ReduceParent))
                throw new ArgumentException("Choose the next level and a duplicate or reduce operation.");
            bool replacing = level < manifest.lods.Length;
            if (replacing && !replaceExisting) throw new InvalidOperationException("Replacing an authored LOD requires explicit confirmation.");
            string parentPath = SourcePath(manifest.lods[level - 1]);
            VoxelGrid parentGrid = VoxelProductionExporter.ReadGrid(parentPath, out var parent, out _, out _, out _);
            string folder = Path.GetDirectoryName(manifestPath).Replace('\\', '/');
            string targetPath = replacing ? SourcePath(manifest.lods[level]) :
                AssetDatabase.GenerateUniqueAssetPath(folder + $"/LOD{level}.vox");
            string sidecarPath = VoxelImporterIntegration.GetMetadataAssetPath(targetPath);
            string absolute = VoxelLodPipeline.AssetPathToAbsolute(targetPath);
            string absoluteSidecar = VoxelLodPipeline.AssetPathToAbsolute(sidecarPath);
            byte[] previousVox = replacing ? File.ReadAllBytes(absolute) : null;
            byte[] previousSidecar = replacing ? File.ReadAllBytes(absoluteSidecar) : null;
            string previousManifest = File.ReadAllText(VoxelLodPipeline.AssetPathToAbsolute(manifestPath));
            try
            {
                progress?.Invoke(0);
                int multiplier = manifest.lods[level - 1].multiplier;
                float size = mode == VoxelLodGenerationMode.DuplicateParent ? parentGrid.VoxelSize :
                    ReductionVoxelSize(manifest, profile, level, parentGrid.VoxelSize, out multiplier);
                if (mode == VoxelLodGenerationMode.DuplicateParent)
                {
                    File.Copy(VoxelLodPipeline.AssetPathToAbsolute(parentPath), absolute, replacing);
                    var copy = JsonUtility.FromJson<VoxelBridgeMetadata>(JsonUtility.ToJson(parent));
                    copy.familyId = manifest.familyId; copy.lodSetAssetPath = manifestPath;
                    copy.lodIndex = level; copy.lodMultiplier = multiplier; copy.lodGenerationMode = mode;
                    copy.baseVoxelSize = manifest.baseVoxelSize; copy.parentVoxAssetPath = parentPath;
                    File.WriteAllText(absoluteSidecar, JsonUtility.ToJson(copy, true));
                }
                else
                {
                    VoxelGrid reduced = VoxelGridDownsampler.Downsample(parentGrid, size, profile.Padding,
                        manifest.chunkCellSize, parent.hideInternalCavities, progress);
                    VoxelSemanticMesher.ValidateGrid(reduced.Size, reduced.Origin, reduced.VoxelSize);
                    parent.baseVoxelSize = manifest.baseVoxelSize;
                    Bounds bounds = new Bounds((parent.sourceBoundsMin + parent.sourceBoundsMax) * 0.5f,
                        parent.sourceBoundsMax - parent.sourceBoundsMin);
                    VoxelLodPipeline.WriteGrid(targetPath, reduced, bounds, manifest.sourceName,
                        manifest.sourceAssetPath, manifest.familyId, manifestPath, level, multiplier,
                        mode, parentPath, profile, parent);
                }
                AssetDatabase.ImportAsset(sidecarPath, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceSynchronousImport);
                var entry = new VoxelLodEntry { lodIndex = level, multiplier = multiplier, generationMode = mode,
                    voxAssetPath = targetPath, sourceGuid = AssetDatabase.AssetPathToGUID(targetPath),
                    parentSourceHash = SourceHash(manifest.lods[level - 1]),
                    meshGuids = replacing ? manifest.lods[level].meshGuids : null };
                if (replacing) manifest.lods[level] = entry;
                else manifest.lods = manifest.lods.Append(entry).ToArray();
                VoxelLodPipeline.SaveManifest(manifestPath, manifest);
                RebuildLevel(manifestPath, level, progress);
            }
            catch
            {
                if (replacing)
                {
                    File.WriteAllBytes(absolute, previousVox); File.WriteAllBytes(absoluteSidecar, previousSidecar);
                    AssetDatabase.ImportAsset(sidecarPath, ImportAssetOptions.ForceSynchronousImport);
                    AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceSynchronousImport);
                }
                else
                {
                    if (File.Exists(absolute)) { AssetDatabase.ImportAsset(targetPath); AssetDatabase.DeleteAsset(targetPath); }
                    if (File.Exists(absoluteSidecar)) { AssetDatabase.ImportAsset(sidecarPath); AssetDatabase.DeleteAsset(sidecarPath); }
                }
                File.WriteAllText(VoxelLodPipeline.AssetPathToAbsolute(manifestPath), previousManifest);
                AssetDatabase.ImportAsset(manifestPath, ImportAssetOptions.ForceSynchronousImport);
                throw;
            }
        }

        internal static void RebuildAll(string manifestPath, Action<float> progress = null)
        {
            var manifest = Load(manifestPath);
            // Rebuild existing sources only. Never regenerate authored VOX files implicitly.
            for (int i = 0; i < manifest.lods.Length; i++) RebuildLevel(manifestPath, i, progress);
        }

        internal static void RebuildLevel(string manifestPath, int level, Action<float> progress = null)
        {
            var manifest = Load(manifestPath);
            var profile = Profile(manifest);
            if (level < 0 || level >= manifest.lods.Length) throw new ArgumentOutOfRangeException(nameof(level));
            if (!string.IsNullOrEmpty(manifest.impostor?.assetPath)) throw new InvalidOperationException("Remove the impostor before rebuilding production geometry, then bake it again.");
            VoxelLodEntry entry = manifest.lods[level];
            VoxelGrid grid = VoxelProductionExporter.ReadGrid(SourcePath(entry), out var metadata,
                out var colors, out var surfaces, out var shader);
            if (!VoxelImporterIntegration.TryLoadMetadata(SourcePath(manifest.lods[0]), out var first, out string error))
                throw new InvalidDataException(error);
            if (metadata.semantic.colorPaletteGuid != first.semantic.colorPaletteGuid ||
                metadata.semantic.surfacePaletteGuid != first.semantic.surfacePaletteGuid)
                throw new InvalidDataException("All levels must use the same global palette pair.");
            Mesh[] generated = VoxelSemanticMesher.BuildChunks(grid, manifest.chunkCellSize, metadata.hideInternalCavities, progress);
            var oldMeshes = new Dictionary<string, Mesh>();
            var backups = new Dictionary<Mesh, Mesh>();
            var created = new List<string>();
            GameObject root = null, backupRoot = null;
            bool existingPrefab = File.Exists(VoxelLodPipeline.AssetPathToAbsolute(manifest.prefabAssetPath));
            string previousManifest = File.ReadAllText(VoxelLodPipeline.AssetPathToAbsolute(manifestPath));
            bool prefabSaved = false;
            try
            {
                foreach (string guid in entry.meshGuids ?? Array.Empty<string>())
                {
                    Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(AssetDatabase.GUIDToAssetPath(guid));
                    if (mesh == null) throw new InvalidDataException("A linked chunk mesh is missing. Restore its asset before rebuilding.");
                    oldMeshes.Add(mesh.name, mesh);
                    backups.Add(mesh, Object.Instantiate(mesh));
                }
                Material material = VoxelProductionExporter.GetSharedMaterial(colors, surfaces, shader, out string materialPath);
                if (materialPath != null) created.Add(materialPath);
                root = existingPrefab ? PrefabUtility.LoadPrefabContents(manifest.prefabAssetPath) : new GameObject(manifest.sourceName);
                if (existingPrefab) { backupRoot = Object.Instantiate(root); backupRoot.name = root.name; backupRoot.hideFlags = HideFlags.HideAndDontSave; }
                Transform levelRoot = root.transform.Find("LOD" + level);
                if (existingPrefab && !string.IsNullOrEmpty(manifest.prefabGuid))
                {
                    if (VoxelProductionLink.Load(manifest.prefabAssetPath).manifestGuid != AssetDatabase.AssetPathToGUID(manifestPath))
                        throw new InvalidDataException("The prefab belongs to another production manifest.");
                    foreach (Mesh mesh in oldMeshes.Values)
                    {
                        Transform chunk = levelRoot != null ? levelRoot.Find(mesh.name) : null;
                        MeshFilter filter = chunk != null ? chunk.GetComponent<MeshFilter>() : null;
                        if (filter == null || filter.sharedMesh != mesh)
                            throw new InvalidDataException("A generated chunk or mesh reference was replaced. Restore its reference before rebuilding.");
                    }
                }
                if (levelRoot == null) { var child = new GameObject("LOD" + level); child.transform.SetParent(root.transform, false); levelRoot = child.transform; }
                var saved = new List<Mesh>();
                VoxelProductionExporter.RegisterMeshUndo(oldMeshes.Values.ToArray());
                string folder = Path.GetDirectoryName(manifestPath).Replace('\\', '/');
                foreach (Mesh mesh in generated)
                {
                    if (oldMeshes.TryGetValue(mesh.name, out Mesh target))
                    {
                        VoxelProductionExporter.ReplaceMeshData(mesh, target); oldMeshes.Remove(mesh.name);
                        EditorUtility.SetDirty(target); saved.Add(target);
                    }
                    else
                    {
                        string assetPath = AssetDatabase.GenerateUniqueAssetPath(folder + $"/LOD{level}_{mesh.name}.asset");
                        string chunkName = mesh.name;
                        AssetDatabase.CreateAsset(mesh, assetPath); mesh.name = chunkName; EditorUtility.SetDirty(mesh);
                        created.Add(assetPath); saved.Add(mesh);
                    }
                }
                // Keep emptied chunks as empty assets so later edits can reuse their GUIDs and scene references.
                foreach (Mesh mesh in oldMeshes.Values) { mesh.Clear(false); mesh.MarkModified(); EditorUtility.SetDirty(mesh); saved.Add(mesh); }
                foreach (Mesh mesh in saved)
                {
                    Transform child = levelRoot.Find(mesh.name);
                    if (child == null) { var go = new GameObject(mesh.name); go.transform.SetParent(levelRoot, false); child = go.transform; }
                    var filter = child.GetComponent<MeshFilter>();
                    if (filter == null) filter = child.gameObject.AddComponent<MeshFilter>();
                    var renderer = child.GetComponent<MeshRenderer>();
                    if (renderer == null) renderer = child.gameObject.AddComponent<MeshRenderer>();
                    if (renderer.sharedMaterial != null && (renderer.sharedMaterial.shader != shader ||
                        renderer.sharedMaterial.GetTexture("_PaletteColor") != colors.GeneratedLut ||
                        renderer.sharedMaterial.GetTexture("_PaletteSurface") != surfaces.GeneratedLut))
                        throw new InvalidDataException("A chunk material uses an incompatible shader or palette pair.");
                    filter.sharedMesh = mesh;
                    if (renderer.sharedMaterial == null) renderer.sharedMaterial = material;
                }
                entry.meshGuids = saved.Select(mesh => AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(mesh))).ToArray();
                VoxelRetainedGeometry.Attach(levelRoot, manifest.retainedGeometryGuid);
                ConfigureGroup(root, manifest, profile);
                foreach (Mesh mesh in saved) AssetDatabase.SaveAssetIfDirty(mesh);
                if (PrefabUtility.SaveAsPrefabAsset(root, manifest.prefabAssetPath) == null) throw new IOException("Could not save the production prefab.");
                prefabSaved = true;
                entry.voxAssetPath = SourcePath(entry); entry.builtSourceHash = SourceHash(entry);
                VoxelLodPipeline.SaveManifest(manifestPath, manifest);
                SceneView.RepaintAll();
            }
            catch
            {
                foreach (var pair in backups)
                { pair.Value.name = pair.Key.name; VoxelProductionExporter.ReplaceMeshData(pair.Value, pair.Key); EditorUtility.SetDirty(pair.Key); AssetDatabase.SaveAssetIfDirty(pair.Key); }
                if (prefabSaved && backupRoot != null) { backupRoot.hideFlags = HideFlags.None; PrefabUtility.SaveAsPrefabAsset(backupRoot, manifest.prefabAssetPath); }
                if (prefabSaved && !existingPrefab) AssetDatabase.DeleteAsset(manifest.prefabAssetPath);
                foreach (string path in created) AssetDatabase.DeleteAsset(path);
                File.WriteAllText(VoxelLodPipeline.AssetPathToAbsolute(manifestPath), previousManifest);
                AssetDatabase.ImportAsset(manifestPath, ImportAssetOptions.ForceSynchronousImport);
                throw;
            }
            finally
            {
                if (root != null) { if (existingPrefab) PrefabUtility.UnloadPrefabContents(root); else Object.DestroyImmediate(root); }
                if (backupRoot != null) Object.DestroyImmediate(backupRoot);
                foreach (Mesh backup in backups.Values) Object.DestroyImmediate(backup);
                foreach (Mesh mesh in generated) if (mesh != null && !AssetDatabase.Contains(mesh)) Object.DestroyImmediate(mesh);
            }
        }

        private static void ConfigureGroup(GameObject root, VoxelLodSetManifest manifest, VoxelStyleProfile profile)
        {
            var group = root.GetComponent<LODGroup>();
            if (group == null) group = root.AddComponent<LODGroup>();
            var lods = manifest.lods.Select(entry =>
            {
                var child = root.transform.Find("LOD" + entry.lodIndex);
                Renderer[] renderers = child == null ? Array.Empty<Renderer>() : child.GetComponentsInChildren<MeshRenderer>(true)
                    .Where(r => { var filter = r.GetComponent<MeshFilter>(); return filter != null && filter.sharedMesh != null && filter.sharedMesh.vertexCount > 0; }).Cast<Renderer>().ToArray();
                return new LOD(profile.GetLodScreenHeight(entry.lodIndex), renderers);
            }).ToArray();
            group.fadeMode = LODFadeMode.None; group.SetLODs(lods); group.RecalculateBounds();
            manifest.lodGroupSize = group.size;
            int firstShadowless = profile.GetFirstShadowlessVoxelLodIndex(group.size, lods.Length);
            for (int i = 0; i < lods.Length; i++)
            {
                float height = profile.GetLodScreenHeight(i, group.size);
                manifest.lods[i].screenRelativeTransitionHeight = height;
                lods[i].screenRelativeTransitionHeight = height;
                foreach (Renderer renderer in lods[i].renderers)
                    renderer.shadowCastingMode = firstShadowless >= 0 && i >= firstShadowless ? ShadowCastingMode.Off : ShadowCastingMode.On;
            }
            group.SetLODs(lods);
        }
    }
}
