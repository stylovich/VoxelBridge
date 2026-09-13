using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    internal static class VoxelLodTransitionUpdater
    {
        internal static void ApplyToGroup(LODGroup group, VoxelLodSetManifest manifest, VoxelStyleProfile profile)
        {
            if (group == null) throw new InvalidDataException("The production prefab has no LODGroup.");
            if (profile == null || !profile.TryValidate(out _)) throw new InvalidDataException("A valid style profile is required.");
            if (!float.IsFinite(group.size) || group.size <= 0) throw new InvalidDataException("LODGroup size must be finite and positive.");
            if (!string.IsNullOrEmpty(manifest.impostor?.assetPath))
                throw new InvalidOperationException("Transition-only updates do not support a final impostor. Its transition policy requires a separate review.");
            LOD[] lods = group.GetLODs();
            if (manifest.lods == null || lods.Length == 0 || lods.Length != manifest.lods.Length || lods.Length > profile.LodCount)
                throw new InvalidDataException("The prefab, manifest and profile have incompatible LOD counts. No levels were changed.");
            // Validate every level before mutating either representation.
            for (int i = 0; i < lods.Length; i++)
            {
                if (manifest.lods[i] == null || manifest.lods[i].lodIndex != i)
                    throw new InvalidDataException("Production LOD indices must be contiguous.");
                lods[i].screenRelativeTransitionHeight = profile.GetLodScreenHeight(i, group.size);
            }
            // SetLODs can recompute bounds internally, even when renderer references are identical.
            float size = group.size;
            Vector3 referencePoint = group.localReferencePoint;
            group.SetLODs(lods);
            group.size = size;
            group.localReferencePoint = referencePoint;
            manifest.lodGroupSize = size;
            for (int i = 0; i < lods.Length; i++)
                manifest.lods[i].screenRelativeTransitionHeight = lods[i].screenRelativeTransitionHeight;
        }

        // File transaction, like regeneration: explicit confirmation in the UI, persistent backup,
        // and paired rollback on failure. Scene overrides are deliberately not reverted.
        internal static string Apply(string manifestPath)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Exit Play Mode before changing production assets.");
            var manifest = VoxelProductionFamily.Load(manifestPath);
            var profile = VoxelProductionFamily.Profile(manifest);
            string prefabPath = manifest.prefabAssetPath;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null || PrefabUtility.GetPrefabAssetType(prefab) != PrefabAssetType.Regular)
                throw new InvalidDataException("A regular production prefab is required.");
            if (PrefabStageUtility.GetCurrentPrefabStage()?.assetPath == prefabPath)
                throw new InvalidOperationException("Close Prefab Mode before applying profile transitions.");
            if (VoxelProductionLink.Load(prefabPath).manifestGuid != AssetDatabase.AssetPathToGUID(manifestPath))
                throw new InvalidDataException("The prefab belongs to a different manifest.");

            string prefabAbsolute = VoxelLodPipeline.AssetPathToAbsolute(prefabPath);
            string manifestAbsolute = VoxelLodPipeline.AssetPathToAbsolute(manifestPath);
            byte[] oldPrefab = File.ReadAllBytes(prefabAbsolute), oldManifest = File.ReadAllBytes(manifestAbsolute);
            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            bool writeStarted = false;
            try
            {
                ApplyToGroup(root.GetComponent<LODGroup>(), manifest, profile);
                string backup = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/VoxelBridge/LODTransitionBackups", Guid.NewGuid().ToString("N")));
                Directory.CreateDirectory(backup);
                foreach (string source in new[] { prefabAbsolute, manifestAbsolute, prefabAbsolute + ".meta", manifestAbsolute + ".meta" })
                    File.Copy(source, Path.Combine(backup, Path.GetFileName(source)));
                Debug.Log("LOD transition recovery copy: " + backup);
                writeStarted = true;
                if (PrefabUtility.SaveAsPrefabAsset(root, prefabPath) == null)
                    throw new IOException("Could not save the production prefab.");
                VoxelLodPipeline.SaveManifest(manifestPath, manifest);
                return backup;
            }
            catch
            {
                if (root != null) { PrefabUtility.UnloadPrefabContents(root); root = null; }
                if (writeStarted)
                {
                    File.WriteAllBytes(prefabAbsolute, oldPrefab);
                    File.WriteAllBytes(manifestAbsolute, oldManifest);
                    AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceSynchronousImport);
                    AssetDatabase.ImportAsset(manifestPath, ImportAssetOptions.ForceSynchronousImport);
                }
                throw;
            }
            finally { if (root != null) PrefabUtility.UnloadPrefabContents(root); }
        }
    }
}
