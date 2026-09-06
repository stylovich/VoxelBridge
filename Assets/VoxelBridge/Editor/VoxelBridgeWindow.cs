using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge
{
    /// <summary>Primary workflow for physically consistent voxel families and LODs.</summary>
    internal sealed class VoxelBridgeWindow : EditorWindow
    {
        private const string DefaultExportFolder = "Assets/VoxelBridgeExports";
        private const string DefaultImpostorProfilePath =
            "Assets/VoxelBridgeSettings/VoxelImpostorProfile.asset";

        private Object source;
        [SerializeField] private bool generateLod0Only = true;
        [SerializeField] private bool individualGenerateImpostor;
        [SerializeField] private bool individualPlaceInScene;
        [SerializeField] private bool individualDisableOriginalObject = true;
        [SerializeField, Min(256)] private int individualMemoryBudgetMb = 1024;
        [SerializeField] private bool individualAdaptInitialVoxelSize = true;
        [SerializeField, Range(0, 7)] private int individualMaximumInitialLodIndex = 2;
        [SerializeField, Min(100_000)] private int individualMaximumImportedVoxelCount =
            VoxelLodBatchOptions.DefaultMaximumImportedVoxelCount;
        private VoxelLodBatchSourceEstimate individualPreflight;
        private GameObject batchParent;
        [SerializeField] private bool batchReusePrefabSources = true;
        [SerializeField] private VoxelPrefabOverrideHandling batchModifiedPrefabHandling =
            VoxelPrefabOverrideHandling.UsePrefabSource;
        [SerializeField] private bool batchPlaceInScene;
        [SerializeField] private bool batchDisableOriginalRoot = true;
        [SerializeField, Min(256)] private int batchMemoryBudgetMb = 1024;
        [SerializeField] private bool batchSkipOverMemoryBudget = true;
        [SerializeField] private bool batchAdaptInitialVoxelSize = true;
        [SerializeField, Range(0, 7)] private int batchMaximumInitialLodIndex = 2;
        [SerializeField, Min(100_000)] private int batchMaximumImportedVoxelCount =
            VoxelLodBatchOptions.DefaultMaximumImportedVoxelCount;
        [SerializeField] private bool batchIgnoreInactiveObjects = true;
        [SerializeField] private bool batchResumeInterrupted = true;
        [SerializeField, Range(1, 25)] private int batchCleanupInterval = 1;
        [SerializeField] private bool batchGenerateImpostors;
        [SerializeField] private bool batchSelectImpostorQualityBySize = true;
        [SerializeField, Min(64)] private int batchImpostorAtlasBudgetMb =
            VoxelImpostorBatchOptions.DefaultAtlasBudgetMb;
        [SerializeField] private bool batchShowPlanDetails;
        private VoxelLodBatchPreflight batchPreflight;
        private VoxelStyleProfile styleProfile;
        private VoxelImpostorProfile impostorProfile;
        [SerializeField] private VoxelImpostorQuality impostorQuality = VoxelImpostorQuality.Medium;
        private VoxelColorMode colorMode = VoxelColorMode.MaterialAndTexture;
        [SerializeField] private VoxelConversionProfile conversionProfile;
        private Color singleColor = new Color32(180, 180, 180, 255);
        private float alphaCutoff = 0.1f;
        private string exportFolder = DefaultExportFolder;
        private Object manualParentVox;
        private int manualTargetLod = 1;
        private VoxelLodGenerationMode manualGenerationMode = VoxelLodGenerationMode.ReduceParent;
        private TextAsset lodSetManifest;
        private Object lastVoxAsset;
        private Object lastPrefabAsset;
        private Object lastImpostorAsset;
        private string lastVoxAssetPath;
        private string status;
        private Vector2 scroll;

        [MenuItem("Tools/Voxel Bridge/Physical Models and LODs", false, 100)]
        private static void OpenWindow()
        {
            VoxelBridgeWindow window = GetOrCreateWindow();
            if (VoxelBridgeSourceSelection.IsSupported(Selection.activeObject))
                window.source = Selection.activeObject;
            window.Show();
        }

        private static VoxelBridgeWindow GetOrCreateWindow()
        {
            var window = GetWindow<VoxelBridgeWindow>();
            window.titleContent = new GUIContent("Voxel LODs");
            window.minSize = new Vector2(460, 570);
            return window;
        }

        [MenuItem("Assets/Voxel Bridge/Generate Physical Model and LODs", false, 2100)]
        private static void OpenFromSelection() => OpenWindow();

        [MenuItem("Assets/Voxel Bridge/Generate Physical Model and LODs", true)]
        private static bool ValidateOpenFromSelection() =>
            VoxelBridgeSourceSelection.IsSupported(Selection.activeObject);

        [MenuItem("GameObject/Voxel Bridge/Generate Child Voxel Families and LODs", false, 48)]
        private static void OpenBatchFromHierarchy()
        {
            var window = GetOrCreateWindow();
            window.batchParent = Selection.activeGameObject;
            window.Show();
            window.Focus();
        }

        [MenuItem("GameObject/Voxel Bridge/Generate Child Voxel Families and LODs", true)]
        private static bool ValidateOpenBatchFromHierarchy() =>
            Selection.activeGameObject != null && Selection.activeGameObject.transform.childCount > 0;

        [MenuItem("Assets/Voxel Bridge/Edit Family LODs", false, 2101)]
        private static void OpenFamilyFromSelection()
        {
            var window = GetOrCreateWindow();
            if (!window.TryLoadFamilyFromAsset(Selection.activeObject)) return;
            window.Show();
            window.Focus();
        }

        [MenuItem("Assets/Voxel Bridge/Edit Family LODs", true)]
        private static bool ValidateOpenFamilyFromSelection() =>
            Selection.activeObject != null && VoxelLodPipeline.TryFindManifestForAsset(
                AssetDatabase.GetAssetPath(Selection.activeObject), out _, out _);

        [MenuItem("Assets/Voxel Bridge/Configure or Generate Final Impostor", false, 2102)]
        private static void OpenImpostorFromSelection()
        {
            var window = GetOrCreateWindow();
            if (!window.TryLoadFamilyFromAsset(Selection.activeObject)) return;
            window.status = "Family loaded. Select a profile to generate the final impostor.";
            window.Show();
            window.Focus();
        }

        [MenuItem("Assets/Voxel Bridge/Configure or Generate Final Impostor", true)]
        private static bool ValidateOpenImpostorFromSelection() =>
            ValidateOpenFamilyFromSelection();

        [MenuItem("Assets/Voxel Bridge/Remove Final Impostor", false, 2103)]
        private static void RemoveImpostorFromSelection()
        {
            if (!VoxelLodPipeline.TryFindManifestForAsset(
                    AssetDatabase.GetAssetPath(Selection.activeObject),
                    out string manifestPath, out VoxelLodSetManifest manifest))
                return;
            RemoveImpostorWithConfirmation(manifestPath, manifest, Selection.activeObject);
        }

        [MenuItem("Assets/Voxel Bridge/Remove Final Impostor", true)]
        private static bool ValidateRemoveImpostorFromSelection() =>
            Selection.activeObject != null && VoxelLodPipeline.TryFindManifestForAsset(
                AssetDatabase.GetAssetPath(Selection.activeObject), out _,
                out VoxelLodSetManifest manifest) &&
            AmplifyImpostorIntegration.HasConfiguredImpostor(manifest);

        [MenuItem("GameObject/Voxel Bridge/Edit This LOD in MagicaVoxel", false, 49)]
        private static void EditHierarchyLod()
        {
            if (TryResolveHierarchyLod(Selection.activeGameObject,
                    out Object voxAsset, out _, out _))
                MagicaVoxelLauncher.OpenAsset(voxAsset);
        }

        [MenuItem("GameObject/Voxel Bridge/Edit This LOD in MagicaVoxel", true)]
        private static bool ValidateEditHierarchyLod() =>
            TryResolveHierarchyLod(Selection.activeGameObject, out _, out _, out _);

        [MenuItem("GameObject/Voxel Bridge/Edit Family LODs", false, 50)]
        private static void OpenHierarchyFamily()
        {
            if (!TryResolveHierarchyFamily(Selection.activeGameObject,
                    out string prefabAssetPath, out _, out _))
                return;
            var window = GetOrCreateWindow();
            window.TryLoadFamilyFromAsset(AssetDatabase.LoadMainAssetAtPath(prefabAssetPath));
            window.Show();
            window.Focus();
        }

        [MenuItem("GameObject/Voxel Bridge/Edit Family LODs", true)]
        private static bool ValidateOpenHierarchyFamily() =>
            TryResolveHierarchyFamily(Selection.activeGameObject, out _, out _, out _);

        [MenuItem("GameObject/Voxel Bridge/Configure or Generate Final Impostor", false, 51)]
        private static void OpenHierarchyImpostor()
        {
            if (!TryResolveHierarchyFamily(Selection.activeGameObject,
                    out string prefabAssetPath, out _, out _))
                return;
            var window = GetOrCreateWindow();
            if (!window.TryLoadFamilyFromAsset(AssetDatabase.LoadMainAssetAtPath(prefabAssetPath))) return;
            window.status = "Family loaded. Select a profile to generate the final impostor.";
            window.Show();
            window.Focus();
        }

        [MenuItem("GameObject/Voxel Bridge/Configure or Generate Final Impostor", true)]
        private static bool ValidateOpenHierarchyImpostor() =>
            ValidateOpenHierarchyFamily();

        [MenuItem("GameObject/Voxel Bridge/Remove Final Impostor", false, 52)]
        private static void RemoveHierarchyImpostor()
        {
            if (!TryResolveHierarchyFamily(Selection.activeGameObject,
                    out string prefabAssetPath, out VoxelLodSetManifest manifest, out _) ||
                !VoxelLodPipeline.TryFindManifestForAsset(prefabAssetPath,
                    out string manifestPath, out _))
                return;
            RemoveImpostorWithConfirmation(
                manifestPath, manifest, Selection.activeGameObject);
        }

        [MenuItem("GameObject/Voxel Bridge/Remove Final Impostor", true)]
        private static bool ValidateRemoveHierarchyImpostor() =>
            TryResolveHierarchyFamily(Selection.activeGameObject,
                out _, out VoxelLodSetManifest manifest, out _) &&
            AmplifyImpostorIntegration.HasConfiguredImpostor(manifest);

        private void OnEnable()
        {
            if (source == null && VoxelBridgeSourceSelection.IsSupported(Selection.activeObject))
                source = Selection.activeObject;
            if (impostorProfile == null)
                impostorProfile = AssetDatabase.LoadAssetAtPath<VoxelImpostorProfile>(
                    DefaultImpostorProfilePath);
            impostorQuality = VoxelImpostorProfile.NormalizeQuality(impostorQuality);
            if (impostorProfile != null && impostorProfile.EnsureInitialized())
            {
                EditorUtility.SetDirty(impostorProfile);
                AssetDatabase.SaveAssets();
            }
        }

        private void OnHierarchyChange()
        {
            individualPreflight = null;
            batchPreflight = null;
            Repaint();
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("Physical Models and LODs", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Genera una familia de archivos .vox con unidad física consistente, sus LODs y el prefab final para Unity.",
                MessageType.Info);
            EditorGUILayout.Space(8);
            DrawAutomaticSection();
            EditorGUILayout.Space(18);
            DrawManualSection();
            EditorGUILayout.Space(18);
            DrawImpostorSection();
            EditorGUILayout.Space(18);
            DrawOutputSection();
            EditorGUILayout.EndScrollView();
        }

        private void DrawAutomaticSection()
        {
            EditorGUILayout.LabelField("1. Automatic Generation", EditorStyles.boldLabel);
            source = EditorGUILayout.ObjectField(new GUIContent("Source Model", "GameObject, prefab, FBX/OBJ o Mesh"),
                source, typeof(Object), true);
            styleProfile = (VoxelStyleProfile)EditorGUILayout.ObjectField(
                new GUIContent("Voxel Profile", "Unidad física, LODs, chunks y transiciones compartidos"),
                styleProfile, typeof(VoxelStyleProfile), false);

            if (styleProfile == null)
            {
                EditorGUILayout.HelpBox(
                    "Crea o asigna un perfil para generar modelos con vóxeles y LODs consistentes.",
                    MessageType.Warning);
                if (GUILayout.Button("Create Default Voxel Profile")) CreateDefaultProfile();
            }
            else if (!styleProfile.TryValidate(out string profileError))
            {
                EditorGUILayout.HelpBox(profileError, MessageType.Error);
            }
            else
            {
                string levels = string.Join(", ", Enumerable.Range(0, styleProfile.LodCount)
                    .Select(i => $"LOD{i}=x{styleProfile.GetLodMultiplier(i)} " +
                                 $"({styleProfile.BaseVoxelSize * styleProfile.GetLodMultiplier(i):0.###} m)"));
                EditorGUILayout.HelpBox(
                    $"Base voxel size: {styleProfile.BaseVoxelSize:0.###} m · Chunk: {styleProfile.ChunkCellSize} cells\n{levels}",
                    MessageType.Info);
            }

            DrawColorSettings();
            generateLod0Only = EditorGUILayout.Popup(new GUIContent("Generate Levels",
                "LOD0 Only permite editar y asignar IDs antes de derivar los demás niveles. Se aplica a la conversión individual y por lotes; no cambia Maximum Allowed Base."),
                generateLod0Only ? 0 : 1, new[] { "LOD0 Only", "All Profile Levels" }) == 0;
            VoxelBridgeFolderPicker.Draw("Family Folder", ref exportFolder);
            DrawIndividualSafetyOptions();
            bool individualImpostorReady = DrawIndividualImpostorOptions();
            GameObject individualSceneSource = source as GameObject;
            bool canPlaceIndividual = VoxelLodBatchScenePlacement.CanPlace(individualSceneSource);
            using (new EditorGUI.DisabledScope(!canPlaceIndividual))
                individualPlaceInScene = EditorGUILayout.Toggle(
                    new GUIContent("Place Result in Scene",
                        "Instancia el prefab voxel junto al GameObject fuente y conserva su Transform, layer, tag y flags Static."),
                    individualPlaceInScene);
            if (individualPlaceInScene && canPlaceIndividual)
            {
                EditorGUI.indentLevel++;
                individualDisableOriginalObject = EditorGUILayout.Toggle(
                    new GUIContent("Disable Original Object",
                        "Desactiva el GameObject fuente solamente después de crear correctamente la instancia voxel. La operación admite Undo."),
                    individualDisableOriginalObject);
                EditorGUI.indentLevel--;
            }
            else if (individualPlaceInScene && source != null && !canPlaceIndividual)
            {
                EditorGUILayout.HelpBox(
                    "La colocación requiere un GameObject de una escena cargada. Los assets del Project solo se convierten.",
                    MessageType.None);
            }
            bool canGenerate = source != null && VoxelBridgeSourceSelection.IsSupported(source) &&
                               styleProfile != null && styleProfile.TryValidate(out _) &&
                               VoxelLodPipeline.IsAssetFolder(exportFolder) &&
                               individualImpostorReady;
            using (new EditorGUI.DisabledScope(!canGenerate))
            {
                string label = individualGenerateImpostor
                    ? "Generate .vox Family + LOD Prefab + Impostor"
                    : conversionProfile != null ? "Generate .vox + Production LOD Prefab" : "Generate .vox + RGB Preview Prefab";
                if (GUILayout.Button(label, GUILayout.Height(38)))
                    GenerateAutomaticLods();
            }
            if (source != null && !VoxelBridgeSourceSelection.IsSupported(source))
                EditorGUILayout.HelpBox("Selecciona un GameObject, prefab, FBX/OBJ o Mesh.", MessageType.Warning);

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Batch Generation", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "Convierte cada hijo directo del padre en una familia independiente. Cada familia incluye las mallas de sus descendientes según la opción de objetos desactivados. Los hijos sin mallas utilizables se omiten.",
                MessageType.Info);
            batchParent = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Parent Object", "Objeto de escena o prefab cuyos hijos directos se procesarán por separado"),
                batchParent, typeof(GameObject), true);
            batchIgnoreInactiveObjects = EditorGUILayout.Toggle(
                new GUIContent("Ignore Inactive Objects",
                    "Omite hijos directos desactivados y mallas situadas bajo descendientes desactivados. El estado del padre del lote no afecta esta evaluación."),
                batchIgnoreInactiveObjects);
            batchReusePrefabSources = EditorGUILayout.Toggle(
                new GUIContent("Reuse Source Prefab",
                    "Convierte una sola vez las instancias sin overrides que procedan del mismo prefab y reutiliza esa familia."),
                batchReusePrefabSources);
            batchModifiedPrefabHandling = (VoxelPrefabOverrideHandling)EditorGUILayout.Popup(
                new GUIContent("Modified Instances",
                    "Decide qué hacer cuando una instancia tiene overrides distintos de posición, rotación o escala del root."),
                (int)batchModifiedPrefabHandling,
                new[]
                {
                    new GUIContent("Use Source Prefab", "Descarta los overrides visuales para la conversión y usa la familia del prefab fuente."),
                    new GUIContent("Convert as Separate Source", "Voxeliza la jerarquía modificada y crea una familia exclusiva para esta instancia."),
                    new GUIContent("Ignore Instance", "No convierte ni coloca esta instancia modificada.")
                });
            EditorGUILayout.HelpBox(GetBatchOverrideDescription(batchModifiedPrefabHandling), MessageType.None);

            VoxelLodBatchOptions previewOptions = CreateBatchOptions();
            VoxelLodBatchSourcePlan[] batchPlans =
                VoxelLodPipeline.GetAutomaticBatchPlans(batchParent, previewOptions);
            string preflightSignature = VoxelLodBatchAnalyzer.CreateSignature(
                batchPlans, styleProfile, CreateBatchLodOptions(), previewOptions);
            if (batchPreflight != null && batchPreflight.Signature != preflightSignature)
                batchPreflight = null;
            int directChildCount = batchParent != null ? batchParent.transform.childCount : 0;
            if (batchParent != null)
            {
                int inactiveDirectChildCount = batchIgnoreInactiveObjects
                    ? Enumerable.Range(0, directChildCount).Count(index =>
                        !batchParent.transform.GetChild(index).gameObject.activeSelf)
                    : 0;
                int skippedCount = Mathf.Max(
                    0, directChildCount - batchPlans.Length - inactiveDirectChildCount);
                int ignoredCount = batchPlans.Count(plan => plan.Ignored);
                int conversionCount = batchPlans.Where(plan => !plan.Ignored)
                    .Select(plan => plan.ReuseKey)
                    .Distinct()
                    .Count();
                int reuseCount = batchPlans.Length - ignoredCount - conversionCount;
                int modifiedCount = batchPlans.Count(plan => plan.HasPrefabOverrides);
                EditorGUILayout.HelpBox(
                    $"{batchPlans.Length} mesh object(s) · {conversionCount} conversion(s) · " +
                    $"{reuseCount} reused · {modifiedCount} modified instance(s) · " +
                    $"{ignoredCount} ignored · {inactiveDirectChildCount} inactive skipped · " +
                    $"{skippedCount} without active meshes.",
                    batchPlans.Any(plan => !plan.Ignored) ? MessageType.None : MessageType.Warning);

                batchShowPlanDetails = EditorGUILayout.Foldout(
                    batchShowPlanDetails, "Show Conversion / Reuse / Skip Plan", true);
                if (batchShowPlanDetails)
                {
                    var seen = new HashSet<Object>();
                    int visibleCount = Mathf.Min(100, batchPlans.Length);
                    EditorGUI.indentLevel++;
                    for (int index = 0; index < visibleCount; index++)
                    {
                        VoxelLodBatchSourcePlan plan = batchPlans[index];
                        string action;
                        if (plan.Ignored)
                        {
                            action = "SKIP · instance with overrides";
                        }
                        else if (!seen.Add(plan.ReuseKey))
                        {
                            action = $"REUSE · {plan.ConversionSource.name}";
                        }
                        else if (plan.UsesPrefabSource && plan.HasPrefabOverrides)
                        {
                            action = $"CONVERT PREFAB · {plan.ConversionSource.name} · ignore overrides";
                        }
                        else if (plan.UsesPrefabSource)
                        {
                            string sourcePath = AssetDatabase.GetAssetPath(plan.ConversionSource);
                            action = $"CONVERT PREFAB · {plan.ConversionSource.name}" +
                                     (string.IsNullOrEmpty(sourcePath) ? string.Empty : $" · {sourcePath}");
                        }
                        else if (plan.HasPrefabOverrides)
                        {
                            action = "CONVERT SEPARATELY · modified instance";
                        }
                        else
                        {
                            action = "CONVERT · no reusable prefab source";
                        }

                        EditorGUILayout.LabelField(
                            $"{index + 1}. {plan.Source.name}  →  {action}",
                            EditorStyles.wordWrappedMiniLabel);
                    }
                    if (batchPlans.Length > visibleCount)
                        EditorGUILayout.LabelField(
                            $"… y {batchPlans.Length - visibleCount} elemento(s) más. " +
                            "El resumen superior cuenta el lote completo.",
                            EditorStyles.wordWrappedMiniLabel);
                    EditorGUI.indentLevel--;
                }
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Memory Limits", EditorStyles.miniBoldLabel);
            batchMemoryBudgetMb = Mathf.Clamp(EditorGUILayout.IntField(
                new GUIContent("Per-Model Budget (MiB)",
                    "Límite estimado de memoria temporal para una fuente. El valor debe ajustarse según la memoria disponible y la carga del Editor."),
                batchMemoryBudgetMb), 256, 8192);
            batchAdaptInitialVoxelSize = EditorGUILayout.Toggle(
                new GUIContent("Adapt Resolution Automatically",
                    "Prueba tamaños voxel equivalentes a los LOD del perfil y usa el menor que cumple el límite de rejilla y el presupuesto de memoria."),
                batchAdaptInitialVoxelSize);
            batchMaximumImportedVoxelCount = Mathf.Clamp(EditorGUILayout.IntField(
                new GUIContent("Maximum Imported Voxels",
                    "Cantidad máxima de vóxeles ocupados que Voxel Importer puede recibir en el LOD0. La comprobación se realiza antes de escribir o importar archivos."),
                batchMaximumImportedVoxelCount), 100_000, 50_000_000);
            if (batchAdaptInitialVoxelSize && styleProfile != null && styleProfile.LodCount > 0)
            {
                batchMaximumInitialLodIndex = Mathf.Clamp(
                    batchMaximumInitialLodIndex, 0, styleProfile.LodCount - 1);
                string[] initialLodLabels = Enumerable.Range(0, styleProfile.LodCount)
                    .Select(index =>
                        $"LOD{index} · ×{styleProfile.GetLodMultiplier(index)} · " +
                        $"{styleProfile.BaseVoxelSize * styleProfile.GetLodMultiplier(index):0.###} m")
                    .ToArray();
                batchMaximumInitialLodIndex = EditorGUILayout.Popup(
                    new GUIContent("Maximum Allowed Base",
                        "Último tamaño base que puede seleccionar la adaptación automática."),
                    batchMaximumInitialLodIndex, initialLodLabels);
                EditorGUILayout.HelpBox(
                    "Cada fuente usa la base más fina que cumple los límites. Durante la generación, el LOD0 también se compara con el máximo de vóxeles importados y se repite con la siguiente base cuando sea necesario. Las fuentes que todavía excedan algún límite con la base máxima se omiten.",
                    MessageType.None);
            }
            using (new EditorGUI.DisabledScope(batchAdaptInitialVoxelSize))
            {
                bool skipOverBudget = EditorGUILayout.Toggle(
                    new GUIContent("Skip Over-Budget Models",
                        batchAdaptInitialVoxelSize
                            ? "La adaptación automática siempre omite las fuentes que no caben con la base máxima."
                            : "Evita iniciar fuentes cuyo pico estimado supera el límite. Se registran como error y el resto del lote continúa."),
                    batchAdaptInitialVoxelSize || batchSkipOverMemoryBudget);
                if (!batchAdaptInitialVoxelSize) batchSkipOverMemoryBudget = skipOverBudget;
            }
            batchResumeInterrupted = EditorGUILayout.Toggle(
                new GUIContent("Resume Interrupted Batch",
                    "Recupera familias completas registradas en Library y continúa con las fuentes pendientes. El checkpoint se descarta al terminar el lote."),
                batchResumeInterrupted);
            batchCleanupInterval = EditorGUILayout.IntSlider(
                new GUIContent("Clean Up Every N Families",
                    "Libera assets, texturas temporales y memoria administrada después de este número de fuentes únicas. 1 es el valor más seguro; 2–4 puede ser algo más rápido."),
                batchCleanupInterval, 1, 25);
            EditorGUILayout.HelpBox(
                "Cada familia terminada se registra fuera de Assets. Si Unity se cierra, la siguiente ejecución con la misma fuente y configuración reutiliza esas familias. Las carpetas parciales marcadas por la herramienta se eliminan antes de continuar.",
                MessageType.None);
            if (GUILayout.Button("Find and Clean Incomplete Outputs"))
            {
                int cleaned = VoxelLodBatchRecovery.CleanupIncompleteFamilies(exportFolder);
                status = cleaned > 0
                    ? $"Removed {cleaned} incomplete family output(s) marked by Voxel Bridge."
                    : "No incomplete family outputs marked by Voxel Bridge were found.";
            }
            using (new EditorGUI.DisabledScope(batchPlans.All(plan => plan.Ignored) ||
                                               styleProfile == null ||
                                               !styleProfile.TryValidate(out _)))
            {
                if (GUILayout.Button("Analyze Batch Memory")) AnalyzeAutomaticBatch();
            }
            if (batchPreflight != null)
            {
                MessageType analysisType = batchPreflight.InvalidCount > 0 ||
                                           batchPreflight.OverBudgetCount > 0
                    ? MessageType.Warning
                    : MessageType.Info;
                EditorGUILayout.HelpBox(
                    $"{batchPreflight.UniqueConversionCount} unique source(s) · " +
                    $"estimated peak {VoxelLodBatchAnalyzer.FormatBytes(batchPreflight.EstimatedPeakBytes)} · " +
                    $"{batchPreflight.TotalDenseCells:N0} cells across all LODs · " +
                    $"{batchPreflight.AdaptedCount} adapted · " +
                    $"{batchPreflight.OverBudgetCount} over budget · " +
                    $"{batchPreflight.InvalidCount} invalid.", analysisType);
                foreach (VoxelLodBatchSourceEstimate estimate in batchPreflight.Sources
                             .Where(item => item.WasAdapted ||
                                            item.Risk is VoxelLodBatchMemoryRisk.OverBudget or
                                                VoxelLodBatchMemoryRisk.Invalid)
                             .Take(8))
                    EditorGUILayout.LabelField(
                        $"• {estimate.SourceName}: {FormatBatchEstimate(estimate)}",
                        EditorStyles.wordWrappedMiniLabel);
            }

            bool batchImpostorsReady = DrawBatchImpostorOptions();

            bool canPlaceBatch = VoxelLodBatchScenePlacement.CanPlace(batchParent);
            using (new EditorGUI.DisabledScope(!canPlaceBatch))
                batchPlaceInScene = EditorGUILayout.Toggle(
                    new GUIContent("Place Result in Scene",
                        "Crea una raíz voxel paralela e instancia cada prefab convertido con el Transform del hijo original."),
                    batchPlaceInScene);
            if (batchParent != null && !canPlaceBatch)
                EditorGUILayout.HelpBox(
                    "Para colocar el resultado, asigna un objeto padre perteneciente a una escena cargada; un prefab del Project solo puede convertirse.",
                    MessageType.None);
            if (batchPlaceInScene && canPlaceBatch)
            {
                EditorGUI.indentLevel++;
                batchDisableOriginalRoot = EditorGUILayout.Toggle(
                    new GUIContent("Disable Original Root",
                        "Solo se desactiva cuando todos los hijos directos tienen malla y las conversiones terminan sin fallos, elementos ignorados ni cancelación."),
                    batchDisableOriginalRoot);
                EditorGUI.indentLevel--;
                EditorGUILayout.HelpBox(
                    "La raíz voxel conserva Transform, layer, estado activo y flags Static, pero no copia scripts, colliders ni otros componentes. Usa el reemplazo automático solo con una raíz visual.",
                    MessageType.Warning);
                int inactiveDirectChildren = batchIgnoreInactiveObjects && batchParent != null
                    ? Enumerable.Range(0, directChildCount).Count(index =>
                        !batchParent.transform.GetChild(index).gameObject.activeSelf)
                    : 0;
                if (directChildCount > batchPlans.Length + inactiveDirectChildren)
                    EditorGUILayout.HelpBox(
                        "Hay hijos directos sin malla. Se creará la raíz voxel desactivada y no se desactivará la original para evitar perder objetos o componentes no convertidos.",
                        MessageType.Warning);
            }

            bool canGenerateBatch = batchPlans.Any(plan => !plan.Ignored) && styleProfile != null &&
                                    styleProfile.TryValidate(out _) &&
                                    VoxelLodPipeline.IsAssetFolder(exportFolder) &&
                                    batchImpostorsReady;
            using (new EditorGUI.DisabledScope(!canGenerateBatch))
            {
                string label = batchGenerateImpostors
                    ? "Generate Families and Impostors for All Children"
                    : conversionProfile != null ? "Generate Production Families for All Children" : "Generate RGB Preview Families for All Children";
                if (GUILayout.Button(label, GUILayout.Height(38)))
                    GenerateAutomaticBatch();
            }
        }

        private void DrawIndividualSafetyOptions()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(
                "Large Model Limits", EditorStyles.miniBoldLabel);
            individualMemoryBudgetMb = Mathf.Clamp(EditorGUILayout.IntField(
                new GUIContent("Temporary Memory Budget (MiB)",
                    "Límite estimado de memoria temporal para esta conversión. La generación no comienza si ninguna unidad permitida cabe en el presupuesto."),
                individualMemoryBudgetMb), 256, 8192);
            individualAdaptInitialVoxelSize = EditorGUILayout.Toggle(
                new GUIContent("Adapt Voxel Size Automatically",
                    "Prueba las unidades equivalentes a los LOD del perfil y usa la más fina que cumple los límites de rejilla y memoria."),
                individualAdaptInitialVoxelSize);
            individualMaximumImportedVoxelCount = Mathf.Clamp(EditorGUILayout.IntField(
                new GUIContent("Maximum Imported Voxels",
                    "Cantidad máxima de vóxeles ocupados que Voxel Importer puede recibir en el LOD0. Si se supera, la generación repite el modelo con la siguiente unidad permitida."),
                individualMaximumImportedVoxelCount), 100_000, 50_000_000);
            if (individualAdaptInitialVoxelSize && styleProfile != null &&
                styleProfile.LodCount > 0)
            {
                individualMaximumInitialLodIndex = Mathf.Clamp(
                    individualMaximumInitialLodIndex, 0, styleProfile.LodCount - 1);
                string[] initialLodLabels = Enumerable.Range(0, styleProfile.LodCount)
                    .Select(index =>
                        $"LOD{index} · ×{styleProfile.GetLodMultiplier(index)} · " +
                        $"{styleProfile.BaseVoxelSize * styleProfile.GetLodMultiplier(index):0.###} m")
                    .ToArray();
                individualMaximumInitialLodIndex = EditorGUILayout.Popup(
                    new GUIContent("Maximum Allowed Base",
                        "Última unidad física que puede seleccionar la adaptación automática."),
                    individualMaximumInitialLodIndex, initialLodLabels);
            }

            EditorGUILayout.HelpBox(
                individualAdaptInitialVoxelSize
                    ? "Antes de reservar memoria se selecciona la base más fina compatible. Los chunks siguen creándose automáticamente después de voxelizar."
                    : "La conversión mantiene la unidad LOD0 y se cancela antes de voxelizar si la rejilla o la memoria exceden los límites.",
                MessageType.None);
        }

        private bool DrawBatchImpostorOptions()
        {
            bool ready = DrawAutomaticImpostorOptions(
                ref batchGenerateImpostors,
                "Batch Impostors",
                "Hornea un impostor después de cada familia voxel única y lo añade como último nivel del LODGroup. Las instancias reutilizadas comparten el mismo impostor.",
                "El horneado se realiza en serie y una sola vez por familia. Un error de Amplify conserva la familia voxel y permite continuar con las restantes.",
                false);
            if (!batchGenerateImpostors || impostorProfile == null) return ready;

            batchSelectImpostorQualityBySize = EditorGUILayout.Toggle(
                new GUIContent("Select Profile by Size",
                    "Selecciona Low, Medium o Architecture a partir del tamaño físico de cada familia. El perfil High permanece como selección manual porque el tamaño no permite saber si un objeto puede observarse desde abajo."),
                batchSelectImpostorQualityBySize);

            bool policyValid = true;
            if (batchSelectImpostorQualityBySize)
            {
                policyValid = impostorProfile.TryValidateAutomaticPolicy(out string policyError);
                if (!policyValid)
                    EditorGUILayout.HelpBox(policyError, MessageType.Error);
                else
                    EditorGUILayout.HelpBox(
                        $"Política: menos de {impostorProfile.MinimumImpostorSize:0.##} m sin impostor; " +
                        $"hasta {impostorProfile.MediumImpostorSize:0.##} m perfil Low; " +
                        $"hasta {impostorProfile.ArchitectureImpostorSize:0.##} m perfil Medium; " +
                        "a partir de ese tamaño, Architecture. El presupuesto prioriza la cobertura de las familias mayores y mejora sus perfiles mientras haya memoria disponible.",
                        MessageType.None);

                EditorGUILayout.BeginHorizontal();
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.ObjectField("Shared Settings", impostorProfile,
                        typeof(VoxelImpostorProfile), false);
                if (GUILayout.Button("Edit Settings", GUILayout.Width(100)))
                    SelectAndPing(impostorProfile);
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                DrawImpostorQualitySelector();
                policyValid = ValidateSelectedImpostorQuality();
            }

            batchImpostorAtlasBudgetMb = Mathf.Max(64, EditorGUILayout.IntField(
                new GUIContent("Total Atlas Budget (MiB)",
                    "Límite estimado de memoria importada para los cinco mapas y sus mipmaps por cada familia única. En modo automático se asigna primero el perfil de menor coste a cada familia elegible y después se mejora su calidad. En modo fijo se omiten las familias que no caben."),
                batchImpostorAtlasBudgetMb));
            if (!QualitySettings.streamingMipmapsActive)
                EditorGUILayout.HelpBox(
                    "El nivel de calidad activo tiene deshabilitado Mipmap Streaming. Las texturas generadas quedarán preparadas para streaming, pero Unity no limitará su residencia hasta activarlo en Quality Settings.",
                    MessageType.Warning);

            return ready && policyValid;
        }

        private bool DrawIndividualImpostorOptions()
        {
            return DrawAutomaticImpostorOptions(
                ref individualGenerateImpostor,
                "Final Impostor",
                "Hornea un impostor con Amplify después de generar la familia voxel y lo añade como último nivel del LODGroup.",
                "El impostor se hornea antes de colocar el prefab en la escena. Si el horneado falla, el objeto original permanece activo.");
        }

        private bool DrawAutomaticImpostorOptions(
            ref bool generateImpostor, string heading, string toggleTooltip, string footer,
            bool drawQualitySelector = true)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(heading, EditorStyles.miniBoldLabel);
            generateImpostor = EditorGUILayout.Toggle(
                new GUIContent("Generate Final Impostor", toggleTooltip), generateImpostor);
            if (!generateImpostor) return true;
            if (conversionProfile != null)
            {
                EditorGUILayout.HelpBox("El horneado de impostores semánticos requiere integrar las LUT y los IDs en el shader de captura. Desactivar Generate Final Impostor para convertir con materiales semánticos.", MessageType.Warning);
                return false;
            }

            AmplifyImpostorCompatibility compatibility =
                AmplifyImpostorIntegration.GetCompatibility();
            MessageType compatibilityMessage = compatibility.Status switch
            {
                AmplifyImpostorCompatibilityStatus.Ready => MessageType.Info,
                AmplifyImpostorCompatibilityStatus.NotInstalled => MessageType.Warning,
                _ => MessageType.Error
            };
            EditorGUILayout.HelpBox(compatibility.Message, compatibilityMessage);
            DrawAmplifyPipelinePatchAction();

            if (impostorProfile == null)
            {
                EditorGUILayout.HelpBox(
                    "Asigna o crea la configuración central de perfiles de impostor.",
                    MessageType.Error);
                if (GUILayout.Button("Create Impostor Profile Settings"))
                    CreateDefaultImpostorProfile();
                return false;
            }

            if (drawQualitySelector)
            {
                DrawImpostorQualitySelector();
                if (!ValidateSelectedImpostorQuality()) return false;
            }

            EditorGUILayout.HelpBox(footer, MessageType.None);
            return compatibility.CanBake;
        }

        private bool ValidateSelectedImpostorQuality()
        {
            if (styleProfile == null || styleProfile.LodCount == 0) return true;
            if (!styleProfile.TryValidate(out string styleError))
            {
                EditorGUILayout.HelpBox(styleError, MessageType.Error);
                return false;
            }
            if (impostorProfile.TryValidate(impostorQuality,
                    styleProfile.GetMinimumLodScreenHeight(styleProfile.LodCount - 1),
                    out string profileError))
                return true;

            EditorGUILayout.HelpBox(profileError, MessageType.Error);
            return false;
        }

        private void DrawColorSettings()
        {
            conversionProfile = (VoxelConversionProfile)EditorGUILayout.ObjectField(
                new GUIContent("Conversion Profile", "Reglas por material, ColorID y SurfaceID durante la voxelización. Crear desde Assets > Create > Voxel Bridge > Conversion Profile."),
                conversionProfile, typeof(VoxelConversionProfile), false);
            if (conversionProfile != null)
                EditorGUILayout.HelpBox("Salida: prefab semántico de producción, con edición y reconstrucción por LOD. Place Result in Scene utiliza este prefab. Los .vox se conservan como fuentes editables. Keep Original conserva geometría estática con sus materiales originales. Las LUT deben estar actualizadas; máximo 8 millones de celdas y 500.000 quads por nivel.", MessageType.Info);
            else
                EditorGUILayout.HelpBox("Sin Conversion Profile, la salida es un prefab de previsualización RGB de Voxel Importer. No utiliza ColorID, SurfaceID ni emisión semántica.", MessageType.Info);
            colorMode = (VoxelColorMode)EditorGUILayout.Popup("Color Source", (int)colorMode,
                new[] { "Material + Texture", "Material Only", "Single Color" });
            if (colorMode == VoxelColorMode.SingleColor)
                singleColor = EditorGUILayout.ColorField("Color", singleColor);
            if (colorMode == VoxelColorMode.MaterialAndTexture)
                alphaCutoff = EditorGUILayout.Slider(
                    new GUIContent("Alpha Cutoff", "Descarta texeles transparentes"), alphaCutoff, 0f, 1f);
        }

        private void DrawManualSection()
        {
            EditorGUILayout.LabelField("2. Manual LOD", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Parte del .vox editado anterior: duplícalo para una simplificación libre o redúcelo a la rejilla física del nuevo nivel.",
                MessageType.Info);
            manualParentVox = EditorGUILayout.ObjectField("Parent LOD (.vox)", manualParentVox,
                typeof(Object), false);

            int firstTargetLod = 1;
            if (VoxelImporterIntegration.IsVoxAsset(manualParentVox) &&
                VoxelImporterIntegration.TryLoadMetadata(AssetDatabase.GetAssetPath(manualParentVox),
                    out VoxelBridgeMetadata parentMetadata, out _))
                firstTargetLod = parentMetadata.lodIndex + 1;
            bool hasManualTarget = styleProfile != null && firstTargetLod < styleProfile.LodCount;
            if (hasManualTarget)
            {
                string[] labels = Enumerable.Range(firstTargetLod, styleProfile.LodCount - firstTargetLod)
                    .Select(i => $"LOD {i} · x{styleProfile.GetLodMultiplier(i)}")
                    .ToArray();
                int selected = Mathf.Clamp(manualTargetLod - firstTargetLod, 0, labels.Length - 1);
                manualTargetLod = EditorGUILayout.Popup("Target LOD", selected, labels) + firstTargetLod;
            }
            else if (VoxelImporterIntegration.IsVoxAsset(manualParentVox) && styleProfile != null)
            {
                EditorGUILayout.HelpBox("El LOD padre ya es el último nivel del perfil.", MessageType.Warning);
            }

            manualGenerationMode = (VoxelLodGenerationMode)EditorGUILayout.Popup(
                "Starting Point", manualGenerationMode == VoxelLodGenerationMode.DuplicateParent ? 0 : 1,
                new[] { "Duplicate Previous (Free Editing)", "Reduce to Target Resolution" }) == 0
                ? VoxelLodGenerationMode.DuplicateParent
                : VoxelLodGenerationMode.ReduceParent;
            bool canGenerate = hasManualTarget && styleProfile.TryValidate(out _) &&
                               VoxelImporterIntegration.IsVoxAsset(manualParentVox);
            using (new EditorGUI.DisabledScope(!canGenerate))
            {
                if (GUILayout.Button("Create Manual LOD and Update Prefab", GUILayout.Height(32)))
                    GenerateManualLod();
            }

            EditorGUILayout.Space(8);
            lodSetManifest = (TextAsset)EditorGUILayout.ObjectField(".voxset Manifest", lodSetManifest,
                typeof(TextAsset), false);
            using (new EditorGUI.DisabledScope(lodSetManifest == null))
            {
                if (GUILayout.Button("Rebuild Prefab from Manifest")) RebuildLodPrefab();
            }
            DrawFamilyLodEditor();
        }

        private void DrawFamilyLodEditor()
        {
            if (lodSetManifest == null || !VoxelLodPipeline.TryFindManifestForAsset(
                    AssetDatabase.GetAssetPath(lodSetManifest), out _, out VoxelLodSetManifest manifest))
                return;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Edit Family Levels", EditorStyles.boldLabel);
            foreach (VoxelLodEntry entry in (manifest.lods ?? Array.Empty<VoxelLodEntry>())
                         .OrderBy(value => value.lodIndex))
            {
                Object voxAsset = AssetDatabase.LoadMainAssetAtPath(entry.voxAssetPath);
                EditorGUILayout.BeginHorizontal();
                float voxelSize = manifest.baseVoxelSize * Mathf.Max(1, entry.multiplier);
                EditorGUILayout.LabelField(
                    $"LOD {entry.lodIndex} · x{entry.multiplier} · {voxelSize:0.###} m",
                    GUILayout.MinWidth(155));
                using (new EditorGUI.DisabledScope(voxAsset == null))
                {
                    if (GUILayout.Button("Select", GUILayout.Width(80))) SelectAndPing(voxAsset);
                    if (GUILayout.Button("Edit", GUILayout.Width(58))) MagicaVoxelLauncher.OpenAsset(voxAsset);
                    if (GUILayout.Button("Use as Parent", GUILayout.Width(105)))
                        UseAsManualParent(voxAsset, entry.lodIndex, manifest);
                }
                EditorGUILayout.EndHorizontal();
                if (voxAsset == null)
                    EditorGUILayout.HelpBox($"No se encontró {entry.voxAssetPath}", MessageType.Warning);
            }
        }

        private void UseAsManualParent(
            Object voxAsset, int lodIndex, VoxelLodSetManifest manifest)
        {
            manualParentVox = voxAsset;
            manualTargetLod = lodIndex + 1;
            if (!string.IsNullOrEmpty(manifest.profileAssetPath))
            {
                VoxelStyleProfile familyProfile =
                    AssetDatabase.LoadAssetAtPath<VoxelStyleProfile>(manifest.profileAssetPath);
                if (familyProfile != null) styleProfile = familyProfile;
            }
            status = $"LOD {lodIndex} selected as the source for the next manual LOD.";
        }

        private void DrawOutputSection()
        {
            EditorGUILayout.LabelField("4. Results", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Los niveles editables siguen siendo archivos .vox. El prefab los agrupa para la escena y, si se generó, añade el impostor de Amplify como último LOD.",
                MessageType.Info);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("Generated LOD (.vox)", lastVoxAsset, typeof(Object), false);
                EditorGUILayout.ObjectField("Scene Prefab", lastPrefabAsset, typeof(Object), false);
                EditorGUILayout.ObjectField("Amplify Impostor", lastImpostorAsset, typeof(Object), false);
            }
            if (!string.IsNullOrEmpty(lastVoxAssetPath))
            {
                EditorGUILayout.LabelField("File Path", EditorStyles.miniBoldLabel);
                EditorGUILayout.SelectableLabel(lastVoxAssetPath, EditorStyles.textField,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
            if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);

            using (new EditorGUI.DisabledScope(lastVoxAsset == null))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Select .vox")) SelectAndPing(lastVoxAsset);
                if (GUILayout.Button("Reveal .vox File"))
                    EditorUtility.RevealInFinder(VoxelLodPipeline.AssetPathToAbsolute(lastVoxAssetPath));
                if (GUILayout.Button("Open in MagicaVoxel")) MagicaVoxelLauncher.OpenAsset(lastVoxAsset);
                EditorGUILayout.EndHorizontal();
            }
            using (new EditorGUI.DisabledScope(lastPrefabAsset == null))
            {
                if (GUILayout.Button("Select Scene Prefab")) SelectAndPing(lastPrefabAsset);
            }
            using (new EditorGUI.DisabledScope(lastImpostorAsset == null))
            {
                if (GUILayout.Button("Select Impostor Asset")) SelectAndPing(lastImpostorAsset);
            }
        }

        private void DrawImpostorSection()
        {
            EditorGUILayout.LabelField("3. Final Impostor", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Hornea LOD0 —incluidos sus chunks— y añade el resultado como último nivel sin reemplazar los LOD voxel.",
                MessageType.Info);

            AmplifyImpostorCompatibility compatibility =
                AmplifyImpostorIntegration.GetCompatibility();
            MessageType compatibilityMessage = compatibility.Status switch
            {
                AmplifyImpostorCompatibilityStatus.Ready => MessageType.Info,
                AmplifyImpostorCompatibilityStatus.NotInstalled => MessageType.Warning,
                _ => MessageType.Error
            };
            EditorGUILayout.HelpBox(compatibility.Message, compatibilityMessage);
            DrawAmplifyPipelinePatchAction();

            if (impostorProfile == null)
            {
                EditorGUILayout.HelpBox(
                    "La configuración central contiene los perfiles de uso compartidos por todas las familias.",
                    MessageType.Warning);
                if (GUILayout.Button("Create Impostor Profile Settings"))
                    CreateDefaultImpostorProfile();
            }
            else
            {
                DrawImpostorQualitySelector();
            }

            VoxelLodSetManifest manifest = null;
            bool hasManifest = lodSetManifest != null && VoxelLodPipeline.TryReadManifest(
                AssetDatabase.GetAssetPath(lodSetManifest), out manifest);
            if (!hasManifest)
                EditorGUILayout.HelpBox(
                    "Carga una familia desde su prefab, .vox o manifiesto para generar el impostor.",
                    MessageType.Warning);
            if (hasManifest && manifest.productionMeshes)
                EditorGUILayout.HelpBox("El horneado de esta familia semántica requiere un shader de captura compatible con ColorID, SurfaceID y las LUT. Esta operación no está disponible para materiales semánticos.", MessageType.Info);

            float lastVoxelTransition = 0f;
            bool hasTransition = hasManifest &&
                                 AmplifyImpostorIntegration.TryGetLastVoxelTransition(
                                     manifest, out lastVoxelTransition);
            string error = null;
            bool validProfile = impostorProfile != null && hasTransition &&
                                impostorProfile.TryValidate(
                                    impostorQuality, lastVoxelTransition, out error);
            if (impostorProfile != null && hasTransition)
            {
                VoxelImpostorSettings settings = impostorProfile.GetSettings(impostorQuality);
                if (!validProfile)
                    EditorGUILayout.HelpBox(error, MessageType.Error);
                else
                {
                    float finalVoxelTransition =
                        AmplifyImpostorIntegration.GetExpandedLastVoxelTransition(
                            lastVoxelTransition, settings.CullScreenHeight,
                            manifest.lods?.Length ?? 0);
                    EditorGUILayout.HelpBox(
                        $"{settings.ImpostorType} · atlas {settings.TextureResolution} · " +
                        $"{settings.Frames}x{settings.Frames} views · padding {settings.PixelPadding}px\n" +
                        $"Final voxel LOD → impostor: {finalVoxelTransition:0.####} · " +
                        $"cull: {settings.CullScreenHeight:0.####} · Fade Mode: None",
                        MessageType.None);
                }
            }

            bool hasConfiguredImpostor = hasManifest &&
                                         AmplifyImpostorIntegration.HasConfiguredImpostor(manifest);
            Object currentImpostor = hasConfiguredImpostor
                ? AssetDatabase.LoadMainAssetAtPath(manifest.impostor.assetPath)
                : null;
            if (hasManifest && manifest.impostorDisabled)
                EditorGUILayout.HelpBox(
                    "La familia está excluida de la generación automática de impostores. Generar uno manualmente vuelve a habilitarla.",
                    MessageType.Info);
            if (hasConfiguredImpostor)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.ObjectField("Current Asset", currentImpostor, typeof(Object), false);
                using (new EditorGUI.DisabledScope(currentImpostor == null))
                    if (GUILayout.Button("Select", GUILayout.Width(80)))
                        SelectAndPing(currentImpostor);
                EditorGUILayout.EndHorizontal();
                if (currentImpostor == null)
                    EditorGUILayout.HelpBox(
                        "El manifiesto contiene un impostor, pero su asset no está disponible.",
                        MessageType.Warning);
            }

            bool canGenerate = compatibility.CanBake && hasManifest && !manifest.productionMeshes && validProfile;
            using (new EditorGUI.DisabledScope(!canGenerate))
            {
                if (GUILayout.Button("Generate or Update Final Impostor", GUILayout.Height(36)))
                    GenerateImpostor();
            }
            using (new EditorGUI.DisabledScope(!hasConfiguredImpostor))
            {
                if (GUILayout.Button(new GUIContent(
                        "Remove Family Impostor",
                        "Elimina el último nivel de impostor del prefab y excluye esta familia de la generación automática. Los atlas se conservan como caché sin referencias.")))
                    RemoveLoadedImpostor(manifest);
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Existing Families", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "Procesa las familias RGB de la carpeta de exportación cuyo manifiesto todavía no contiene un impostor válido. Las familias semánticas se excluyen hasta disponer de un shader de captura compatible. Utiliza el perfil seleccionado y no vuelve a voxelizar los modelos.",
                MessageType.None);
            bool canGeneratePending = compatibility.CanBake && impostorProfile != null &&
                                      VoxelLodPipeline.IsAssetFolder(exportFolder);
            using (new EditorGUI.DisabledScope(!canGeneratePending))
            {
                if (GUILayout.Button("Generate Pending Impostors in Folder"))
                    GeneratePendingImpostors();
            }
        }

        private static void DrawAmplifyPipelinePatchAction()
        {
            AmplifyPipelinePatchResult patchStatus =
                AmplifyImpostorPipelinePatcher.GetProjectStatus(out _);
            if (patchStatus != AmplifyPipelinePatchResult.Required) return;

            if (!GUILayout.Button("Apply Amplify Impostors Compatibility Patch")) return;
            if (!AmplifyImpostorPipelinePatcher.TryApplyToProject(
                    out AmplifyPipelinePatchResult result, out string message))
            {
                Debug.LogWarning(message);
                EditorUtility.DisplayDialog(
                    "Voxel Bridge - Patch Not Applied", message, "OK");
                return;
            }

            if (result == AmplifyPipelinePatchResult.Applied)
                AssetDatabase.ImportAsset(
                    AmplifyImpostorPipelinePatcher.SourceAssetPath,
                    ImportAssetOptions.ForceUpdate);
        }

        private void DrawImpostorQualitySelector()
        {
            VoxelImpostorQuality[] qualities =
            {
                VoxelImpostorQuality.Low,
                VoxelImpostorQuality.Medium,
                VoxelImpostorQuality.High,
                VoxelImpostorQuality.Architecture
            };
            string[] labels = qualities.Select(VoxelImpostorProfile.GetQualityName).ToArray();
            int selected = Mathf.Max(0, Array.IndexOf(qualities,
                VoxelImpostorProfile.NormalizeQuality(impostorQuality)));
            impostorQuality = qualities[EditorGUILayout.Popup(
                new GUIContent("Impostor Profile",
                    "Selecciona el conjunto de captura, atlas, silueta, transición y descarte que corresponde al uso de este modelo."),
                selected, labels)];

            EditorGUILayout.HelpBox(
                VoxelImpostorProfile.GetQualityDescription(impostorQuality), MessageType.None);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("Shared Settings", impostorProfile,
                    typeof(VoxelImpostorProfile), false);
            if (GUILayout.Button("Edit Settings", GUILayout.Width(100))) SelectAndPing(impostorProfile);
            EditorGUILayout.EndHorizontal();
        }

        private void CreateDefaultProfile()
        {
            const string settingsFolder = "Assets/VoxelBridgeSettings";
            const string profilePath = settingsFolder + "/VoxelStyleProfile.asset";
            VoxelLodPipeline.EnsureAssetFolder(settingsFolder);
            styleProfile = AssetDatabase.LoadAssetAtPath<VoxelStyleProfile>(profilePath);
            if (styleProfile == null)
            {
                styleProfile = CreateInstance<VoxelStyleProfile>();
                AssetDatabase.CreateAsset(styleProfile, profilePath);
                AssetDatabase.SaveAssets();
            }
            SelectAndPing(styleProfile);
        }

        private void CreateDefaultImpostorProfile()
        {
            const string settingsFolder = "Assets/VoxelBridgeSettings";
            VoxelLodPipeline.EnsureAssetFolder(settingsFolder);
            impostorProfile = AssetDatabase.LoadAssetAtPath<VoxelImpostorProfile>(
                DefaultImpostorProfilePath);
            if (impostorProfile == null)
            {
                impostorProfile = CreateInstance<VoxelImpostorProfile>();
                impostorProfile.EnsureInitialized();
                AssetDatabase.CreateAsset(impostorProfile, DefaultImpostorProfilePath);
                AssetDatabase.SaveAssets();
            }
            else if (impostorProfile.EnsureInitialized())
            {
                EditorUtility.SetDirty(impostorProfile);
                AssetDatabase.SaveAssets();
            }
            impostorQuality = VoxelImpostorQuality.Medium;
            SelectAndPing(impostorProfile);
        }

        private VoxelLodBuildOptions CreateLodOptions() => new()
        {
            ColorMode = colorMode,
            SingleColor = singleColor,
            AlphaCutoff = alphaCutoff,
            GenerateLod0Only = generateLod0Only,
            ConversionProfile = conversionProfile,
            ExportFolder = exportFolder
        };

        private VoxelLodBuildOptions CreateBatchLodOptions()
        {
            VoxelLodBuildOptions options = CreateLodOptions();
            options.IncludeInactiveObjects = !batchIgnoreInactiveObjects;
            return options;
        }

        private static string FormatBatchEstimate(VoxelLodBatchSourceEstimate estimate)
        {
            if (!estimate.IsValid) return estimate.Error;
            string voxelSize = estimate.LodPlans.Length > 0
                ? $" · {estimate.LodPlans[0].VoxelSize:0.###} m"
                : string.Empty;
            string budget = estimate.IsOverBudget ? " · over budget" : string.Empty;
            return $"base LOD{estimate.InitialLodIndex} · ×{estimate.InitialVoxelMultiplier}" +
                   $"{voxelSize} · {VoxelLodBatchAnalyzer.FormatBytes(estimate.EstimatedPeakBytes)}" +
                   budget;
        }

        private VoxelLodBatchOptions CreateIndividualSafetyOptions() => new()
        {
            MaximumEstimatedMemoryBytes = Mathf.Max(256, individualMemoryBudgetMb) *
                                          VoxelLodBatchAnalyzer.Mebibyte,
            SkipSourcesOverMemoryBudget = true,
            AdaptInitialVoxelSize = individualAdaptInitialVoxelSize,
            MaximumInitialLodIndex = individualMaximumInitialLodIndex,
            MaximumImportedVoxelCount = individualMaximumImportedVoxelCount,
            IgnoreInactiveObjects = false,
            EnableCheckpoint = false,
            ResumeInterruptedBatch = false
        };

        private VoxelLodBatchOptions CreateBatchOptions() => new()
        {
            ReusePrefabSources = batchReusePrefabSources,
            ModifiedPrefabHandling = batchModifiedPrefabHandling,
            MaximumEstimatedMemoryBytes = Mathf.Max(256, batchMemoryBudgetMb) *
                                          VoxelLodBatchAnalyzer.Mebibyte,
            SkipSourcesOverMemoryBudget = batchAdaptInitialVoxelSize ||
                                          batchSkipOverMemoryBudget,
            AdaptInitialVoxelSize = batchAdaptInitialVoxelSize,
            MaximumInitialLodIndex = batchMaximumInitialLodIndex,
            MaximumImportedVoxelCount = batchMaximumImportedVoxelCount,
            IgnoreInactiveObjects = batchIgnoreInactiveObjects,
            EnableCheckpoint = true,
            ResumeInterruptedBatch = batchResumeInterrupted,
            CleanupInterval = batchCleanupInterval,
            Preflight = batchPreflight
        };

        private void AnalyzeAutomaticBatch()
        {
            try
            {
                VoxelLodBatchOptions batchOptions = CreateBatchOptions();
                batchOptions.Preflight = null;
                VoxelLodBatchSourcePlan[] plans =
                    VoxelLodPipeline.GetAutomaticBatchPlans(batchParent, batchOptions);
                batchPreflight = VoxelLodBatchAnalyzer.Analyze(
                    plans, styleProfile, CreateBatchLodOptions(), batchOptions,
                    (progress, message) => EditorUtility.DisplayCancelableProgressBar(
                        "Voxel Bridge · Memory Analysis", message, progress));
                status = $"Analysis complete: {batchPreflight.UniqueConversionCount} unique source(s), " +
                         $"peak {VoxelLodBatchAnalyzer.FormatBytes(batchPreflight.EstimatedPeakBytes)}.";
            }
            catch (OperationCanceledException)
            {
                status = "Memory analysis cancelled.";
            }
            catch (Exception exception)
            {
                ShowException(exception);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void GenerateAutomaticLods()
        {
            try
            {
                VoxelLodBuildOptions lodOptions = CreateLodOptions();
                VoxelLodBatchOptions safetyOptions = CreateIndividualSafetyOptions();
                individualPreflight = VoxelLodBatchAnalyzer.AnalyzeSingle(
                    source, styleProfile, lodOptions, safetyOptions);
                if (!individualPreflight.IsValid)
                    throw new InvalidOperationException(
                        $"Cannot plan conversion for {source.name}. " +
                        individualPreflight.Error);
                if (individualPreflight.IsOverBudget)
                    throw new InvalidOperationException(
                        $"Converting {source.name} requires an estimated peak of " +
                        $"{VoxelLodBatchAnalyzer.FormatBytes(individualPreflight.EstimatedPeakBytes)}, " +
                        $"above the budget of " +
                        $"{VoxelLodBatchAnalyzer.FormatBytes(individualPreflight.MemoryBudgetBytes)}. " +
                        "Increase the budget or the maximum allowed base.");

                int maximumInitialVoxelMultiplier = individualPreflight.InitialVoxelMultiplier;
                if (safetyOptions.AdaptInitialVoxelSize)
                    maximumInitialVoxelMultiplier = styleProfile.GetLodMultiplier(Mathf.Clamp(
                        safetyOptions.MaximumInitialLodIndex, 0, styleProfile.LodCount - 1));
                VoxelLodBuildResult result = VoxelLodPipeline.GenerateAutomatic(
                    source, styleProfile, lodOptions,
                    (progress, message) => EditorUtility.DisplayCancelableProgressBar(
                        "Voxel Bridge · Automatic LOD", message, progress),
                    individualPreflight.InitialVoxelMultiplier,
                    safetyOptions.MaximumImportedVoxelCount,
                    maximumInitialVoxelMultiplier);
                lodSetManifest = AssetDatabase.LoadAssetAtPath<TextAsset>(result.ManifestAssetPath);
                if (result.VoxAssetPaths.Length > 0)
                {
                    lastVoxAssetPath = result.VoxAssetPaths[0];
                    lastVoxAsset = AssetDatabase.LoadMainAssetAtPath(lastVoxAssetPath);
                    manualParentVox = lastVoxAsset;
                    manualTargetLod = Mathf.Min(1, styleProfile.LodCount - 1);
                }
                lastPrefabAsset = AssetDatabase.LoadMainAssetAtPath(result.PrefabAssetPath);
                lastImpostorAsset = null;
                VoxelLodBuildResult placementBuild = result;
                if (individualGenerateImpostor)
                {
                    VoxelImpostorBuildResult impostorResult =
                        AmplifyImpostorIntegration.GenerateOrUpdate(
                            result.ManifestAssetPath, impostorProfile, impostorQuality);
                    lastImpostorAsset = AssetDatabase.LoadMainAssetAtPath(
                        impostorResult.ImpostorAssetPath);
                    lastPrefabAsset = AssetDatabase.LoadMainAssetAtPath(
                        impostorResult.PrefabAssetPath);
                    placementBuild = new VoxelLodBuildResult(
                        result.ManifestAssetPath, impostorResult.PrefabAssetPath,
                        result.VoxAssetPaths);
                }

                VoxelLodSinglePlacementResult? placement = null;
                if (individualPlaceInScene &&
                    VoxelLodBatchScenePlacement.CanPlace(source as GameObject))
                    placement = VoxelLodBatchScenePlacement.PlaceSingle(
                        (GameObject)source, placementBuild, styleProfile,
                        individualDisableOriginalObject);

                SelectAndPing(placement.HasValue ? placement.Value.Instance : lastVoxAsset);
                int actualInitialMultiplier = individualPreflight.InitialVoxelMultiplier;
                if (VoxelLodPipeline.TryReadManifest(
                        result.ManifestAssetPath, out VoxelLodSetManifest manifest))
                    actualInitialMultiplier = manifest.initialVoxelMultiplier;
                status = $"Family created: {result.VoxAssetPaths.Length} .vox file(s). " +
                         $"Initial base ×{actualInitialMultiplier} " +
                         $"({styleProfile.BaseVoxelSize * actualInitialMultiplier:0.###} m). " +
                         $"Prefab: {placementBuild.PrefabAssetPath}";
                if (individualGenerateImpostor)
                    status += $" Generated {VoxelImpostorProfile.GetQualityName(impostorQuality)} impostor.";
                if (placement.HasValue)
                    status += placement.Value.OriginalObjectDisabled
                        ? " Instance placed and original object disabled."
                        : " Instance placed in the scene.";
            }
            catch (OperationCanceledException)
            {
                status = "LOD generation cancelled.";
            }
            catch (Exception exception)
            {
                ShowException(exception);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void GenerateAutomaticBatch()
        {
            try
            {
                VoxelLodBatchOptions batchOptions = CreateBatchOptions();
                VoxelLodBatchSourcePlan[] plans =
                    VoxelLodPipeline.GetAutomaticBatchPlans(batchParent, batchOptions);
                VoxelLodBuildOptions lodOptions = CreateBatchLodOptions();
                string signature = VoxelLodBatchAnalyzer.CreateSignature(
                    plans, styleProfile, lodOptions, batchOptions);
                if (batchPreflight == null || batchPreflight.Signature != signature)
                {
                    batchOptions.Preflight = null;
                    batchPreflight = VoxelLodBatchAnalyzer.Analyze(
                        plans, styleProfile, lodOptions, batchOptions,
                        (progress, message) => EditorUtility.DisplayCancelableProgressBar(
                            "Voxel Bridge · Preflight Analysis", message, progress));
                }
                batchOptions.Preflight = batchPreflight;
                if (!batchOptions.SkipSourcesOverMemoryBudget &&
                    batchPreflight.OverBudgetCount > 0 &&
                    !EditorUtility.DisplayDialog("Voxel Bridge · Memory Risk",
                        $"{batchPreflight.OverBudgetCount} fuente(s) superan el presupuesto de " +
                        $"{batchMemoryBudgetMb} MiB. Continuar puede cerrar Unity por falta de memoria.",
                        "Continue", "Cancel"))
                {
                    status = "Batch generation cancelled before voxelization.";
                    return;
                }
                VoxelLodBatchBuildResult result = VoxelLodPipeline.GenerateAutomaticBatch(
                    batchParent, styleProfile, lodOptions,
                    (progress, message) => EditorUtility.DisplayCancelableProgressBar(
                        "Voxel Bridge · Batch Generation", message, progress),
                    batchOptions);

                VoxelImpostorBatchBuildResult impostorResult = null;
                if (batchGenerateImpostors && !result.Cancelled && result.SucceededCount > 0)
                {
                    var impostorOptions = new VoxelImpostorBatchOptions
                    {
                        SelectQualityBySize = batchSelectImpostorQualityBySize,
                        FixedQuality = impostorQuality,
                        MaximumEstimatedAtlasBytes =
                            batchImpostorAtlasBudgetMb * 1024L * 1024L
                    };
                    impostorResult = AmplifyImpostorIntegration.GenerateForBatch(
                        result, impostorProfile, impostorOptions,
                        (progress, message) => EditorUtility.DisplayCancelableProgressBar(
                            "Voxel Bridge · Batch Impostors", message, progress));
                    LogImpostorFailures(impostorResult);
                }

                foreach (VoxelLodBatchItemResult failed in result.Items.Where(item => item.Failed))
                    Debug.LogWarning($"Voxel Bridge skipped '{failed.Source.name}': {failed.Error}", failed.Source);

                VoxelLodBatchItemResult lastSuccess = result.Items.LastOrDefault(item => item.Succeeded);
                if (lastSuccess != null)
                    ApplyAutomaticBuildResult(lastSuccess.BuildResult);

                VoxelLodBatchPlacementResult? placement = null;
                if (batchPlaceInScene && VoxelLodBatchScenePlacement.CanPlace(batchParent) &&
                    result.SucceededCount > 0)
                    placement = VoxelLodBatchScenePlacement.Place(
                        batchParent, result, styleProfile, batchDisableOriginalRoot);

                if (placement.HasValue) SelectAndPing(placement.Value.Root);
                else SelectAndPing(lastPrefabAsset);

                int pendingCount = result.CandidateCount - result.Items.Length;
                string completion = result.Cancelled
                    ? $"Batch cancelled; {pendingCount} object(s) remain unprocessed."
                    : "Batch complete.";
                status = $"{completion} {result.CreatedFamilyCount} family output(s) created, " +
                         $"{result.ResumedCount} restored from checkpoint, " +
                         $"{result.ReusedCount} instance(s) reused, {result.FailedCount} failed and " +
                         $"{result.IgnoredCount} ignored.";
                if (impostorResult != null)
                {
                    status += $" {impostorResult.GeneratedCount} impostor(s) generated and " +
                              $"{impostorResult.FailedCount} failed.";
                    if (impostorResult.SkippedCount > 0)
                        status += $" {impostorResult.SkippedForSizeCount} skipped by size and " +
                                  $"{impostorResult.SkippedForBudgetCount} by budget; " +
                                  $"{impostorResult.SkippedDisabledCount} manually excluded.";
                    if (impostorResult.ReducedQualityCount > 0)
                        status += $" {impostorResult.ReducedQualityCount} used a lower-quality profile " +
                                  "to stay within budget.";
                    status += $" Planned atlases: " +
                              $"{VoxelLodBatchAnalyzer.FormatBytes(impostorResult.EstimatedAtlasBytes)}.";
                    if (impostorResult.Cancelled)
                        status += $" Bake cancelled; " +
                                  $"{impostorResult.RemainingCount} family output(s) remain pending.";
                }
                if (placement.HasValue)
                {
                    if (placement.Value.OriginalRootDisabled)
                        status += $" Placed {placement.Value.PlacedCount} objects and disabled the original root.";
                    else if (!batchDisableOriginalRoot)
                        status += $" Placed {placement.Value.PlacedCount} objects; the original root remains active.";
                    else
                        status += $" Created '{placement.Value.Root.name}' inactive because replacement was incomplete.";
                }
                if ((result.SucceededCount == 0 && result.FailedCount > 0) ||
                    impostorResult is { FailedCount: > 0 })
                    EditorUtility.DisplayDialog("Voxel Bridge · Batch Errors", status +
                        " Revisa la Console para ver el detalle de cada objeto.", "Close");
            }
            catch (OperationCanceledException)
            {
                status = "Batch generation cancelled.";
            }
            catch (Exception exception)
            {
                ShowException(exception);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void ApplyAutomaticBuildResult(VoxelLodBuildResult result)
        {
            lodSetManifest = AssetDatabase.LoadAssetAtPath<TextAsset>(result.ManifestAssetPath);
            if (result.VoxAssetPaths.Length > 0)
            {
                lastVoxAssetPath = result.VoxAssetPaths[0];
                lastVoxAsset = AssetDatabase.LoadMainAssetAtPath(lastVoxAssetPath);
                manualParentVox = lastVoxAsset;
                manualTargetLod = Mathf.Min(1, styleProfile.LodCount - 1);
            }
            lastPrefabAsset = AssetDatabase.LoadMainAssetAtPath(result.PrefabAssetPath);
            lastImpostorAsset = VoxelLodPipeline.TryReadManifest(
                    result.ManifestAssetPath, out VoxelLodSetManifest manifest) &&
                manifest.impostor != null
                    ? AssetDatabase.LoadMainAssetAtPath(manifest.impostor.assetPath)
                    : null;
        }

        private static string GetBatchOverrideDescription(VoxelPrefabOverrideHandling handling) =>
            handling switch
            {
                VoxelPrefabOverrideHandling.ConvertInstanceSeparately =>
                    "Las instancias modificadas conservan sus overrides visuales y generan una familia voxel propia.",
                VoxelPrefabOverrideHandling.IgnoreInstance =>
                    "Las instancias modificadas se excluyen del lote; las instancias sin overrides aún pueden reutilizar su prefab fuente.",
                _ =>
                    "Las instancias modificadas usan la geometría del prefab original. La instancia de escena no se altera, pero sus overrides visuales no aparecen en el resultado voxel."
            };

        private void GenerateManualLod()
        {
            int generatedLod = manualTargetLod;
            try
            {
                VoxelLodBuildResult result = VoxelLodPipeline.GenerateManual(
                    AssetDatabase.GetAssetPath(manualParentVox), styleProfile, generatedLod,
                    manualGenerationMode, CreateLodOptions());
                lodSetManifest = AssetDatabase.LoadAssetAtPath<TextAsset>(result.ManifestAssetPath);
                lastVoxAssetPath = result.VoxAssetPaths[0];
                lastVoxAsset = AssetDatabase.LoadMainAssetAtPath(lastVoxAssetPath);
                manualParentVox = lastVoxAsset;
                manualTargetLod = Mathf.Min(generatedLod + 1, styleProfile.LodCount - 1);
                lastPrefabAsset = AssetDatabase.LoadMainAssetAtPath(result.PrefabAssetPath);
                SelectAndPing(lastVoxAsset);
                status = $"Created LOD {generatedLod} as .vox and updated prefab: {result.PrefabAssetPath}";
            }
            catch (Exception exception)
            {
                ShowException(exception);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void RebuildLodPrefab()
        {
            try
            {
                string path = VoxelLodPipeline.RebuildPrefab(AssetDatabase.GetAssetPath(lodSetManifest));
                lastPrefabAsset = AssetDatabase.LoadMainAssetAtPath(path);
                SelectAndPing(lastPrefabAsset);
                status = $"LOD prefab rebuilt: {path}";
            }
            catch (Exception exception)
            {
                ShowException(exception);
            }
        }

        private void GenerateImpostor()
        {
            try
            {
                VoxelImpostorBuildResult result = AmplifyImpostorIntegration.GenerateOrUpdate(
                    AssetDatabase.GetAssetPath(lodSetManifest), impostorProfile, impostorQuality);
                lastImpostorAsset = AssetDatabase.LoadMainAssetAtPath(result.ImpostorAssetPath);
                lastPrefabAsset = AssetDatabase.LoadMainAssetAtPath(result.PrefabAssetPath);
                SelectAndPing(lastPrefabAsset);
                status = $"Generated {VoxelImpostorProfile.GetQualityName(impostorQuality)} impostor " +
                         $"from LOD0 and added it as the final level: {result.PrefabAssetPath}";
            }
            catch (Exception exception)
            {
                ShowException(exception);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void RemoveLoadedImpostor(VoxelLodSetManifest manifest)
        {
            string manifestPath = AssetDatabase.GetAssetPath(lodSetManifest);
            string prefabPath = RemoveImpostorWithConfirmation(
                manifestPath, manifest, lastPrefabAsset);
            if (string.IsNullOrEmpty(prefabPath)) return;

            lodSetManifest = AssetDatabase.LoadAssetAtPath<TextAsset>(manifestPath);
            lastImpostorAsset = null;
            lastPrefabAsset = AssetDatabase.LoadMainAssetAtPath(prefabPath);
            SelectAndPing(lastPrefabAsset);
            status = "Impostor removed from prefab. The family is excluded from " +
                     "automatic generation and retains the atlas as an unreferenced cache.";
        }

        private static string RemoveImpostorWithConfirmation(
            string manifestPath, VoxelLodSetManifest manifest, Object context)
        {
            if (!AmplifyImpostorIntegration.HasConfiguredImpostor(manifest) ||
                string.IsNullOrEmpty(manifestPath))
                return null;
            string modelName = string.IsNullOrWhiteSpace(manifest.sourceName)
                ? Path.GetFileNameWithoutExtension(manifestPath)
                : manifest.sourceName;
            if (!EditorUtility.DisplayDialog(
                    "Voxel Bridge · Remove Impostor",
                    $"Se quitará el impostor de '{modelName}' y se reconstruirá su prefab con " +
                    "los LOD voxel. El atlas generado se conservará como caché sin referencias. " +
                    "La familia quedará excluida de la generación automática hasta que se genere " +
                    "un nuevo impostor manualmente.",
                    "Remove Impostor", "Cancel"))
                return null;

            try
            {
                string prefabPath = AmplifyImpostorIntegration.RemoveFromFamily(manifestPath);
                Object prefab = AssetDatabase.LoadMainAssetAtPath(prefabPath);
                if (prefab != null)
                {
                    Selection.activeObject = prefab;
                    EditorGUIUtility.PingObject(prefab);
                }
                return prefabPath;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, context);
                EditorUtility.DisplayDialog(
                    "Voxel Bridge · Impostor Removal Failed",
                    exception.Message, "Close");
                return null;
            }
        }

        private void GeneratePendingImpostors()
        {
            try
            {
                string[] manifests =
                    AmplifyImpostorIntegration.FindPendingManifestAssetPaths(exportFolder);
                if (manifests.Length == 0)
                {
                    status = "No families are pending impostor generation in the export folder.";
                    return;
                }
                if (!EditorUtility.DisplayDialog(
                        "Voxel Bridge · Generate Pending Impostors",
                        $"Se procesarán {manifests.Length} familia(s) con el perfil " +
                        $"'{VoxelImpostorProfile.GetQualityName(impostorQuality)}'. " +
                        "Las familias pueden conservarse aunque un horneado individual falle.",
                        "Generate", "Cancel"))
                {
                    status = "Pending impostor generation cancelled before starting.";
                    return;
                }

                VoxelImpostorBatchBuildResult result =
                    AmplifyImpostorIntegration.GenerateForManifests(
                        manifests, impostorProfile, impostorQuality,
                        (progress, message) => EditorUtility.DisplayCancelableProgressBar(
                            "Voxel Bridge · Pending Impostors", message, progress));
                LogImpostorFailures(result);

                if (result.Builds.Length > 0)
                {
                    VoxelImpostorBuildResult last = result.Builds[result.Builds.Length - 1];
                    TryLoadFamilyFromAsset(
                        AssetDatabase.LoadMainAssetAtPath(last.PrefabAssetPath));
                    SelectAndPing(lastPrefabAsset);
                }

                status = result.Cancelled ? "Impostor bake cancelled." : "Bake complete.";
                status += $" {result.GeneratedCount} impostor(s) generated, " +
                          $"{result.FailedCount} failed and " +
                          $"{result.RemainingCount} pending.";
                if (result.FailedCount > 0)
                    EditorUtility.DisplayDialog("Voxel Bridge · Impostor Errors", status +
                        " Revisa la Console para ver el detalle de cada familia.", "Close");
            }
            catch (Exception exception)
            {
                ShowException(exception);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private static void LogImpostorFailures(VoxelImpostorBatchBuildResult result)
        {
            if (result == null) return;
            foreach (VoxelImpostorBatchFailure failure in result.Failures)
            {
                Object context = AssetDatabase.LoadMainAssetAtPath(failure.ManifestAssetPath);
                Debug.LogWarning(
                    $"Voxel Bridge could not generate an impostor for " +
                    $"'{failure.ManifestAssetPath}': {failure.Error}", context);
            }
        }

        private bool TryLoadFamilyFromAsset(Object asset)
        {
            if (asset == null || !VoxelLodPipeline.TryFindManifestForAsset(
                    AssetDatabase.GetAssetPath(asset), out string manifestPath,
                    out VoxelLodSetManifest manifest))
                return false;

            lodSetManifest = AssetDatabase.LoadAssetAtPath<TextAsset>(manifestPath);
            lastPrefabAsset = AssetDatabase.LoadMainAssetAtPath(manifest.prefabAssetPath);
            lastImpostorAsset = manifest.impostor == null
                ? null
                : AssetDatabase.LoadMainAssetAtPath(manifest.impostor.assetPath);
            if (manifest.lods != null && manifest.lods.Length > 0)
            {
                VoxelLodEntry first = manifest.lods.OrderBy(entry => entry.lodIndex).First();
                lastVoxAssetPath = first.voxAssetPath;
                lastVoxAsset = AssetDatabase.LoadMainAssetAtPath(first.voxAssetPath);
            }
            if (!string.IsNullOrEmpty(manifest.profileAssetPath))
                styleProfile = AssetDatabase.LoadAssetAtPath<VoxelStyleProfile>(manifest.profileAssetPath);
            if (manifest.impostor != null && !string.IsNullOrEmpty(manifest.impostor.profileAssetPath))
                impostorProfile = AssetDatabase.LoadAssetAtPath<VoxelImpostorProfile>(
                    manifest.impostor.profileAssetPath);
            impostorQuality = manifest.impostor == null
                ? VoxelImpostorQuality.Medium
                : VoxelImpostorProfile.NormalizeQuality(manifest.impostor.quality);
            status = $"Family loaded: {manifest.sourceName}.";
            return true;
        }

        internal static bool TryResolveHierarchyLod(
            GameObject selected, out Object voxAsset, out string voxAssetPath, out int lodIndex)
        {
            voxAsset = null;
            voxAssetPath = null;
            lodIndex = -1;
            if (!TryResolveHierarchyFamily(selected, out _,
                    out VoxelLodSetManifest manifest, out LODGroup lodGroup) ||
                selected.transform == lodGroup.transform)
                return false;

            VoxelLodEntry[] entries = (manifest.lods ?? Array.Empty<VoxelLodEntry>())
                .OrderBy(entry => entry.lodIndex)
                .ToArray();
            LOD[] unityLods = lodGroup.GetLODs();
            int count = Mathf.Min(entries.Length, unityLods.Length);
            for (int i = 0; i < count; i++)
            {
                bool containsSelection = unityLods[i].renderers.Any(renderer => renderer != null &&
                    (renderer.transform == selected.transform ||
                     renderer.transform.IsChildOf(selected.transform) ||
                     selected.transform.IsChildOf(renderer.transform)));
                if (!containsSelection) continue;

                voxAssetPath = entries[i].voxAssetPath;
                voxAsset = AssetDatabase.LoadMainAssetAtPath(voxAssetPath);
                lodIndex = entries[i].lodIndex;
                return voxAsset != null;
            }

            return false;
        }

        private static bool TryResolveHierarchyFamily(
            GameObject selected, out string prefabAssetPath,
            out VoxelLodSetManifest manifest, out LODGroup lodGroup)
        {
            prefabAssetPath = null;
            manifest = null;
            lodGroup = selected != null ? selected.GetComponentInParent<LODGroup>(true) : null;
            if (lodGroup == null) return false;

            Object originalRoot = PrefabUtility.GetCorrespondingObjectFromOriginalSource(lodGroup.gameObject);
            if (originalRoot != null) prefabAssetPath = AssetDatabase.GetAssetPath(originalRoot);
            if (string.IsNullOrEmpty(prefabAssetPath))
            {
                var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
                if (prefabStage != null && selected.scene == prefabStage.scene)
                    prefabAssetPath = prefabStage.assetPath;
            }
            if (string.IsNullOrEmpty(prefabAssetPath))
                prefabAssetPath = AssetDatabase.GetAssetPath(lodGroup.gameObject);

            return VoxelLodPipeline.TryFindManifestForAsset(
                prefabAssetPath, out _, out manifest);
        }

        private void ShowException(Exception exception)
        {
            status = "Error: " + exception.Message;
            Debug.LogException(exception);
            EditorUtility.DisplayDialog("Voxel Bridge", status, "Close");
        }

        private static void SelectAndPing(Object value)
        {
            if (value == null) return;
            Selection.activeObject = value;
            EditorGUIUtility.PingObject(value);
        }
    }
}
