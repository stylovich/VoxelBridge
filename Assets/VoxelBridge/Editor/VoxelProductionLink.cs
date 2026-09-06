using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge
{
    // Authoring-only link stored in the prefab importer, never in a runtime component.
    [Serializable]
    internal sealed class VoxelProductionLink
    {
        private const string Prefix = "VoxelBridgeProduction:";
        public int version = 1;
        public string ownerGuid;
        public string sourceGuid;
        public string meshGuid;
        public string manifestGuid;

        public string SourcePath
        {
            get
            {
                string path = AssetDatabase.GUIDToAssetPath(sourceGuid);
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".vox", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(VoxelLodPipeline.AssetPathToAbsolute(path)))
                    throw new InvalidDataException("The linked source VOX is missing. Restore the asset with its .meta file.");
                return path;
            }
        }

        internal static VoxelProductionLink Load(string prefabPath)
        {
            var importer = AssetImporter.GetAtPath(prefabPath);
            if (importer == null || !importer.userData.StartsWith(Prefix, StringComparison.Ordinal))
                throw new InvalidDataException("This prefab has no production source link. Create a linked output from its source VOX first.");
            var link = JsonUtility.FromJson<VoxelProductionLink>(importer.userData.Substring(Prefix.Length));
            if (link == null || link.version != 1 || string.IsNullOrEmpty(link.sourceGuid) ||
                (string.IsNullOrEmpty(link.meshGuid) && string.IsNullOrEmpty(link.manifestGuid)) ||
                link.ownerGuid != AssetDatabase.AssetPathToGUID(prefabPath))
                throw new InvalidDataException("Invalid or copied production link. Rebuild the original prefab, not an independent duplicate.");
            return link;
        }

        internal static bool HasLink(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var importer = AssetImporter.GetAtPath(path);
            return importer != null && importer.userData.StartsWith(Prefix, StringComparison.Ordinal);
        }

        internal static void Store(GameObject prefab, string sourcePath, Mesh mesh, string manifestPath = null)
        {
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(sourcePath)))
                AssetDatabase.ImportAsset(sourcePath, ImportAssetOptions.ForceSynchronousImport);
            string path = AssetDatabase.GetAssetPath(prefab);
            var importer = AssetImporter.GetAtPath(path);
            if (importer == null || !string.IsNullOrEmpty(importer.userData))
                throw new InvalidDataException("Cannot replace existing prefab importer metadata.");
            var link = new VoxelProductionLink
            {
                ownerGuid = AssetDatabase.AssetPathToGUID(path),
                sourceGuid = AssetDatabase.AssetPathToGUID(sourcePath),
                meshGuid = mesh != null ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(mesh)) : null,
                manifestGuid = string.IsNullOrEmpty(manifestPath) ? null : AssetDatabase.AssetPathToGUID(manifestPath)
            };
            if (string.IsNullOrEmpty(link.sourceGuid) ||
                (string.IsNullOrEmpty(link.meshGuid) && string.IsNullOrEmpty(link.manifestGuid)))
                throw new InvalidDataException("Could not resolve production asset GUIDs.");
            importer.userData = Prefix + JsonUtility.ToJson(link);
            EditorUtility.SetDirty(importer);
            AssetDatabase.WriteImportSettingsIfDirty(path);
        }

        internal static string FindOutput(string sourcePath, string folder)
        {
            string source = AssetDatabase.AssetPathToGUID(sourcePath);
            if (string.IsNullOrEmpty(source) || !AssetDatabase.IsValidFolder(folder)) return null;
            string found = null;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!HasLink(path)) continue;
                VoxelProductionLink link = Load(path);
                if (link.sourceGuid != source) continue;
                if (found != null) throw new InvalidDataException("Several production prefabs use this source. Select the intended prefab and use Rebuild.");
                found = path;
            }
            return found;
        }

        internal static string PrefabPath(Object target)
        {
            GameObject go = target as GameObject;
            if (target is Component component) go = component.gameObject;
            if (go == null) return null;
            var stage = PrefabStageUtility.GetPrefabStage(go);
            if (stage != null) return stage.assetPath;
            if (EditorUtility.IsPersistent(go)) return AssetDatabase.GetAssetPath(go);
            return PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
        }
    }
}
