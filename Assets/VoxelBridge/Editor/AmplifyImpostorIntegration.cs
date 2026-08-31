using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge
{
    internal enum AmplifyImpostorCompatibilityStatus
    {
        Ready,
        NotInstalled,
        UnsupportedVersion,
        ApiConflict
    }

    internal readonly struct AmplifyImpostorCompatibility
    {
        public readonly AmplifyImpostorCompatibilityStatus Status;
        public readonly string Version;
        public readonly string Message;

        public AmplifyImpostorCompatibility(
            AmplifyImpostorCompatibilityStatus status, string version, string message)
        {
            Status = status;
            Version = version;
            Message = message;
        }

        public bool CanBake => Status == AmplifyImpostorCompatibilityStatus.Ready;
    }

    internal readonly struct VoxelImpostorBuildResult
    {
        public readonly string ImpostorAssetPath;
        public readonly string PrefabAssetPath;

        public VoxelImpostorBuildResult(string impostorAssetPath, string prefabAssetPath)
        {
            ImpostorAssetPath = impostorAssetPath;
            PrefabAssetPath = prefabAssetPath;
        }
    }

    internal readonly struct VoxelImpostorBatchFailure
    {
        public readonly string ManifestAssetPath;
        public readonly string Error;

        public VoxelImpostorBatchFailure(string manifestAssetPath, string error)
        {
            ManifestAssetPath = manifestAssetPath;
            Error = error;
        }
    }

    internal enum VoxelImpostorBatchSkipReason
    {
        BelowMinimumSize,
        AtlasBudget,
        DisabledByUser
    }

    internal readonly struct VoxelImpostorBatchSkip
    {
        public readonly string ManifestAssetPath;
        public readonly VoxelImpostorBatchSkipReason Reason;

        public VoxelImpostorBatchSkip(
            string manifestAssetPath, VoxelImpostorBatchSkipReason reason)
        {
            ManifestAssetPath = manifestAssetPath;
            Reason = reason;
        }
    }

    internal sealed class VoxelImpostorBatchOptions
    {
        public const int DefaultAtlasBudgetMb = 512;

        public bool SelectQualityBySize = true;
        public VoxelImpostorQuality FixedQuality = VoxelImpostorQuality.Medium;
        public long MaximumEstimatedAtlasBytes =
            DefaultAtlasBudgetMb * 1024L * 1024L;
    }

    internal readonly struct VoxelImpostorBatchPlanEntry
    {
        public readonly string ManifestAssetPath;
        public readonly VoxelImpostorQuality RequestedQuality;
        public readonly VoxelImpostorQuality Quality;
        public readonly float ModelSize;
        public readonly long EstimatedAtlasBytes;
        public bool WasReduced => Quality != RequestedQuality;

        public VoxelImpostorBatchPlanEntry(
            string manifestAssetPath, VoxelImpostorQuality requestedQuality,
            VoxelImpostorQuality quality,
            float modelSize, long estimatedAtlasBytes)
        {
            ManifestAssetPath = manifestAssetPath;
            RequestedQuality = requestedQuality;
            Quality = quality;
            ModelSize = modelSize;
            EstimatedAtlasBytes = estimatedAtlasBytes;
        }
    }

    internal sealed class VoxelImpostorBatchPlan
    {
        public readonly int CandidateCount;
        public readonly VoxelImpostorBatchPlanEntry[] Entries;
        public readonly VoxelImpostorBatchSkip[] Skips;
        public readonly long EstimatedAtlasBytes;
        public int ReducedQualityCount => Entries.Count(entry => entry.WasReduced);

        public VoxelImpostorBatchPlan(
            int candidateCount, IEnumerable<VoxelImpostorBatchPlanEntry> entries,
            IEnumerable<VoxelImpostorBatchSkip> skips, long estimatedAtlasBytes)
        {
            CandidateCount = candidateCount;
            Entries = entries.ToArray();
            Skips = skips.ToArray();
            EstimatedAtlasBytes = estimatedAtlasBytes;
        }
    }

    internal sealed class VoxelImpostorBatchBuildResult
    {
        public readonly int CandidateCount;
        public readonly VoxelImpostorBuildResult[] Builds;
        public readonly VoxelImpostorBatchFailure[] Failures;
        public readonly VoxelImpostorBatchSkip[] Skips;
        public readonly long EstimatedAtlasBytes;
        public readonly int ReducedQualityCount;
        public readonly bool Cancelled;

        public int GeneratedCount => Builds.Length;
        public int FailedCount => Failures.Length;
        public int SkippedCount => Skips.Length;
        public int SkippedForSizeCount => Skips.Count(skip =>
            skip.Reason == VoxelImpostorBatchSkipReason.BelowMinimumSize);
        public int SkippedForBudgetCount => Skips.Count(skip =>
            skip.Reason == VoxelImpostorBatchSkipReason.AtlasBudget);
        public int SkippedDisabledCount => Skips.Count(skip =>
            skip.Reason == VoxelImpostorBatchSkipReason.DisabledByUser);
        public int RemainingCount => Mathf.Max(
            0, CandidateCount - GeneratedCount - FailedCount - SkippedCount);

        public VoxelImpostorBatchBuildResult(
            int candidateCount, IEnumerable<VoxelImpostorBuildResult> builds,
            IEnumerable<VoxelImpostorBatchFailure> failures, bool cancelled,
            IEnumerable<VoxelImpostorBatchSkip> skips = null,
            long estimatedAtlasBytes = 0, int reducedQualityCount = 0)
        {
            CandidateCount = candidateCount;
            Builds = builds.ToArray();
            Failures = failures.ToArray();
            Skips = skips?.ToArray() ?? Array.Empty<VoxelImpostorBatchSkip>();
            EstimatedAtlasBytes = Math.Max(0, estimatedAtlasBytes);
            ReducedQualityCount = Math.Max(0, reducedQualityCount);
            Cancelled = cancelled;
        }
    }

    /// <summary>
    /// Reflection-only bridge to Amplify Impostors. Voxel Bridge keeps compiling when the
    /// optional asset is removed and refuses to bake against an unvalidated vendor API.
    /// </summary>
    internal static class AmplifyImpostorIntegration
    {
        internal const string SupportedVersion = "1.0.4";
        private const string ComponentTypeName = "AmplifyImpostors.AmplifyImpostor";
        private const string AssetTypeName = "AmplifyImpostors.AmplifyImpostorAsset";
        private const string VersionTypeName = "AmplifyImpostors.VersionInfo";
        private const string DefaultPresetPath =
            "Assets/AmplifyImpostors/Plugins/EditorResources/Presets/BakePreset.asset";

        private static bool apiResolutionAttempted;
        private static bool cachedCanBake;
        private static Api cachedApi;
        private static AmplifyImpostorCompatibility cachedCompatibility;

        private sealed class BatchCandidate
        {
            public string ManifestAssetPath;
            public VoxelImpostorQuality DesiredQuality;
            public VoxelImpostorQuality SelectedQuality;
            public float ModelSize;
            public long EstimatedAtlasBytes;
        }

        private sealed class Api
        {
            public Type ComponentType;
            public Type AssetType;
            public PropertyInfo DataProperty;
            public PropertyInfo RootTransformProperty;
            public PropertyInfo LodGroupProperty;
            public PropertyInfo RenderersProperty;
            public FieldInfo LodReplacementField;
            public FieldInfo FolderPathField;
            public FieldInfo ImpostorNameField;
            public FieldInfo RenderPipelineField;
            public MethodInfo RenderMethod;
            public MethodInfo CheckHdrpMaterialMethod;
            public FieldInfo MeshField;
            public FieldInfo MaterialField;
            public FieldInfo ImpostorTypeField;
            public FieldInfo LockedSizesField;
            public FieldInfo SelectedSizeField;
            public FieldInfo TextureSizeField;
            public FieldInfo DecoupleFramesField;
            public FieldInfo HorizontalFramesField;
            public FieldInfo VerticalFramesField;
            public FieldInfo PixelPaddingField;
            public FieldInfo MaxVerticesField;
            public FieldInfo ToleranceField;
            public FieldInfo NormalScaleField;
            public FieldInfo PresetField;
            public FieldInfo BakeShaderField;
            public string Version;
        }

        public static AmplifyImpostorCompatibility GetCompatibility()
        {
            TryResolveApi(out _, out AmplifyImpostorCompatibility compatibility);
            return compatibility;
        }

        public static VoxelImpostorBuildResult GenerateOrUpdate(
            string manifestAssetPath, VoxelImpostorProfile profile,
            VoxelImpostorQuality quality)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            quality = VoxelImpostorProfile.NormalizeQuality(quality);
            VoxelImpostorSettings settings = profile.GetSettings(quality);
            manifestAssetPath = VoxelLodPipeline.NormalizeAssetPath(manifestAssetPath);
            if (!VoxelLodPipeline.TryReadManifest(manifestAssetPath, out VoxelLodSetManifest manifest))
                throw new InvalidDataException("El manifiesto de la familia voxel no es válido.");
            if (manifest.lods == null || manifest.lods.Length == 0)
                throw new InvalidDataException("La familia no contiene ningún LOD voxel.");
            if (!TryGetLastVoxelTransition(manifest, out float lastVoxelTransition))
                throw new InvalidDataException("No se pudo determinar la transición del último LOD voxel.");
            if (!settings.TryValidate(lastVoxelTransition, out string profileError))
                throw new InvalidOperationException(profileError);
            if (!TryResolveApi(out Api api, out AmplifyImpostorCompatibility compatibility))
                throw new InvalidOperationException(compatibility.Message);

            string prefabPath = VoxelLodPipeline.NormalizeAssetPath(manifest.prefabAssetPath);
            GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefabAsset == null)
            {
                prefabPath = VoxelLodPipeline.RebuildPrefab(manifestAssetPath);
                prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            }
            if (prefabAsset == null)
                throw new InvalidDataException("No se encontró el prefab de la familia voxel.");

            string familyFolder = VoxelLodPipeline.NormalizeAssetPath(
                Path.GetDirectoryName(manifestAssetPath));
            string outputFolder = familyFolder + "/Impostor";
            VoxelLodPipeline.EnsureAssetFolder(outputFolder);
            string safeName = VoxelLodPipeline.MakeSafeFileName(manifest.sourceName);
            string impostorName = safeName + "_Impostor";
            string impostorAssetPath = outputFolder + "/" + impostorName + ".asset";

            Object data = AssetDatabase.LoadMainAssetAtPath(impostorAssetPath);
            if (data != null && !api.AssetType.IsInstanceOfType(data))
                throw new InvalidDataException(
                    $"'{impostorAssetPath}' existe pero no es un asset compatible de Amplify Impostors.");
            if (data == null)
            {
                data = ScriptableObject.CreateInstance(api.AssetType);
                data.name = impostorName;
                AssetDatabase.CreateAsset(data, impostorAssetPath);
            }
            ConfigureData(api, data, settings);
            AssetDatabase.SaveAssets();

            Scene previewScene = default;
            try
            {
                previewScene = EditorSceneManager.NewPreviewScene();
                var instance = PrefabUtility.InstantiatePrefab(prefabAsset, previewScene) as GameObject;
                if (instance == null)
                    throw new InvalidOperationException("No se pudo crear la instancia temporal del prefab.");
                instance.SetActive(true);

                LODGroup lodGroup = instance.GetComponent<LODGroup>();
                if (lodGroup == null) throw new InvalidDataException("El prefab no contiene un LODGroup.");
                LOD[] lods = lodGroup.GetLODs();
                if (lods.Length == 0)
                    throw new InvalidDataException("El LODGroup no contiene niveles para hornear.");
                Renderer[] sourceRenderers = (lods[0].renderers ?? Array.Empty<Renderer>())
                    .Where(renderer => renderer != null)
                    .ToArray();
                if (sourceRenderers.Length == 0)
                    throw new InvalidDataException("LOD0 no contiene renderers para hornear el impostor.");

                Component component = instance.AddComponent(api.ComponentType);
                api.DataProperty.SetValue(component, data);
                api.RootTransformProperty.SetValue(component, instance.transform);
                api.LodGroupProperty.SetValue(component, lodGroup);
                api.RenderersProperty.SetValue(component, sourceRenderers);
                api.LodReplacementField.SetValue(component,
                    Enum.ToObject(api.LodReplacementField.FieldType, 0)); // DoNothing
                api.FolderPathField.SetValue(component, outputFolder);
                api.ImpostorNameField.SetValue(component, impostorName);
                ConfigureRenderPipeline(api, component);

                Object originalPreset = api.PresetField.GetValue(data) as Object;
                Object temporaryPreset = CreateHdrpBakePreset(api);
                if (temporaryPreset != null)
                    api.PresetField.SetValue(data, temporaryPreset);
                try
                {
                    api.RenderMethod.Invoke(component, new[] { data });
                }
                catch (TargetInvocationException exception) when (exception.InnerException != null)
                {
                    throw new InvalidOperationException(
                        "Amplify Impostors no pudo completar el horneado: " +
                        exception.InnerException.Message, exception.InnerException);
                }
                finally
                {
                    if (temporaryPreset != null)
                    {
                        api.PresetField.SetValue(data, originalPreset);
                        Object.DestroyImmediate(temporaryPreset);
                    }
                }
                ValidateRenderPipeline(api, component);

                if (!(api.MeshField.GetValue(data) is Mesh) ||
                    !(api.MaterialField.GetValue(data) is Material generatedMaterial))
                    throw new InvalidOperationException(
                        "Amplify terminó sin producir el mesh o el material del impostor.");

                ConfigureRenderPipeline(api, component);
                ConfigureGeneratedMaterialForActivePipeline(
                    generatedMaterial, settings.ImpostorType);
                if (GetActiveRenderPipeline() == ActiveRenderPipeline.Hdrp)
                    api.CheckHdrpMaterialMethod.Invoke(component, null);
                EditorUtility.SetDirty(data);
                AssetDatabase.SaveAssets();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                if (previewScene.IsValid()) EditorSceneManager.ClosePreviewScene(previewScene);
            }

            manifest.formatVersion = Mathf.Max(3, manifest.formatVersion);
            manifest.impostor = new VoxelImpostorEntry
            {
                assetPath = impostorAssetPath,
                profileAssetPath = AssetDatabase.GetAssetPath(profile),
                quality = quality,
                amplifyVersion = api.Version,
                sourceLodIndex = 0,
                cullScreenHeight = settings.CullScreenHeight
            };
            manifest.impostorDisabled = false;
            VoxelLodPipeline.SaveManifest(manifestAssetPath, manifest);
            prefabPath = VoxelLodPipeline.RebuildPrefab(manifestAssetPath);
            ConfigureGeneratedTextureStreaming(outputFolder);
            return new VoxelImpostorBuildResult(impostorAssetPath, prefabPath);
        }

        internal static string RemoveFromFamily(string manifestAssetPath)
        {
            manifestAssetPath = VoxelLodPipeline.NormalizeAssetPath(manifestAssetPath);
            if (!VoxelLodPipeline.TryReadManifest(
                    manifestAssetPath, out VoxelLodSetManifest manifest))
                throw new InvalidDataException("El manifiesto de la familia voxel no es válido.");
            if (!HasConfiguredImpostor(manifest))
                throw new InvalidOperationException("La familia no contiene un impostor.");

            VoxelImpostorEntry previousImpostor = manifest.impostor;
            bool previousDisabled = manifest.impostorDisabled;
            manifest.impostor = null;
            manifest.impostorDisabled = true;
            VoxelLodPipeline.SaveManifest(manifestAssetPath, manifest);

            try
            {
                return VoxelLodPipeline.RebuildPrefab(manifestAssetPath);
            }
            catch
            {
                manifest.impostor = previousImpostor;
                manifest.impostorDisabled = previousDisabled;
                VoxelLodPipeline.SaveManifest(manifestAssetPath, manifest);
                try
                {
                    VoxelLodPipeline.RebuildPrefab(manifestAssetPath);
                }
                catch (Exception rollbackException)
                {
                    Debug.LogException(rollbackException);
                }
                throw;
            }
        }

        internal static bool HasConfiguredImpostor(VoxelLodSetManifest manifest) =>
            !string.IsNullOrWhiteSpace(manifest?.impostor?.assetPath);

        internal static int ConfigureGeneratedTextureStreaming(string outputFolder)
        {
            outputFolder = VoxelLodPipeline.NormalizeAssetPath(outputFolder).TrimEnd('/');
            string folderPrefix = outputFolder + "/";
            int changedCount = 0;

            string[] texturePaths = AssetDatabase.FindAssets(
                    "t:Texture2D", new[] { outputFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(VoxelLodPipeline.NormalizeAssetPath)
                .Where(path => path.StartsWith(
                    folderPrefix, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (string path in texturePaths)
            {
                if (!(AssetImporter.GetAtPath(path) is TextureImporter importer))
                    continue;

                bool changed = !importer.mipmapEnabled || !importer.streamingMipmaps ||
                               importer.streamingMipmapsPriority != 0;
                if (!changed) continue;

                importer.mipmapEnabled = true;
                importer.streamingMipmaps = true;
                importer.streamingMipmapsPriority = 0;
                importer.SaveAndReimport();
                changedCount++;
            }

            return changedCount;
        }

        private enum ActiveRenderPipeline
        {
            BuiltIn,
            Hdrp,
            Urp,
            Custom
        }

        private static ActiveRenderPipeline GetActiveRenderPipeline()
        {
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline ??
                                           GraphicsSettings.defaultRenderPipeline;
            if (pipeline == null) return ActiveRenderPipeline.BuiltIn;

            string typeName = pipeline.GetType().FullName ?? pipeline.GetType().Name;
            if (typeName.IndexOf("HDRenderPipeline", StringComparison.OrdinalIgnoreCase) >= 0)
                return ActiveRenderPipeline.Hdrp;
            if (typeName.IndexOf(
                    "UniversalRenderPipeline", StringComparison.OrdinalIgnoreCase) >= 0)
                return ActiveRenderPipeline.Urp;
            return ActiveRenderPipeline.Custom;
        }

        private static void ConfigureRenderPipeline(Api api, Component component)
        {
            string enumName = GetActiveRenderPipelineEnumName();
            if (!Enum.TryParse(api.RenderPipelineField.FieldType, enumName, out object value))
                throw new InvalidDataException(
                    $"Amplify Impostors no expone el pipeline '{enumName}' esperado.");
            api.RenderPipelineField.SetValue(component, value);
        }

        private static void ValidateRenderPipeline(Api api, Component component)
        {
            string expected = GetActiveRenderPipelineEnumName();
            string detected = api.RenderPipelineField.GetValue(component)?.ToString();
            if (!string.Equals(expected, detected, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Amplify Impostors detectó el pipeline '{detected ?? "desconocido"}' " +
                    $"durante el horneado; se esperaba '{expected}'.");
        }

        private static string GetActiveRenderPipelineEnumName()
        {
            return GetActiveRenderPipeline() switch
            {
                ActiveRenderPipeline.Hdrp => "HDRP",
                ActiveRenderPipeline.Urp => "URP",
                ActiveRenderPipeline.Custom => "Custom",
                _ => "None"
            };
        }

        private static Object CreateHdrpBakePreset(Api api)
        {
            if (GetActiveRenderPipeline() != ActiveRenderPipeline.Hdrp) return null;

            Object sourcePreset = AssetDatabase.LoadMainAssetAtPath(
                "Assets/AmplifyImpostors/Plugins/EditorResources/Presets/BakePreset.asset");
            Shader bakeShader = Shader.Find("Hidden/Voxel Bridge/Impostor Bake HDRP");
            if (sourcePreset == null || bakeShader == null)
                throw new InvalidDataException(
                    "No se encontró el preset o el shader de horneado HDRP de Amplify Impostors.");
            if (!bakeShader.isSupported)
                throw new InvalidDataException(
                    $"El shader de horneado HDRP '{bakeShader.name}' no es compatible con " +
                    "la configuración gráfica activa.");

            Object preset = Object.Instantiate(sourcePreset);
            preset.name = "Voxel Bridge HDRP Bake Preset";
            api.BakeShaderField.SetValue(preset, bakeShader);
            return preset;
        }

        internal static bool ConfigureGeneratedMaterialForActivePipeline(
            Material material, VoxelImpostorType impostorType)
        {
            if (material == null) throw new ArgumentNullException(nameof(material));
            ActiveRenderPipeline pipeline = GetActiveRenderPipeline();
            if (pipeline == ActiveRenderPipeline.Custom) return false;

            string projection = impostorType == VoxelImpostorType.Spherical
                ? "Spherical"
                : "Octahedron";
            string suffix = pipeline switch
            {
                ActiveRenderPipeline.Hdrp => " HDRP",
                ActiveRenderPipeline.Urp => " URP",
                _ => string.Empty
            };
            string shaderName = $"Hidden/Amplify Impostors/{projection} Impostor{suffix}";
            Shader shader = Shader.Find(shaderName);
            if (shader == null)
                throw new InvalidDataException(
                    $"No se encontró el shader runtime '{shaderName}' de Amplify Impostors.");
            if (material.shader == shader) return false;

            material.shader = shader;
            EditorUtility.SetDirty(material);
            return true;
        }

        internal static VoxelImpostorBatchBuildResult GenerateForBatch(
            VoxelLodBatchBuildResult batch, VoxelImpostorProfile profile,
            VoxelImpostorQuality quality, Func<float, string, bool> cancelProgress = null,
            Func<string, VoxelImpostorProfile, VoxelImpostorQuality,
                VoxelImpostorBuildResult> generate = null)
        {
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            return GenerateForManifests(
                batch.Items.Where(item => item.Succeeded)
                    .Select(item => item.BuildResult.ManifestAssetPath),
                profile, quality, cancelProgress, generate);
        }

        internal static VoxelImpostorBatchBuildResult GenerateForBatch(
            VoxelLodBatchBuildResult batch, VoxelImpostorProfile profile,
            VoxelImpostorBatchOptions options,
            Func<float, string, bool> cancelProgress = null,
            Func<string, VoxelImpostorProfile, VoxelImpostorQuality,
                VoxelImpostorBuildResult> generate = null)
        {
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            VoxelImpostorBatchPlan plan = CreateBatchPlan(
                batch.Items.Where(item => item.Succeeded)
                    .Select(item => item.BuildResult.ManifestAssetPath),
                profile, options);
            return GeneratePlannedBatch(plan, profile, cancelProgress, generate);
        }

        internal static VoxelImpostorBatchPlan CreateBatchPlan(
            IEnumerable<string> manifestAssetPaths, VoxelImpostorProfile profile,
            VoxelImpostorBatchOptions options)
        {
            if (manifestAssetPaths == null)
                throw new ArgumentNullException(nameof(manifestAssetPaths));
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            options ??= new VoxelImpostorBatchOptions();
            if (options.SelectQualityBySize &&
                !profile.TryValidateAutomaticPolicy(out string policyError))
                throw new InvalidOperationException(policyError);

            string[] paths = manifestAssetPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(VoxelLodPipeline.NormalizeAssetPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var candidates = new List<BatchCandidate>(paths.Length);
            var skips = new List<VoxelImpostorBatchSkip>();

            foreach (string path in paths)
            {
                float modelSize = 0f;
                if (VoxelLodPipeline.TryReadManifest(path, out VoxelLodSetManifest manifest))
                {
                    if (manifest.impostorDisabled)
                    {
                        skips.Add(new VoxelImpostorBatchSkip(
                            path, VoxelImpostorBatchSkipReason.DisabledByUser));
                        continue;
                    }
                    modelSize = manifest.lodGroupSize;
                }
                bool validSize = !float.IsNaN(modelSize) && !float.IsInfinity(modelSize) &&
                                 modelSize > 0f;
                if (options.SelectQualityBySize && validSize &&
                    modelSize < profile.MinimumImpostorSize)
                {
                    skips.Add(new VoxelImpostorBatchSkip(
                        path, VoxelImpostorBatchSkipReason.BelowMinimumSize));
                    continue;
                }

                VoxelImpostorQuality quality = VoxelImpostorProfile.NormalizeQuality(
                    options.FixedQuality);
                if (options.SelectQualityBySize && validSize)
                    profile.TrySelectAutomaticQuality(modelSize, out quality);
                candidates.Add(new BatchCandidate
                {
                    ManifestAssetPath = path,
                    DesiredQuality = VoxelImpostorProfile.NormalizeQuality(quality),
                    ModelSize = modelSize
                });
            }

            candidates.Sort((left, right) =>
            {
                int bySize = right.ModelSize.CompareTo(left.ModelSize);
                return bySize != 0
                    ? bySize
                    : StringComparer.OrdinalIgnoreCase.Compare(
                        left.ManifestAssetPath, right.ManifestAssetPath);
            });

            long budget = options.MaximumEstimatedAtlasBytes <= 0
                ? long.MaxValue
                : options.MaximumEstimatedAtlasBytes;
            long estimatedBytes = 0;
            var selectedCandidates = new List<BatchCandidate>(candidates.Count);
            foreach (BatchCandidate candidate in candidates)
            {
                VoxelImpostorQuality[] fallbacks = EnumerateQualityFallbacks(
                        candidate.DesiredQuality, options.SelectQualityBySize)
                    .Distinct()
                    .ToArray();
                VoxelImpostorQuality baselineQuality = fallbacks
                    .OrderBy(quality => EstimateAtlasBytes(profile, quality))
                    .First();
                long baselineBytes = EstimateAtlasBytes(profile, baselineQuality);
                if (baselineBytes > budget - estimatedBytes)
                {
                    skips.Add(new VoxelImpostorBatchSkip(
                        candidate.ManifestAssetPath,
                        VoxelImpostorBatchSkipReason.AtlasBudget));
                    continue;
                }

                candidate.SelectedQuality = baselineQuality;
                candidate.EstimatedAtlasBytes = baselineBytes;
                selectedCandidates.Add(candidate);
                estimatedBytes += baselineBytes;
            }

            if (options.SelectQualityBySize)
            {
                foreach (BatchCandidate candidate in selectedCandidates)
                {
                    foreach (VoxelImpostorQuality quality in EnumerateQualityFallbacks(
                                 candidate.DesiredQuality, true).Distinct())
                    {
                        long bytes = EstimateAtlasBytes(profile, quality);
                        long additionalBytes = bytes - candidate.EstimatedAtlasBytes;
                        if (additionalBytes < 0 ||
                            additionalBytes > budget - estimatedBytes)
                            continue;

                        candidate.SelectedQuality = quality;
                        candidate.EstimatedAtlasBytes = bytes;
                        estimatedBytes += additionalBytes;
                        break;
                    }
                }
            }

            VoxelImpostorBatchPlanEntry[] entries = selectedCandidates
                .Select(candidate => new VoxelImpostorBatchPlanEntry(
                    candidate.ManifestAssetPath, candidate.DesiredQuality,
                    candidate.SelectedQuality, candidate.ModelSize,
                    candidate.EstimatedAtlasBytes))
                .ToArray();

            return new VoxelImpostorBatchPlan(
                paths.Length, entries, skips, estimatedBytes);
        }

        internal static long EstimateAtlasBytes(
            VoxelImpostorProfile profile, VoxelImpostorQuality quality)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            int resolution = profile.GetSettings(quality).TextureResolution;
            const int estimatedBaseBytesPerPixel = 9;
            const int mipNumerator = 4;
            const int mipDenominator = 3;
            const int assetOverheadBytes = 256 * 1024;
            return checked((long)resolution * resolution * estimatedBaseBytesPerPixel *
                           mipNumerator / mipDenominator + assetOverheadBytes);
        }

        private static IEnumerable<VoxelImpostorQuality> EnumerateQualityFallbacks(
            VoxelImpostorQuality desired, bool allowFallback)
        {
            desired = VoxelImpostorProfile.NormalizeQuality(desired);
            yield return desired;
            if (!allowFallback) yield break;
            if (desired is VoxelImpostorQuality.High or VoxelImpostorQuality.Architecture)
                yield return VoxelImpostorQuality.Medium;
            if (desired != VoxelImpostorQuality.Low)
                yield return VoxelImpostorQuality.Low;
        }

        private static VoxelImpostorBatchBuildResult GeneratePlannedBatch(
            VoxelImpostorBatchPlan plan, VoxelImpostorProfile profile,
            Func<float, string, bool> cancelProgress,
            Func<string, VoxelImpostorProfile, VoxelImpostorQuality,
                VoxelImpostorBuildResult> generate)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            var builds = new List<VoxelImpostorBuildResult>();
            var failures = new List<VoxelImpostorBatchFailure>();
            generate ??= GenerateOrUpdate;

            for (int index = 0; index < plan.Entries.Length; index++)
            {
                VoxelImpostorBatchPlanEntry entry = plan.Entries[index];
                if (cancelProgress != null && cancelProgress(
                        (float)index / plan.Entries.Length,
                        $"Impostor {index + 1} de {plan.Entries.Length} · " +
                        Path.GetFileName(entry.ManifestAssetPath)))
                    return new VoxelImpostorBatchBuildResult(
                        plan.CandidateCount, builds, failures, true,
                        plan.Skips, plan.EstimatedAtlasBytes,
                        plan.ReducedQualityCount);

                try
                {
                    builds.Add(generate(entry.ManifestAssetPath, profile, entry.Quality));
                }
                catch (OperationCanceledException)
                {
                    return new VoxelImpostorBatchBuildResult(
                        plan.CandidateCount, builds, failures, true,
                        plan.Skips, plan.EstimatedAtlasBytes,
                        plan.ReducedQualityCount);
                }
                catch (Exception exception)
                {
                    failures.Add(new VoxelImpostorBatchFailure(
                        entry.ManifestAssetPath, exception.Message));
                }
                finally
                {
                    VoxelLodBatchMemoryCleaner.ReleaseUnusedMemory(profile);
                }
            }

            cancelProgress?.Invoke(
                1f, $"Impostores terminados: {builds.Count} de {plan.Entries.Length}");
            return new VoxelImpostorBatchBuildResult(
                plan.CandidateCount, builds, failures, false,
                plan.Skips, plan.EstimatedAtlasBytes,
                plan.ReducedQualityCount);
        }

        internal static VoxelImpostorBatchBuildResult GenerateForManifests(
            IEnumerable<string> manifestAssetPaths, VoxelImpostorProfile profile,
            VoxelImpostorQuality quality, Func<float, string, bool> cancelProgress = null,
            Func<string, VoxelImpostorProfile, VoxelImpostorQuality,
                VoxelImpostorBuildResult> generate = null)
        {
            if (manifestAssetPaths == null)
                throw new ArgumentNullException(nameof(manifestAssetPaths));
            if (profile == null) throw new ArgumentNullException(nameof(profile));

            string[] paths = manifestAssetPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(VoxelLodPipeline.NormalizeAssetPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var builds = new List<VoxelImpostorBuildResult>();
            var failures = new List<VoxelImpostorBatchFailure>();
            generate ??= GenerateOrUpdate;

            for (int index = 0; index < paths.Length; index++)
            {
                string path = paths[index];
                if (cancelProgress != null && cancelProgress(
                        (float)index / paths.Length,
                        $"Impostor {index + 1} de {paths.Length} · {Path.GetFileName(path)}"))
                    return new VoxelImpostorBatchBuildResult(
                        paths.Length, builds, failures, true);

                try
                {
                    builds.Add(generate(path, profile, quality));
                }
                catch (OperationCanceledException)
                {
                    return new VoxelImpostorBatchBuildResult(
                        paths.Length, builds, failures, true);
                }
                catch (Exception exception)
                {
                    failures.Add(new VoxelImpostorBatchFailure(path, exception.Message));
                }
                finally
                {
                    VoxelLodBatchMemoryCleaner.ReleaseUnusedMemory(profile);
                }
            }

            cancelProgress?.Invoke(1f, $"Impostores terminados: {builds.Count} de {paths.Length}");
            return new VoxelImpostorBatchBuildResult(paths.Length, builds, failures, false);
        }

        internal static string[] FindPendingManifestAssetPaths(string exportAssetFolder)
        {
            exportAssetFolder = VoxelLodPipeline.NormalizeAssetPath(exportAssetFolder);
            if (!VoxelLodPipeline.IsAssetFolder(exportAssetFolder)) return Array.Empty<string>();

            return AssetDatabase.FindAssets("t:TextAsset", new[] { exportAssetFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.EndsWith(".voxset.json", StringComparison.OrdinalIgnoreCase))
                .Where(path => VoxelLodPipeline.TryReadManifest(
                    path, out VoxelLodSetManifest manifest) &&
                    !manifest.impostorDisabled &&
                    (manifest.impostor == null ||
                     string.IsNullOrWhiteSpace(manifest.impostor.assetPath) ||
                     !File.Exists(VoxelLodPipeline.AssetPathToAbsolute(
                         manifest.impostor.assetPath))))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        internal static bool TryGetLastVoxelTransition(
            VoxelLodSetManifest manifest, out float transitionHeight)
        {
            transitionHeight = 0f;
            if (manifest?.lods == null || manifest.lods.Length == 0) return false;

            int lastLodIndex = manifest.lods.Max(entry => entry.lodIndex);
            VoxelLodEntry lastEntry = manifest.lods
                .Where(entry => entry != null)
                .OrderBy(entry => entry.lodIndex)
                .LastOrDefault();
            if (lastEntry != null && lastEntry.screenRelativeTransitionHeight > 0f)
            {
                transitionHeight = Mathf.Clamp01(lastEntry.screenRelativeTransitionHeight);
                return transitionHeight > 0f;
            }

            VoxelStyleProfile voxelProfile = string.IsNullOrEmpty(manifest.profileAssetPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<VoxelStyleProfile>(manifest.profileAssetPath);
            if (voxelProfile != null)
            {
                transitionHeight = manifest.lodGroupSize > 0f
                    ? voxelProfile.GetLodScreenHeight(lastLodIndex, manifest.lodGroupSize)
                    : voxelProfile.GetLodScreenHeight(lastLodIndex);
                return transitionHeight > 0f;
            }

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(manifest.prefabAssetPath);
            LODGroup group = prefab != null ? prefab.GetComponent<LODGroup>() : null;
            if (group != null)
            {
                LOD[] lods = group.GetLODs();
                int voxelLodCount = Mathf.Min(manifest.lods.Length, lods.Length);
                if (voxelLodCount > 0)
                {
                    transitionHeight = lods[voxelLodCount - 1].screenRelativeTransitionHeight;
                    return transitionHeight > 0f;
                }
            }

            transitionHeight = Mathf.Max(0.01f, 0.6f * Mathf.Pow(0.5f, lastLodIndex));
            return true;
        }

        internal static bool TryAppendExistingImpostor(
            GameObject root, LODGroup group, VoxelImpostorEntry entry,
            float largeModelRangeBlend, out string error)
        {
            error = null;
            if (root == null || group == null || entry == null)
            {
                error = "La configuración del impostor está incompleta.";
                return false;
            }

            Object data = AssetDatabase.LoadMainAssetAtPath(entry.assetPath);
            if (data == null)
            {
                error = $"No se encontró el asset '{entry.assetPath}'.";
                return false;
            }

            const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo meshField = data.GetType().GetField("Mesh", fields);
            FieldInfo materialField = data.GetType().GetField("Material", fields);
            if (meshField == null || materialField == null)
            {
                error = "El asset instalado cambió la estructura esperada de Mesh/Material.";
                return false;
            }
            if (!(meshField.GetValue(data) is Mesh mesh) ||
                !(materialField.GetValue(data) is Material material))
            {
                error = "El asset del impostor todavía no contiene Mesh y Material.";
                return false;
            }

            LOD[] voxelLods = group.GetLODs();
            if (voxelLods.Length == 0)
            {
                error = "El prefab no contiene LODs voxel.";
                return false;
            }
            float lastVoxelTransition = voxelLods[voxelLods.Length - 1]
                .screenRelativeTransitionHeight;
            if (entry.cullScreenHeight <= 0f || entry.cullScreenHeight >= lastVoxelTransition)
            {
                error = $"El descarte {entry.cullScreenHeight:0.####} debe ser menor que " +
                        $"la transición voxel {lastVoxelTransition:0.####}.";
                return false;
            }

            var child = new GameObject("Impostor", typeof(MeshFilter), typeof(MeshRenderer));
            child.transform.SetParent(root.transform, false);
            child.GetComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = child.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;

            var lods = new LOD[voxelLods.Length + 1];
            ExpandVoxelLodRangesTowardImpostor(
                voxelLods, entry.cullScreenHeight, largeModelRangeBlend);
            Array.Copy(voxelLods, lods, voxelLods.Length);
            lods[lods.Length - 1] = new LOD(entry.cullScreenHeight, new Renderer[] { renderer });
            for (int i = 0; i < lods.Length; i++) lods[i].fadeTransitionWidth = 0f;

            group.fadeMode = LODFadeMode.None;
            group.animateCrossFading = false;
            group.SetLODs(lods);
            group.RecalculateBounds();
            return true;
        }

        internal static void ExpandVoxelLodRangesTowardImpostor(
            LOD[] voxelLods, float impostorCullHeight,
            float largeModelRangeBlend = 0f)
        {
            if (voxelLods == null || voxelLods.Length <= 1) return;

            float[] originalHeights = voxelLods
                .Select(lod => lod.screenRelativeTransitionHeight)
                .ToArray();
            int lastIndex = voxelLods.Length - 1;
            for (int index = 1; index < lastIndex; index++)
                voxelLods[index].screenRelativeTransitionHeight =
                    Mathf.Lerp(originalHeights[index], originalHeights[index + 1], 0.5f);
            voxelLods[lastIndex].screenRelativeTransitionHeight =
                GetExpandedLastVoxelTransition(
                    originalHeights[lastIndex], impostorCullHeight, voxelLods.Length);

            largeModelRangeBlend = Mathf.Clamp01(largeModelRangeBlend);
            if (largeModelRangeBlend <= 0f) return;

            float largeModelLastTransition = Mathf.Lerp(
                originalHeights[lastIndex - 1], originalHeights[lastIndex], 0.5f);
            for (int index = 1; index <= lastIndex; index++)
            {
                float evenlySpacedTransition = Mathf.Lerp(
                    originalHeights[0], largeModelLastTransition,
                    index / (float)lastIndex);
                voxelLods[index].screenRelativeTransitionHeight = Mathf.Lerp(
                    voxelLods[index].screenRelativeTransitionHeight,
                    evenlySpacedTransition, largeModelRangeBlend);
            }
        }

        internal static float GetExpandedLastVoxelTransition(
            float originalTransition, float impostorCullHeight, int voxelLodCount) =>
            voxelLodCount > 1
                ? Mathf.Lerp(originalTransition, impostorCullHeight, 1f / 3f)
                : originalTransition;

        private static void ConfigureData(Api api, Object data, VoxelImpostorSettings settings)
        {
            api.ImpostorTypeField.SetValue(data,
                Enum.ToObject(api.ImpostorTypeField.FieldType, (int)settings.ImpostorType));
            api.LockedSizesField.SetValue(data, true);
            api.SelectedSizeField.SetValue(data, settings.TextureResolution);
            api.TextureSizeField.SetValue(data,
                new Vector2(settings.TextureResolution, settings.TextureResolution));
            api.DecoupleFramesField.SetValue(data, false);
            api.HorizontalFramesField.SetValue(data, settings.Frames);
            api.VerticalFramesField.SetValue(data, settings.Frames);
            api.PixelPaddingField.SetValue(data, settings.PixelPadding);
            api.MaxVerticesField.SetValue(data, settings.MaxVertices);
            api.ToleranceField.SetValue(data, settings.SilhouetteTolerance);
            api.NormalScaleField.SetValue(data, settings.NormalScale);

            Object preset = api.PresetField.GetValue(data) as Object;
            if (preset == null)
            {
                preset = AssetDatabase.LoadMainAssetAtPath(DefaultPresetPath);
                if (preset == null || !api.PresetField.FieldType.IsInstanceOfType(preset))
                {
                    string guid = AssetDatabase.FindAssets("t:AmplifyImpostorBakePreset")
                        .FirstOrDefault();
                    preset = string.IsNullOrEmpty(guid)
                        ? null
                        : AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(guid));
                }
                if (preset == null || !api.PresetField.FieldType.IsInstanceOfType(preset))
                    throw new InvalidDataException(
                        "No se encontró un Bake Preset compatible de Amplify Impostors.");
                api.PresetField.SetValue(data, preset);
            }

            EditorUtility.SetDirty(data);
        }

        private static bool TryResolveApi(
            out Api api, out AmplifyImpostorCompatibility compatibility)
        {
            if (!apiResolutionAttempted)
            {
                apiResolutionAttempted = true;
                cachedCanBake = ResolveApi(out cachedApi, out cachedCompatibility);
            }
            api = cachedApi;
            compatibility = cachedCompatibility;
            return cachedCanBake;
        }

        private static bool ResolveApi(
            out Api api, out AmplifyImpostorCompatibility compatibility)
        {
            api = null;
            Type componentType = FindType(ComponentTypeName);
            Type assetType = FindType(AssetTypeName);
            if (componentType == null || assetType == null)
            {
                compatibility = new AmplifyImpostorCompatibility(
                    AmplifyImpostorCompatibilityStatus.NotInstalled, null,
                    "Amplify Impostors no está instalado. La familia voxel seguirá funcionando sin impostor.");
                return false;
            }

            Type versionType = FindType(VersionTypeName);
            MethodInfo versionMethod = versionType?.GetMethod("StaticToString",
                BindingFlags.Public | BindingFlags.Static);
            string version = null;
            try
            {
                version = versionMethod?.Invoke(null, null) as string;
            }
            catch (Exception)
            {
                // An updated vendor assembly may keep the member name but change its behavior.
            }
            if (string.IsNullOrEmpty(version))
            {
                compatibility = new AmplifyImpostorCompatibility(
                    AmplifyImpostorCompatibilityStatus.ApiConflict, null,
                    "Amplify Impostors está instalado, pero no expone la información de versión esperada. No se aplicó ningún cambio.");
                return false;
            }
            if (!version.Equals(SupportedVersion, StringComparison.Ordinal))
            {
                compatibility = new AmplifyImpostorCompatibility(
                    AmplifyImpostorCompatibilityStatus.UnsupportedVersion, version,
                    $"Amplify Impostors {version} no ha sido validado por este adaptador " +
                    $"(versión admitida: {SupportedVersion}). No se aplicó ningún cambio.");
                return false;
            }

            AmplifyPipelinePatchResult patchStatus =
                AmplifyImpostorPipelinePatcher.GetProjectStatus(out string patchDetail);
            if (patchStatus != AmplifyPipelinePatchResult.AlreadyApplied)
            {
                string message = patchStatus switch
                {
                    AmplifyPipelinePatchResult.Required =>
                        "Amplify Impostors requiere el parche de compatibilidad antes de hornear.",
                    AmplifyPipelinePatchResult.NotInstalled =>
                        "No se encontró la fuente instalada de Amplify Impostors.",
                    _ => "La fuente instalada de Amplify Impostors no admite el parche de compatibilidad."
                };
                if (!string.IsNullOrWhiteSpace(patchDetail)) message += " " + patchDetail;
                compatibility = new AmplifyImpostorCompatibility(
                    AmplifyImpostorCompatibilityStatus.ApiConflict, version, message);
                return false;
            }

            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            api = new Api
            {
                ComponentType = componentType,
                AssetType = assetType,
                DataProperty = componentType.GetProperty("Data", instance),
                RootTransformProperty = componentType.GetProperty("RootTransform", instance),
                LodGroupProperty = componentType.GetProperty("LodGroup", instance),
                RenderersProperty = componentType.GetProperty("Renderers", instance),
                LodReplacementField = componentType.GetField("m_lodReplacement", instance),
                FolderPathField = componentType.GetField("m_folderPath", instance),
                ImpostorNameField = componentType.GetField("m_impostorName", instance),
                RenderPipelineField = componentType.GetField("m_renderPipelineInUse", instance),
                RenderMethod = componentType.GetMethod("RenderAllDeferredGroups", instance,
                    null, new[] { assetType }, null),
                CheckHdrpMaterialMethod = componentType.GetMethod(
                    "CheckHDRPMaterial", instance, null, Type.EmptyTypes, null),
                MeshField = assetType.GetField("Mesh", instance),
                MaterialField = assetType.GetField("Material", instance),
                ImpostorTypeField = assetType.GetField("ImpostorType", instance),
                LockedSizesField = assetType.GetField("LockedSizes", instance),
                SelectedSizeField = assetType.GetField("SelectedSize", instance),
                TextureSizeField = assetType.GetField("TexSize", instance),
                DecoupleFramesField = assetType.GetField("DecoupleAxisFrames", instance),
                HorizontalFramesField = assetType.GetField("HorizontalFrames", instance),
                VerticalFramesField = assetType.GetField("VerticalFrames", instance),
                PixelPaddingField = assetType.GetField("PixelPadding", instance),
                MaxVerticesField = assetType.GetField("MaxVertices", instance),
                ToleranceField = assetType.GetField("Tolerance", instance),
                NormalScaleField = assetType.GetField("NormalScale", instance),
                PresetField = assetType.GetField("Preset", instance),
                Version = version
            };
            api.BakeShaderField = api.PresetField?.FieldType.GetField("BakeShader", instance);

            bool valid = typeof(MonoBehaviour).IsAssignableFrom(componentType) &&
                         typeof(ScriptableObject).IsAssignableFrom(assetType) &&
                         api.DataProperty?.PropertyType == assetType &&
                         api.RootTransformProperty?.PropertyType == typeof(Transform) &&
                         api.LodGroupProperty?.PropertyType == typeof(LODGroup) &&
                         api.RenderersProperty?.PropertyType == typeof(Renderer[]) &&
                         api.LodReplacementField?.FieldType.IsEnum == true &&
                          api.FolderPathField?.FieldType == typeof(string) &&
                          api.ImpostorNameField?.FieldType == typeof(string) &&
                          api.RenderPipelineField?.FieldType.IsEnum == true &&
                          api.RenderMethod != null && api.CheckHdrpMaterialMethod != null &&
                          api.MeshField != null &&
                         api.MaterialField != null && api.ImpostorTypeField?.FieldType.IsEnum == true &&
                         api.LockedSizesField != null && api.SelectedSizeField != null &&
                         api.TextureSizeField != null && api.DecoupleFramesField != null &&
                         api.HorizontalFramesField != null && api.VerticalFramesField != null &&
                         api.PixelPaddingField != null && api.MaxVerticesField != null &&
                          api.ToleranceField != null && api.NormalScaleField != null &&
                          api.PresetField != null &&
                          api.BakeShaderField?.FieldType == typeof(Shader);
            if (!valid)
            {
                api = null;
                compatibility = new AmplifyImpostorCompatibility(
                    AmplifyImpostorCompatibilityStatus.ApiConflict, version,
                    "La API instalada de Amplify Impostors no coincide con la firma validada. No se aplicó ningún cambio; revisa el warning antes de actualizar el adaptador.");
                return false;
            }

            compatibility = new AmplifyImpostorCompatibility(
                AmplifyImpostorCompatibilityStatus.Ready, version,
                $"Amplify Impostors {version} detectado. El horneado usará el pipeline activo y los renderers de LOD0.");
            return true;
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            return null;
        }
    }
}
