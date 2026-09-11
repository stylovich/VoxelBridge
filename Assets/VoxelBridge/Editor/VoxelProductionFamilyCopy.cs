using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge
{
    // Copies saved authoring resources, never regenerates geometry or modifies the source family.
    internal static class VoxelProductionFamilyCopy
    {
        internal static string Duplicate(string prefabPath, string outputParent, Action<float> progress = null)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Duplicate an editable family in Edit Mode.");
            ValidateParent(outputParent);
            var link = VoxelProductionLink.Load(prefabPath);
            if (string.IsNullOrEmpty(link.manifestGuid))
                throw new InvalidDataException("Create a LOD family before duplicating this standalone production prefab.");
            string sourceManifest = AssetDatabase.GUIDToAssetPath(link.manifestGuid);
            var manifest = VoxelProductionFamily.Load(sourceManifest);
            if (manifest.prefabAssetPath != prefabPath || manifest.lods[0].sourceGuid != link.sourceGuid)
                throw new InvalidDataException("The production prefab and manifest do not own the same source family.");
            if (!string.IsNullOrEmpty(manifest.impostor?.assetPath))
                throw new InvalidOperationException("Families with baked impostors cannot be duplicated yet.");

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            ValidatePrefab(prefab);
            VoxelProductionFamily.Profile(manifest);
            var retained = VoxelRetainedGeometry.Resolve(manifest.retainedGeometryGuid);
            if (retained != null) ValidatePrefab(retained);
            var fingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Remember(string path)
            {
                if (!File.Exists(VoxelLodPipeline.AssetPathToAbsolute(path)))
                    throw new InvalidDataException("A required source asset is missing. Restore it before duplicating: " + path);
                if (!fingerprints.ContainsKey(path)) fingerprints.Add(path, FileHash(path));
            }
            Remember(prefabPath); Remember(sourceManifest);
            if (retained != null) Remember(AssetDatabase.GetAssetPath(retained));

            int count = manifest.lods.Length;
            var sources = new string[count];
            var metadata = new VoxelBridgeMetadata[count];
            var freshMeshes = new bool[count];
            var freshParents = new bool[count];
            var meshes = new HashSet<Mesh>();
            for (int i = 0; i < count; i++)
            {
                progress?.Invoke(.15f * i / count);
                var entry = manifest.lods[i];
                sources[i] = VoxelProductionFamily.SourcePath(entry);
                if (sources.Take(i).Contains(sources[i])) throw new InvalidDataException("Each LOD must own a distinct source VOX.");
                Remember(sources[i]); Remember(VoxelImporterIntegration.GetMetadataAssetPath(sources[i]));
                VoxelProductionExporter.ReadGrid(sources[i], out metadata[i], out _, out _, out _);
                if ((metadata[i].retainedGeometryGuid ?? "") != (manifest.retainedGeometryGuid ?? ""))
                    throw new InvalidDataException("The source and family disagree about retained geometry.");
                freshMeshes[i] = entry.builtSourceHash == VoxelProductionFamily.SourceHash(entry);
                freshParents[i] = i > 0 && entry.parentSourceHash == VoxelProductionFamily.SourceHash(manifest.lods[i - 1]);
                if (entry.meshGuids == null || entry.meshGuids.Length == 0)
                    throw new InvalidDataException("The LOD has no saved chunk mesh links. Rebuild it before duplicating.");
                foreach (string guid in entry.meshGuids)
                {
                    var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(AssetDatabase.GUIDToAssetPath(guid));
                    if (mesh == null) throw new InvalidDataException("A linked chunk mesh is missing. Restore it before duplicating.");
                    var filter = prefab.transform.Find($"LOD{i}/{mesh.name}")?.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh != mesh)
                        throw new InvalidDataException("A generated chunk reference was replaced. Restore it before duplicating.");
                    meshes.Add(mesh);
                }
                string report = Path.ChangeExtension(sources[i], ".surface-report.json");
                if (File.Exists(VoxelLodPipeline.AssetPathToAbsolute(report))) Remember(report);
            }
            // Retained snapshots can reference imported submeshes; copy each mesh as an independent asset.
            if (retained != null)
            {
                foreach (var filter in retained.GetComponentsInChildren<MeshFilter>(true))
                    if (filter.sharedMesh != null) meshes.Add(filter.sharedMesh);
                foreach (var collider in retained.GetComponentsInChildren<MeshCollider>(true))
                    if (collider.sharedMesh != null) meshes.Add(collider.sharedMesh);
                foreach (var renderer in retained.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (renderer.sharedMesh != null) meshes.Add(renderer.sharedMesh);
            }
            foreach (var mesh in meshes)
            {
                if (EditorUtility.IsDirty(mesh)) throw new InvalidOperationException("Save modified mesh assets before duplicating.");
                Remember(AssetDatabase.GetAssetPath(mesh));
            }
            ValidateUnchanged(fingerprints);

            string name = Path.GetFileNameWithoutExtension(prefabPath) + "_Copy";
            string folder = AssetDatabase.GenerateUniqueAssetPath(outputParent + "/" + name + "_VoxelLOD");
            string destination = folder + "/" + name + ".prefab";
            string manifestPath = folder + "/" + name + ".voxset.json";
            var copied = new Dictionary<Object, Object>();
            bool created = false;
            try
            {
                progress?.Invoke(.15f);
                // Only this newly created, validated folder is eligible for rollback.
                if (Directory.Exists(VoxelLodPipeline.AssetPathToAbsolute(folder)) || File.Exists(VoxelLodPipeline.AssetPathToAbsolute(folder)))
                    throw new IOException("The duplicate destination already exists.");
                if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(outputParent, Path.GetFileName(folder))))
                    throw new IOException("Could not create the duplicate folder.");
                created = true;
                int index = 0;
                foreach (var mesh in meshes)
                {
                    var clone = Object.Instantiate(mesh);
                    try
                    {
                        string meshPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + VoxelLodPipeline.MakeSafeFileName(mesh.name) + ".asset");
                        clone.name = mesh.name;
                        AssetDatabase.CreateAsset(clone, meshPath);
                        copied.Add(mesh, clone);
                    }
                    catch { if (!AssetDatabase.Contains(clone)) Object.DestroyImmediate(clone); throw; }
                    progress?.Invoke(.15f + .25f * ++index / Math.Max(1, meshes.Count));
                }
                if (retained != null)
                {
                    string retainedPath = folder + "/RetainedGeometry.prefab";
                    CopyPrefab(AssetDatabase.GetAssetPath(retained), retainedPath, copied);
                    manifest.retainedGeometryGuid = AssetDatabase.AssetPathToGUID(retainedPath);
                    copied.Add(retained, AssetDatabase.LoadAssetAtPath<GameObject>(retainedPath));
                }
                manifest.familyId = Guid.NewGuid().ToString("N");
                manifest.sourceName = name;
                manifest.prefabAssetPath = destination;
                manifest.prefabGuid = null;
                // No runtime references to source files are introduced into the prefab.
                for (int i = 0; i < count; i++)
                {
                    string source = folder + $"/{name}_LOD{i}.vox";
                    var meta = metadata[i];
                    meta.familyId = manifest.familyId; meta.lodSetAssetPath = manifestPath;
                    meta.sourceName = name; meta.lodIndex = i;
                    meta.retainedGeometryGuid = manifest.retainedGeometryGuid;
                    meta.parentVoxAssetPath = i == 0 ? "" : manifest.lods[i - 1].voxAssetPath;
                    string sidecar = VoxelImporterIntegration.GetMetadataAssetPath(source);
                    File.WriteAllText(VoxelLodPipeline.AssetPathToAbsolute(sidecar), JsonUtility.ToJson(meta, true));
                    AssetDatabase.ImportAsset(sidecar, ImportAssetOptions.ForceSynchronousImport);
                    if (!AssetDatabase.CopyAsset(sources[i], source)) throw new IOException("Could not copy source VOX: " + sources[i]);
                    if (FileHash(source) != fingerprints[sources[i]]) throw new IOException("The copied VOX does not match its saved source.");
                    string report = Path.ChangeExtension(sources[i], ".surface-report.json");
                    if (fingerprints.ContainsKey(report) && !AssetDatabase.CopyAsset(report, Path.ChangeExtension(source, ".surface-report.json")))
                        throw new IOException("Could not copy the conversion report.");
                    var entry = manifest.lods[i];
                    entry.voxAssetPath = source; entry.sourceGuid = AssetDatabase.AssetPathToGUID(source);
                    entry.meshGuids = (entry.meshGuids ?? Array.Empty<string>()).Select(guid =>
                        AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(copied[
                            AssetDatabase.LoadAssetAtPath<Mesh>(AssetDatabase.GUIDToAssetPath(guid))]))).ToArray();
                    // A changed sidecar has a new dependency hash. Preserve stale warnings, not old hashes.
                    entry.builtSourceHash = freshMeshes[i] ? VoxelProductionFamily.SourceHash(entry) : "";
                    entry.parentSourceHash = freshParents[i] ? VoxelProductionFamily.SourceHash(manifest.lods[i - 1]) : "";
                    progress?.Invoke(.4f + .4f * (i + 1) / count);
                }
                VoxelLodPipeline.SaveManifest(manifestPath, manifest);
                CopyPrefab(prefabPath, destination, copied);
                var duplicate = AssetDatabase.LoadAssetAtPath<GameObject>(destination);
                // CopyAsset preserves importer userData; replace only the validated production link on the NEW asset.
                var importer = AssetImporter.GetAtPath(destination);
                importer.userData = "";
                EditorUtility.SetDirty(importer);
                AssetDatabase.WriteImportSettingsIfDirty(destination);
                VoxelProductionLink.Store(duplicate, manifest.lods[0].voxAssetPath, null, manifestPath);
                manifest.prefabGuid = AssetDatabase.AssetPathToGUID(destination);
                VoxelLodPipeline.SaveManifest(manifestPath, manifest);
                progress?.Invoke(.95f);
                ValidateUnchanged(fingerprints);
                if (VoxelProductionLink.Load(destination).SourcePath != manifest.lods[0].voxAssetPath)
                    throw new InvalidDataException("The duplicate did not acquire its own source link.");
                return destination;
            }
            catch
            {
                if (created && !AssetDatabase.DeleteAsset(folder))
                    Debug.LogWarning("Could not remove the incomplete duplicate folder: " + folder);
                throw;
            }
        }

        private static void CopyPrefab(string source, string destination, Dictionary<Object, Object> copied)
        {
            if (!AssetDatabase.CopyAsset(source, destination)) throw new IOException("Could not copy prefab: " + source);
            GameObject root = PrefabUtility.LoadPrefabContents(destination);
            try
            {
                foreach (var component in root.GetComponentsInChildren<Component>(true))
                {
                    using var serialized = new SerializedObject(component);
                    var property = serialized.GetIterator();
                    while (property.Next(true))
                        if (property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue != null &&
                            copied.TryGetValue(property.objectReferenceValue, out var replacement)) property.objectReferenceValue = replacement;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                root.name = Path.GetFileNameWithoutExtension(destination);
                if (PrefabUtility.SaveAsPrefabAsset(root, destination) == null) throw new IOException("Could not save duplicate prefab.");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private static void ValidatePrefab(GameObject prefab)
        {
            if (prefab == null || PrefabUtility.GetPrefabAssetType(prefab) != PrefabAssetType.Regular)
                throw new InvalidDataException("Duplicate a regular production prefab, not a model or prefab variant.");
            foreach (var transform in prefab.GetComponentsInChildren<Transform>(true))
            {
                if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject) > 0)
                    throw new InvalidDataException("Restore missing prefab scripts before duplicating.");
                if (PrefabUtility.IsAnyPrefabInstanceRoot(transform.gameObject))
                    throw new InvalidDataException("Nested prefab instances require a separate duplication workflow.");
                if (transform.gameObject.GetComponents<Component>().Any(component => EditorUtility.IsDirty(component)))
                    throw new InvalidOperationException("Save modified prefab assets before duplicating.");
            }
        }

        private static void ValidateParent(string parent)
        {
            if (string.IsNullOrEmpty(parent) || !AssetDatabase.IsValidFolder(parent) ||
                (parent != "Assets" && !parent.StartsWith("Assets/", StringComparison.Ordinal)) ||
                parent.Split('/').Any(part => part == "." || part == ".." || part.Length == 0) || parent.Contains('\\'))
                throw new ArgumentException("Choose an existing folder under Assets.", nameof(parent));
        }

        private static void ValidateUnchanged(Dictionary<string, string> fingerprints)
        {
            foreach (var pair in fingerprints)
                if (FileHash(pair.Key) != pair.Value) throw new IOException("A source asset changed during duplication. Retry after saving: " + pair.Key);
        }

        private static string FileHash(string path)
        {
            using var stream = File.OpenRead(VoxelLodPipeline.AssetPathToAbsolute(path));
            using var hash = SHA256.Create();
            return Convert.ToBase64String(hash.ComputeHash(stream));
        }
    }
}
