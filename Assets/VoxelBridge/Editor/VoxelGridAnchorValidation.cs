using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    internal static class VoxelGridAnchorValidation
    {
        [MenuItem("CONTEXT/LODGroup/Validate Family Local Grid")]
        private static void ValidateMenu(MenuCommand command)
        {
            var group = command.context as LODGroup;
            string error = Validate(group);
            if (error == null) Debug.Log("Family Local: los renderers comparten el sistema de coordenadas de la familia. Revisar además la unidad y fase de sus mallas voxel.", group);
            else Debug.LogWarning(error, group);
        }

        internal static string Validate(LODGroup group)
        {
            if (!group) return "Se requiere un LODGroup de familia.";
            try { VoxelScaleNormalization.SourceScale(group.gameObject); }
            catch (InvalidOperationException e) { return e.Message; }
            if (group.transform.localToWorldMatrix.determinant < 0)
                return "Family Local requiere incorporar el reflejo a la geometría mediante Normalize Scale antes de usar la rejilla.";
            var renderers = group.GetLODs().SelectMany(l => l.renderers).Where(r => r).Distinct().ToArray();
            if (renderers.Length == 0) return "La familia no contiene renderers.";
            foreach (var renderer in renderers)
            {
                if (renderer.isPartOfStaticBatch || (GameObjectUtility.GetStaticEditorFlags(renderer.gameObject) & StaticEditorFlags.BatchingStatic) != 0)
                    return "Family Local requiere desactivar Batching Static en sus renderers. Esto no desactiva DOTS Instancing.";
                Matrix4x4 relative = group.transform.worldToLocalMatrix * renderer.localToWorldMatrix;
                for (int i = 0; i < 16; i++)
                    if (Mathf.Abs(relative[i] - Matrix4x4.identity[i]) > .0001f)
                        return $"'{renderer.name}' no comparte el sistema de coordenadas de la familia. Restaurar el Transform del chunk/LOD antes de usar Family Local.";
            }
            return null;
        }
    }
}
