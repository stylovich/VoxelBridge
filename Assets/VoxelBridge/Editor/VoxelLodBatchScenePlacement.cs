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

    internal static class VoxelLodBatchScenePlacement
    {
        public static bool CanPlace(GameObject sourceParent) =>
            sourceParent != null && !AssetDatabase.Contains(sourceParent) &&
            sourceParent.scene.IsValid() && sourceParent.scene.isLoaded &&
            !EditorSceneManager.IsPreviewScene(sourceParent.scene) &&
            PrefabStageUtility.GetPrefabStage(sourceParent) == null;

        public static VoxelLodBatchPlacementResult Place(
            GameObject sourceParent, VoxelLodBatchBuildResult batch,
            bool disableOriginalRoot)
        {
            if (!CanPlace(sourceParent))
                throw new InvalidOperationException(
                    "La colocación automática requiere un objeto padre perteneciente a una escena cargada.");
            if (batch == null) throw new ArgumentNullException(nameof(batch));

            VoxelLodBatchItemResult[] successful = batch.Items
                .Where(item => item.Succeeded)
                .ToArray();
            if (successful.Length == 0)
                throw new InvalidOperationException(
                    "El lote no contiene conversiones correctas para colocar en la escena.");

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Colocar lote voxel en escena");

            string rootName = GameObjectUtility.GetUniqueNameForSibling(
                sourceParent.transform.parent, sourceParent.name + "_Voxel");
            var voxelRoot = new GameObject(rootName);
            Undo.RegisterCreatedObjectUndo(voxelRoot, "Crear raíz voxel");
            voxelRoot.transform.SetParent(sourceParent.transform.parent, false);
            voxelRoot.transform.SetSiblingIndex(sourceParent.transform.GetSiblingIndex() + 1);
            CopyLocalTransform(sourceParent.transform, voxelRoot.transform);
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
                        $"No se encontró el prefab convertido de '{item.Source.name}'.");

                var instance = PrefabUtility.InstantiatePrefab(prefab, voxelRoot.transform) as GameObject;
                if (instance == null)
                    throw new InvalidOperationException(
                        $"No se pudo instanciar el prefab convertido de '{item.Source.name}'.");
                Undo.RegisterCreatedObjectUndo(instance, "Colocar modelo voxel");
                instance.name = item.Source.name;
                CopyLocalTransform(item.Source.transform, instance.transform);
                SetLayerAndStaticFlagsRecursively(
                    instance, item.Source.layer,
                    GameObjectUtility.GetStaticEditorFlags(item.Source));
                instance.SetActive(item.Source.activeSelf);
            }

            bool coversEveryDirectChild =
                sourceParent.transform.childCount == batch.CandidateCount;
            bool originalRootDisabled = disableOriginalRoot && batch.IsComplete &&
                                        coversEveryDirectChild;
            if (originalRootDisabled)
            {
                bool originalActive = sourceParent.activeSelf;
                Undo.RecordObject(sourceParent, "Desactivar raíz visual original");
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
