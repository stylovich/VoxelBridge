using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    internal readonly struct VoxelLodBatchPlacementResult
    {
        public readonly GameObject Root;
        public readonly int PlacedCount;
        public readonly bool OriginalRootDisabled;

        public VoxelLodBatchPlacementResult(
            GameObject root, int placedCount, bool originalRootDisabled)
        {
            Root = root;
            PlacedCount = placedCount;
            OriginalRootDisabled = originalRootDisabled;
        }
    }

    internal readonly struct VoxelLodSinglePlacementResult
    {
        public readonly GameObject Instance;
        public readonly bool OriginalObjectDisabled;

        public VoxelLodSinglePlacementResult(GameObject instance, bool originalObjectDisabled)
        {
            Instance = instance;
            OriginalObjectDisabled = originalObjectDisabled;
        }
    }

    internal static class VoxelLodBatchScenePlacement
    {
        public static bool CanPlace(GameObject sourceParent) =>
            sourceParent != null && !AssetDatabase.Contains(sourceParent) &&
            sourceParent.scene.IsValid() && sourceParent.scene.isLoaded &&
            !EditorSceneManager.IsPreviewScene(sourceParent.scene) &&
            PrefabStageUtility.GetPrefabStage(sourceParent) == null;

        public static VoxelLodSinglePlacementResult PlaceSingle(
            GameObject sourceObject, VoxelLodBuildResult build, VoxelStyleProfile profile,
            bool disableOriginalObject)
        {
            if (!CanPlace(sourceObject))
                throw new InvalidOperationException(
                    "Single placement requires a GameObject in a loaded scene.");

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(build.PrefabAssetPath);
            if (prefab == null)
                throw new InvalidOperationException(
                    $"The converted prefab for '{sourceObject.name}' was not found.");

            var instance = PrefabUtility.InstantiatePrefab(prefab, sourceObject.scene) as GameObject;
            if (instance == null)
                throw new InvalidOperationException(
                    $"The converted prefab for '{sourceObject.name}' could not be instantiated.");

            try
            {
                Transform sourceTransform = sourceObject.transform;
                Transform instanceTransform = instance.transform;
                instanceTransform.SetParent(sourceTransform.parent, false);
                instanceTransform.SetSiblingIndex(sourceTransform.GetSiblingIndex() + 1);
                CopyLocalTransform(sourceTransform, instanceTransform);
                SnapWorldPosition(instanceTransform, sourceTransform.position, profile);
                instance.name = GameObjectUtility.GetUniqueNameForSibling(
                    sourceTransform.parent, sourceObject.name + "_Voxel");
                instance.tag = sourceObject.tag;
                SetLayerAndStaticFlagsRecursively(instance, sourceObject.layer,
                    GameObjectUtility.GetStaticEditorFlags(sourceObject));
                instance.SetActive(sourceObject.activeSelf);
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(instance);
                throw;
            }

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Place voxel model in scene");
            Undo.RegisterCreatedObjectUndo(instance, "Place voxel model");
            if (disableOriginalObject)
            {
                Undo.RecordObject(sourceObject, "Disable original visual object");
                sourceObject.SetActive(false);
            }

            EditorSceneManager.MarkSceneDirty(sourceObject.scene);
            Undo.CollapseUndoOperations(undoGroup);
            return new VoxelLodSinglePlacementResult(instance, disableOriginalObject);
        }

        public static VoxelLodBatchPlacementResult Place(
            GameObject sourceParent, VoxelLodBatchBuildResult batch,
            VoxelStyleProfile profile, bool disableOriginalRoot)
        {
            if (!CanPlace(sourceParent))
                throw new InvalidOperationException(
                    "Automatic placement requires a parent object in a loaded scene.");
            if (batch == null) throw new ArgumentNullException(nameof(batch));

            VoxelLodBatchItemResult[] successful = batch.Items
                .Where(item => item.Succeeded)
                .ToArray();
            if (successful.Length == 0)
                throw new InvalidOperationException(
                    "The batch contains no successful conversions to place in the scene.");

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Place voxel batch in scene");

            string rootName = GameObjectUtility.GetUniqueNameForSibling(
                sourceParent.transform.parent, sourceParent.name + "_Voxel");
            var voxelRoot = new GameObject(rootName);
            Undo.RegisterCreatedObjectUndo(voxelRoot, "Create voxel root");
            voxelRoot.transform.SetParent(sourceParent.transform.parent, false);
            voxelRoot.transform.SetSiblingIndex(sourceParent.transform.GetSiblingIndex() + 1);
            CopyLocalTransform(sourceParent.transform, voxelRoot.transform);
            SnapWorldPosition(voxelRoot.transform, sourceParent.transform.position, profile);
            voxelRoot.layer = sourceParent.layer;
            voxelRoot.tag = sourceParent.tag;
            GameObjectUtility.SetStaticEditorFlags(
                voxelRoot, GameObjectUtility.GetStaticEditorFlags(sourceParent));
            voxelRoot.SetActive(false);

            foreach (VoxelLodBatchItemResult item in successful)
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                    item.BuildResult.PrefabAssetPath);
                if (prefab == null)
                    throw new InvalidOperationException(
                        $"The converted prefab for '{item.Source.name}' was not found.");

                var instance = PrefabUtility.InstantiatePrefab(prefab, voxelRoot.transform) as GameObject;
                if (instance == null)
                    throw new InvalidOperationException(
                        $"The converted prefab for '{item.Source.name}' could not be instantiated.");
                Undo.RegisterCreatedObjectUndo(instance, "Place voxel model");
                instance.name = item.Source.name;
                CopyLocalTransform(item.Source.transform, instance.transform);
                SnapWorldPosition(instance.transform, item.Source.transform.position, profile);
                SetLayerAndStaticFlagsRecursively(
                    instance, item.Source.layer,
                    GameObjectUtility.GetStaticEditorFlags(item.Source));
                instance.SetActive(item.Source.activeSelf);
            }

            bool coversEveryDirectChild =
                sourceParent.transform.childCount == batch.CandidateCount +
                batch.ExcludedInactiveDirectChildCount;
            bool originalRootDisabled = disableOriginalRoot && batch.IsComplete &&
                                        coversEveryDirectChild;
            if (originalRootDisabled)
            {
                bool originalActive = sourceParent.activeSelf;
                Undo.RecordObject(sourceParent, "Disable original visual root");
                sourceParent.SetActive(false);
                voxelRoot.SetActive(originalActive);
            }
            else if (!disableOriginalRoot)
            {
                voxelRoot.SetActive(sourceParent.activeSelf);
            }

            EditorSceneManager.MarkSceneDirty(sourceParent.scene);
            Undo.CollapseUndoOperations(undoGroup);
            return new VoxelLodBatchPlacementResult(
                voxelRoot, successful.Length, originalRootDisabled);
        }

        private static void CopyLocalTransform(Transform source, Transform destination)
        {
            destination.localPosition = source.localPosition;
            destination.localRotation = source.localRotation;
            destination.localScale = source.localScale;
        }

        private static void SnapWorldPosition(
            Transform destination, Vector3 sourceWorldPosition, VoxelStyleProfile profile)
        {
            if (profile != null)
                destination.position = profile.GetSnappedWorldPosition(sourceWorldPosition);
        }

        private static void SetLayerAndStaticFlagsRecursively(
            GameObject root, int layer, StaticEditorFlags staticFlags)
        {
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                transform.gameObject.layer = layer;
                GameObjectUtility.SetStaticEditorFlags(transform.gameObject, staticFlags);
            }
        }
    }
}
