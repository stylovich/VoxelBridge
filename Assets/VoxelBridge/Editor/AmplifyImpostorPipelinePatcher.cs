using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    internal enum AmplifyPipelinePatchResult
    {
        Applied,
        AlreadyApplied,
        Required,
        Conflict,
        NotInstalled
    }

    /// <summary>
    /// Keeps Amplify Impostors pipeline detection and material access stable during baking.
    /// Source changes are signature-gated so unknown vendor versions remain untouched.
    /// </summary>
    internal static class AmplifyImpostorPipelinePatcher
    {
        internal const string SourceAssetPath =
            "Assets/AmplifyImpostors/Plugins/Scripts/AmplifyImpostor.cs";

        private const string PipelinePatchBeginMarker =
            "// Voxel Bridge render-pipeline detection patch. BEGIN";
        private const string PipelinePatchEndMarker =
            "// Voxel Bridge render-pipeline detection patch. END";
        private const string TexturePatchMarker =
            "/* Voxel Bridge texture-property guard patch. */";
        private const string OriginalDetection =
            "string pipelineName = string.Empty;\n" +
            "\t\t\ttry\n" +
            "\t\t\t{\n" +
            "\t\t\t\tpipelineName = UnityEngine.Rendering.RenderPipelineManager.currentPipeline.ToString();\n" +
            "\t\t\t}\n" +
            "\t\t\tcatch( Exception )\n" +
            "\t\t\t{\n" +
            "\t\t\t\tpipelineName = \"\";\n" +
            "\t\t\t}";
        private const string PatchedDetection =
            "// Voxel Bridge render-pipeline detection patch. BEGIN\n" +
            "\t\t\tvar activePipeline = UnityEngine.Rendering.RenderPipelineManager.currentPipeline;\n" +
            "\t\t\tvar configuredPipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline ??\n" +
            "\t\t\t\tUnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline;\n" +
            "\t\t\tstring pipelineName = activePipeline != null\n" +
            "\t\t\t\t? activePipeline.ToString()\n" +
            "\t\t\t\t: configuredPipeline != null\n" +
            "\t\t\t\t\t? configuredPipeline.GetType().FullName\n" +
            "\t\t\t\t\t: string.Empty;\n" +
            "\t\t\t// Voxel Bridge render-pipeline detection patch. END";
        private const string OriginalOutputTextureGuard =
            "material.HasProperty( outputList[ i ].Name )";
        private const string PatchedOutputTextureGuard =
            "/* Voxel Bridge texture-property guard patch. */ " +
            "Array.IndexOf( material.GetTexturePropertyNames(), outputList[ i ].Name ) >= 0";
        private const string OriginalStandardTextureGuard =
            "material.HasProperty( m_propertyNames[ i ] )";
        private const string PatchedStandardTextureGuard =
            "/* Voxel Bridge texture-property guard patch. */ " +
            "Array.IndexOf( material.GetTexturePropertyNames(), m_propertyNames[ i ] ) >= 0";

        [MenuItem("Tools/Voxel Bridge/Compatibilidad/Aplicar parche de Amplify Impostors")]
        private static void ApplyPatchMenu()
        {
            if (!TryApplyToProject(out AmplifyPipelinePatchResult result, out string message))
            {
                Debug.LogWarning(message);
                if (!Application.isBatchMode)
                    EditorUtility.DisplayDialog(
                        "Voxel Bridge - parche no aplicado", message, "Aceptar");
                return;
            }

            Debug.Log(message);
            if (result == AmplifyPipelinePatchResult.Applied)
                AssetDatabase.ImportAsset(SourceAssetPath, ImportAssetOptions.ForceUpdate);
            if (!Application.isBatchMode)
                EditorUtility.DisplayDialog("Voxel Bridge", message, "Aceptar");
        }

        internal static AmplifyPipelinePatchResult GetProjectStatus(out string detail)
        {
            if (!TryReadProjectSource(out string source, out detail))
                return File.Exists(GetAbsoluteSourcePath())
                    ? AmplifyPipelinePatchResult.Conflict
                    : AmplifyPipelinePatchResult.NotInstalled;

            AmplifyPipelinePatchResult result = TryPatchSource(
                source, out _, out string patchDetail);
            detail = patchDetail;
            return result == AmplifyPipelinePatchResult.Applied
                ? AmplifyPipelinePatchResult.Required
                : result;
        }

        internal static bool TryApplyToProject(
            out AmplifyPipelinePatchResult result, out string message)
        {
            result = AmplifyPipelinePatchResult.Conflict;
            if (!TryReadProjectSource(out string source, out string readError))
            {
                message = readError;
                result = File.Exists(GetAbsoluteSourcePath())
                    ? AmplifyPipelinePatchResult.Conflict
                    : AmplifyPipelinePatchResult.NotInstalled;
                return false;
            }

            try
            {
                string absolutePath = GetAbsoluteSourcePath();
                byte[] bytes = File.ReadAllBytes(absolutePath);
                bool hasUtf8Bom = bytes.Length >= 3 && bytes[0] == 0xEF &&
                                  bytes[1] == 0xBB && bytes[2] == 0xBF;
                result = TryPatchSource(source, out string patchedSource, out string detail);
                if (result == AmplifyPipelinePatchResult.Conflict)
                {
                    message = "La fuente instalada de Amplify Impostors no coincide con la " +
                              "firma validada. El parche no se aplicó y el archivo quedó intacto. " +
                              detail;
                    return false;
                }
                if (result == AmplifyPipelinePatchResult.AlreadyApplied)
                {
                    message = "El parche de compatibilidad de Amplify Impostors ya está aplicado.";
                    return true;
                }

                File.WriteAllText(absolutePath, patchedSource, new UTF8Encoding(hasUtf8Bom));
                message = "Parche de compatibilidad aplicado a Amplify Impostors. Unity recompilará " +
                          "el asset antes del siguiente horneado.";
                return true;
            }
            catch (Exception exception)
            {
                message = "No se pudo aplicar el parche de compatibilidad de Amplify Impostors: " +
                          exception.Message;
                result = AmplifyPipelinePatchResult.Conflict;
                return false;
            }
        }

        internal static AmplifyPipelinePatchResult TryPatchSource(
            string source, out string patchedSource, out string detail)
        {
            patchedSource = source;
            detail = null;
            if (source == null)
            {
                detail = "El código fuente está vacío.";
                return AmplifyPipelinePatchResult.Conflict;
            }

            string normalized = source.Replace("\r\n", "\n");
            int pipelineBeginMarkers = CountOccurrences(
                normalized, PipelinePatchBeginMarker);
            int pipelineEndMarkers = CountOccurrences(
                normalized, PipelinePatchEndMarker);
            int originalDetections = CountOccurrences(normalized, OriginalDetection);
            bool pipelinePatched = pipelineBeginMarkers == 1 &&
                                   pipelineEndMarkers == 1 &&
                                   originalDetections == 0;
            bool pipelineOriginal = pipelineBeginMarkers == 0 &&
                                    pipelineEndMarkers == 0 &&
                                    originalDetections == 1;

            int textureMarkers = CountOccurrences(normalized, TexturePatchMarker);
            int originalOutputGuards = CountOccurrences(
                normalized, OriginalOutputTextureGuard);
            int originalStandardGuards = CountOccurrences(
                normalized, OriginalStandardTextureGuard);
            int patchedOutputGuards = CountOccurrences(
                normalized, PatchedOutputTextureGuard);
            int patchedStandardGuards = CountOccurrences(
                normalized, PatchedStandardTextureGuard);
            bool texturesPatched = textureMarkers == 5 &&
                                   originalOutputGuards == 0 &&
                                   originalStandardGuards == 0 &&
                                   patchedOutputGuards == 2 &&
                                   patchedStandardGuards == 3;
            bool texturesOriginal = textureMarkers == 0 &&
                                    originalOutputGuards == 2 &&
                                    originalStandardGuards == 3 &&
                                    patchedOutputGuards == 0 &&
                                    patchedStandardGuards == 0;

            if ((!pipelinePatched && !pipelineOriginal) ||
                (!texturesPatched && !texturesOriginal))
            {
                detail = "Firma esperada no encontrada. " +
                         $"Pipeline: detecciones={originalDetections}, " +
                         $"marcadores={pipelineBeginMarkers}/{pipelineEndMarkers}. " +
                         $"Texturas: originales={originalOutputGuards}/{originalStandardGuards}, " +
                         $"parcheadas={patchedOutputGuards}/{patchedStandardGuards}, " +
                         $"marcadores={textureMarkers}.";
                return AmplifyPipelinePatchResult.Conflict;
            }
            if (pipelinePatched && texturesPatched)
                return AmplifyPipelinePatchResult.AlreadyApplied;

            string newline = source.Contains("\r\n") ? "\r\n" : "\n";
            if (pipelineOriginal)
                normalized = normalized.Replace(OriginalDetection, PatchedDetection);
            if (texturesOriginal)
            {
                normalized = normalized
                    .Replace(OriginalOutputTextureGuard, PatchedOutputTextureGuard)
                    .Replace(OriginalStandardTextureGuard, PatchedStandardTextureGuard);
            }
            patchedSource = normalized.Replace("\n", newline);
            return AmplifyPipelinePatchResult.Applied;
        }

        private static bool TryReadProjectSource(out string source, out string error)
        {
            source = null;
            string absolutePath = GetAbsoluteSourcePath();
            if (!File.Exists(absolutePath))
            {
                error = $"No se encontró '{SourceAssetPath}'.";
                return false;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(absolutePath);
                int offset = bytes.Length >= 3 && bytes[0] == 0xEF &&
                             bytes[1] == 0xBB && bytes[2] == 0xBF
                    ? 3
                    : 0;
                source = Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                error = "No se pudo leer la fuente de Amplify Impostors: " + exception.Message;
                return false;
            }
        }

        private static string GetAbsoluteSourcePath()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            return string.IsNullOrEmpty(projectRoot)
                ? SourceAssetPath
                : Path.GetFullPath(Path.Combine(projectRoot, SourceAssetPath));
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
