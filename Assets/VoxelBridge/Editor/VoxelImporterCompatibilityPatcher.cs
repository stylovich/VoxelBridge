using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    internal enum VoxelImporterPatchResult
    {
        Applied,
        AlreadyApplied,
        Conflict
    }

    /// <summary>
    /// Reapplies the Voxel Bridge normal correction after Voxel Importer updates.
    /// The patch is deliberately signature-gated so an unknown importer version is never modified.
    /// </summary>
    internal static class VoxelImporterCompatibilityPatcher
    {
        internal const string ImporterAssetPath = "Assets/VoxelImporter/Editor/Scripts/VoxelBaseCore.cs";

        private const string PendingVoxReimportSessionKey =
            "LocalModels.VoxelBridge.PendingVoxelImporterPatchReimport";

        private const string AddFaceMethodSignature =
            "private void AddFaceSecondary(VoxelData.FaceArea faceArea";
        private const string AddEditFaceMethodSignature =
            "private void AddEditFaceSecondary(VoxelData.FaceArea faceArea";
        private const string OriginalNormalStatement =
            "for (int j = 0; j < 4; j++) normals.Add(desc.Normal);";
        private const string PatchBeginMarker =
            "// Voxel Bridge normal-scale compatibility patch. BEGIN";
        private const string PatchEndMarker =
            "// Voxel Bridge normal-scale compatibility patch. END";
        private const string PatchedNormalStatements =
            "// Voxel Bridge normal-scale compatibility patch. BEGIN\n" +
            "            Vector3 importScaleSign = new Vector3(\n" +
            "                VoxelBase.importScale.x < 0f ? -1f : 1f,\n" +
            "                VoxelBase.importScale.y < 0f ? -1f : 1f,\n" +
            "                VoxelBase.importScale.z < 0f ? -1f : 1f);\n" +
            "            Vector3 scaledNormal = Vector3.Scale(desc.Normal, importScaleSign);\n" +
            "            for (int j = 0; j < 4; j++) normals.Add(scaledNormal);\n" +
            "            // Voxel Bridge normal-scale compatibility patch. END";

        [MenuItem("Tools/Voxel Bridge/Compatibilidad/Aplicar parche de normales de Voxel Importer")]
        private static void ApplyNormalsPatchMenu()
        {
            if (!TryApplyToProject(out VoxelImporterPatchResult result, out string message))
            {
                Debug.LogWarning(message);
                if (!Application.isBatchMode)
                    EditorUtility.DisplayDialog("Voxel Bridge - parche no aplicado", message, "Aceptar");
                return;
            }

            if (result == VoxelImporterPatchResult.Applied)
            {
                SessionState.SetBool(PendingVoxReimportSessionKey, true);
                Debug.Log(message);
                AssetDatabase.ImportAsset(ImporterAssetPath, ImportAssetOptions.ForceUpdate);
            }
            else
            {
                Debug.Log(message);
            }

            if (!Application.isBatchMode)
                EditorUtility.DisplayDialog("Voxel Bridge", message, "Aceptar");
        }

        [InitializeOnLoadMethod]
        private static void ResumeReimportAfterPatchCompilation()
        {
            if (!SessionState.GetBool(PendingVoxReimportSessionKey, false)) return;

            SessionState.EraseBool(PendingVoxReimportSessionKey);
            EditorApplication.delayCall += () =>
            {
                Debug.Log("Voxel Bridge regenerará los .vox después de aplicar el parche de normales.");
                VoxelBridgeBatch.SyncAllGeneratedVoxAssets(false);
            };
        }

        internal static bool TryApplyToProject(out VoxelImporterPatchResult result, out string message)
        {
            result = VoxelImporterPatchResult.Conflict;
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot))
            {
                message = "Voxel Bridge no pudo encontrar la raíz del proyecto. No se modificó ningún archivo.";
                return false;
            }

            string absolutePath = Path.GetFullPath(Path.Combine(projectRoot, ImporterAssetPath));
            if (!File.Exists(absolutePath))
            {
                message = $"No se encontró '{ImporterAssetPath}'. Instala Voxel Importer antes de aplicar el parche.";
                return false;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(absolutePath);
                bool hasUtf8Bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                int textOffset = hasUtf8Bom ? 3 : 0;
                string source = Encoding.UTF8.GetString(bytes, textOffset, bytes.Length - textOffset);

                result = TryPatchSource(source, out string patchedSource, out string detail);
                if (result == VoxelImporterPatchResult.Conflict)
                {
                    message = "Voxel Bridge detectó cambios incompatibles en VoxelBaseCore.cs. " +
                              "El parche no se aplicó y el archivo quedó intacto. " + detail;
                    return false;
                }

                if (result == VoxelImporterPatchResult.AlreadyApplied)
                {
                    message = "El parche de normales de Voxel Importer ya está aplicado.";
                    return true;
                }

                File.WriteAllText(absolutePath, patchedSource, new UTF8Encoding(hasUtf8Bom));
                message = "Parche de normales aplicado a Voxel Importer. " +
                          "Unity recompilará el asset; después sincroniza o reimporta los .vox generados.";
                return true;
            }
            catch (Exception exception)
            {
                message = "Voxel Bridge no pudo aplicar el parche y no puede confirmar el estado del archivo: " +
                          exception.Message;
                return false;
            }
        }

        internal static VoxelImporterPatchResult TryPatchSource(
            string source, out string patchedSource, out string detail)
        {
            patchedSource = source;
            detail = null;
            if (source == null)
            {
                detail = "El código fuente está vacío.";
                return VoxelImporterPatchResult.Conflict;
            }

            int beginMarkers = CountOccurrences(source, PatchBeginMarker);
            int endMarkers = CountOccurrences(source, PatchEndMarker);
            int originalStatements = CountOccurrences(source, OriginalNormalStatement);
            int addFaceMethods = CountOccurrences(source, AddFaceMethodSignature);
            int addEditFaceMethods = CountOccurrences(source, AddEditFaceMethodSignature);

            if (beginMarkers == 2 && endMarkers == 2 && originalStatements == 0 &&
                addFaceMethods == 1 && addEditFaceMethods == 1)
            {
                return VoxelImporterPatchResult.AlreadyApplied;
            }

            if (beginMarkers != 0 || endMarkers != 0 || originalStatements != 2 ||
                addFaceMethods != 1 || addEditFaceMethods != 1)
            {
                detail = $"Firma esperada: 2 asignaciones de normales y una copia de cada método; " +
                         $"encontrado: normales={originalStatements}, AddFace={addFaceMethods}, " +
                         $"AddEditFace={addEditFaceMethods}, marcadores={beginMarkers}/{endMarkers}.";
                return VoxelImporterPatchResult.Conflict;
            }

            string newline = source.Contains("\r\n") ? "\r\n" : "\n";
            string replacement = PatchedNormalStatements.Replace("\n", newline);
            patchedSource = source.Replace(OriginalNormalStatement, replacement);
            return VoxelImporterPatchResult.Applied;
        }

        private static int CountOccurrences(string source, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }
            return count;
        }
    }
}
