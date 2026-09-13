using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace LocalModels.EditorTools
{
    internal static class RemoveHierarchyColliders
    {
        private const string MenuPath = "GameObject/LocalModels/Eliminar colliders de la jerarquía";
        private const string UndoName = "Eliminar colliders de la jerarquía";

        [MenuItem(MenuPath, false, 49)]
        private static void RemoveSelected()
        {
            var colliders = new HashSet<Component>();
            foreach (var root in Selection.gameObjects)
            {
                if (!CanEdit(root)) continue;
                foreach (var collider in root.GetComponentsInChildren<Collider>(true))
                    colliders.Add(collider);
                foreach (var collider in root.GetComponentsInChildren<Collider2D>(true))
                    colliders.Add(collider);
            }

            if (colliders.Count == 0)
            {
                Debug.Log("No se encontraron colliders en la selección ni en sus hijos.");
                return;
            }

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(UndoName);
            int removed = 0;
            try
            {
                foreach (var collider in colliders)
                {
                    if (!collider) continue;
                    Undo.DestroyObjectImmediate(collider);
                    if (!collider) removed++;
                    else Debug.LogWarning("No se pudo eliminar un collider. Revisar si otro componente lo requiere o si el objeto es de solo lectura.", collider);
                }
            }
            finally
            {
                Undo.CollapseUndoOperations(group);
                Debug.Log($"Colliders eliminados: {removed} de {colliders.Count}. Deshacer con Ctrl+Z.");
            }
        }

        [MenuItem(MenuPath, true)]
        private static bool ValidateSelection()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return false;
            foreach (var root in Selection.gameObjects)
                if (CanEdit(root)) return true;
            return false;
        }

        private static bool CanEdit(GameObject root)
        {
            return root && !EditorUtility.IsPersistent(root) && root.scene.IsValid()
                && (root.hideFlags & HideFlags.NotEditable) == 0;
        }
    }
}
