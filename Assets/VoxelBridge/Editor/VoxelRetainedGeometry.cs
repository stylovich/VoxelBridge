using System.IO;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    internal static class VoxelRetainedGeometry
    {
        internal const string ChildName = "Retained Geometry";

        internal static void Attach(Transform parent, string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;
            var asset = Resolve(guid);
            if (parent.Find(ChildName) != null) return;
            var child = Object.Instantiate(asset);
            child.name = ChildName;
            child.transform.SetParent(parent, false);
        }

        internal static GameObject Resolve(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
            if (asset == null) throw new InvalidDataException("The retained geometry snapshot is missing. Restore it before rebuilding.");
            foreach (var renderer in asset.GetComponentsInChildren<MeshRenderer>(true))
            {
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null || renderer.sharedMaterial == null)
                    throw new InvalidDataException("A retained geometry mesh or material is missing. Restore its asset before rebuilding.");
            }
            return asset;
        }
    }
}
