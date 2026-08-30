using System;
using System.Collections.Generic;
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
        [SerializeField] private bool batchIgnoreInactiveObjects = true;
        [SerializeField] private bool batchResumeInterrupted = true;
        [SerializeField, Range(1, 25)] private int batchCleanupInterval = 1;
        [SerializeField] private bool batchShowPlanDetails;
        private VoxelLodBatchPreflight batchPreflight;
        private VoxelStyleProfile styleProfile;
        private VoxelImpostorProfile impostorProfile;
        [SerializeField] private VoxelImpostorQuality impostorQuality = VoxelImpostorQuality.Medium;
        private VoxelColorMode colorMode = VoxelColorMode.MaterialAndTexture;
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

        [MenuItem("Tools/Voxel Bridge/Modelos físicos y LODs", false, 100)]
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

        [MenuItem("Assets/Voxel Bridge/Generar modelo físico y LODs", false, 2100)]
        private static void OpenFromSelection() => OpenWindow();

        [MenuItem("Assets/Voxel Bridge/Generar modelo físico y LODs", true)]
        private static bool ValidateOpenFromSelection() =>
            VoxelBridgeSourceSelection.IsSupported(Selection.activeObject);

        [MenuItem("GameObject/Voxel Bridge/Generar hijos como familias voxel y LOD", false, 48)]
        private static void OpenBatchFromHierarchy()
        {
            var window = GetOrCreateWindow();
            window.batchParent = Selection.activeGameObject;
            window.Show();
            window.Focus();
        }

        [MenuItem("GameObject/Voxel Bridge/Generar hijos como familias voxel y LOD", true)]
        private static bool ValidateOpenBatchFromHierarchy() =>
            Selection.activeGameObject != null && Selection.activeGameObject.transform.childCount > 0;

        [MenuItem("Assets/Voxel Bridge/Editar LODs de la familia", false, 2101)]
        private static void OpenFamilyFromSelection()
        {
            var window = GetOrCreateWindow();
            if (!window.TryLoadFamilyFromAsset(Selection.activeObject)) return;
            window.Show();
            window.Focus();
        }

        [MenuItem("Assets/Voxel Bridge/Editar LODs de la familia", true)]
        private static bool ValidateOpenFamilyFromSelection() =>
            Selection.activeObject != null && VoxelLodPipeline.TryFindManifestForAsset(
                AssetDatabase.GetAssetPath(Selection.activeObject), out _, out _);

        [MenuItem("Assets/Voxel Bridge/Configurar o generar impostor final", false, 2102)]
        private static void OpenImpostorFromSelection()
        {
            var window = GetOrCreateWindow();
            if (!window.TryLoadFamilyFromAsset(Selection.activeObject)) return;
            window.status = "Familia cargada. Revisa el perfil y genera el impostor final.";
            window.Show();
            window.Focus();
        }

        [MenuItem("Assets/Voxel Bridge/Configurar o generar impostor final", true)]
        private static bool ValidateOpenImpostorFromSelection() =>
            ValidateOpenFamilyFromSelection();

        [MenuItem("GameObject/Voxel Bridge/Editar este LOD en MagicaVoxel", false, 49)]
        private static void EditHierarchyLod()
        {
            if (TryResolveHierarchyLod(Selection.activeGameObject,
                    out Object voxAsset, out _, out _))
                MagicaVoxelLauncher.OpenAsset(voxAsset);
        }

        [MenuItem("GameObject/Voxel Bridge/Editar este LOD en MagicaVoxel", true)]
        private static bool ValidateEditHierarchyLod() =>
            TryResolveHierarchyLod(Selection.activeGameObject, out _, out _, out _);

        [MenuItem("GameObject/Voxel Bridge/Editar LODs de la familia", false, 50)]
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

        [MenuItem("GameObject/Voxel Bridge/Editar LODs de la familia", true)]
        private static bool ValidateOpenHierarchyFamily() =>
            TryResolveHierarchyFamily(Selection.activeGameObject, out _, out _, out _);

        [MenuItem("GameObject/Voxel Bridge/Configurar o generar impostor final", false, 51)]
        private static void OpenHierarchyImpostor()
        {
            if (!TryResolveHierarchyFamily(Selection.activeGameObject,
                    out string prefabAssetPath, out _, out _))
                return;
            var window = GetOrCreateWindow();
            if (!window.TryLoadFamilyFromAsset(AssetDatabase.LoadMainAssetAtPath(prefabAssetPath))) return;
            window.status = "Familia cargada. Revisa el perfil y genera el impostor final.";
            window.Show();
            window.Focus();
        }

        [MenuItem("GameObject/Voxel Bridge/Configurar o generar impostor final", true)]
        private static bool ValidateOpenHierarchyImpostor() =>
            ValidateOpenHierarchyFamily();

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
            batchPreflight = null;
            Repaint();
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("Modelos físicos y LODs", EditorStyles.boldLabel);
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
            EditorGUILayout.LabelField("1. Generación automática", EditorStyles.boldLabel);
            source = EditorGUILayout.ObjectField(new GUIContent("Modelo", "GameObject, prefab, FBX/OBJ o Mesh"),
                source, typeof(Object), true);
            styleProfile = (VoxelStyleProfile)EditorGUILayout.ObjectField(
                new GUIContent("Perfil voxel", "Unidad física, LODs, chunks y transiciones compartidos"),
                styleProfile, typeof(VoxelStyleProfile), false);

            if (styleProfile == null)
            {
                EditorGUILayout.HelpBox(
                    "Crea o asigna un perfil para generar modelos con vóxeles y LODs consistentes.",
                    MessageType.Warning);
                if (GUILayout.Button("Crear perfil voxel predeterminado")) CreateDefaultProfile();
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
                    $"Unidad base: {styleProfile.BaseVoxelSize:0.###} m · Chunk: {styleProfile.ChunkCellSize} celdas\n{levels}",
                    MessageType.Info);
            }

            DrawColorSettings();
            VoxelBridgeFolderPicker.Draw("Carpeta de familias", ref exportFolder);
            bool canGenerate = source != null && VoxelBridgeSourceSelection.IsSupported(source) &&
                               styleProfile != null && styleProfile.TryValidate(out _) &&
                               VoxelLodPipeline.IsAssetFolder(exportFolder);
            using (new EditorGUI.DisabledScope(!canGenerate))
            {
                if (GUILayout.Button("Generar familia .vox + prefab LOD", GUILayout.Height(38)))
                    GenerateAutomaticLods();
            }
            if (source != null && !VoxelBridgeSourceSelection.IsSupported(source))
                EditorGUILayout.HelpBox("Selecciona un GameObject, prefab, FBX/OBJ o Mesh.", MessageType.Warning);

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Generación por lotes", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "Convierte cada hijo directo del padre en una familia independiente. Cada familia incluye las mallas de sus descendientes según la opción de objetos desactivados. Los hijos sin mallas utilizables se omiten.",
                MessageType.Info);
            batchParent = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Objeto padre", "Objeto de escena o prefab cuyos hijos directos se procesarán por separado"),
                batchParent, typeof(GameObject), true);
            batchIgnoreInactiveObjects = EditorGUILayout.Toggle(
                new GUIContent("Ignorar objetos desactivados",
                    "Omite hijos directos desactivados y mallas situadas bajo descendientes desactivados. El estado del padre del lote no afecta esta evaluación."),
                batchIgnoreInactiveObjects);
            batchReusePrefabSources = EditorGUILayout.Toggle(
                new GUIContent("Reutilizar prefab de origen",
                    "Convierte una sola vez las instancias sin overrides que procedan del mismo prefab y reutiliza esa familia."),
                batchReusePrefabSources);
            batchModifiedPrefabHandling = (VoxelPrefabOverrideHandling)EditorGUILayout.Popup(
                new GUIContent("Instancias modificadas",
                    "Decide qué hacer cuando una instancia tiene overrides distintos de posición, rotación o escala del root."),
                (int)batchModifiedPrefabHandling,
                new[]
                {
                    new GUIContent("Usar prefab original", "Descarta los overrides visuales para la conversión y usa la familia del prefab fuente."),
                    new GUIContent("Convertir como fuente separada", "Voxeliza la jerarquía modificada y crea una familia exclusiva para esta instancia."),
                    new GUIContent("Ignorar instancia", "No convierte ni coloca esta instancia modificada.")
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
                    $"{batchPlans.Length} objeto(s) con malla · {conversionCount} conversión(es) · " +
                    $"{reuseCount} reutilización(es) · {modifiedCount} instancia(s) modificadas · " +
                    $"{ignoredCount} ignoradas · {inactiveDirectChildCount} desactivadas omitidas · " +
                    $"{skippedCount} sin malla activa.",
                    batchPlans.Any(plan => !plan.Ignored) ? MessageType.None : MessageType.Warning);

                batchShowPlanDetails = EditorGUILayout.Foldout(
                    batchShowPlanDetails, "Ver plan convertir / reutilizar / ignorar", true);
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
                            action = "IGNORAR · instancia con overrides";
                        }
                        else if (!seen.Add(plan.ReuseKey))
                        {
                            action = $"REUTILIZAR · {plan.ConversionSource.name}";
                        }
                        else if (plan.UsesPrefabSource && plan.HasPrefabOverrides)
                        {
                            action = $"CONVERTIR PREFAB · {plan.ConversionSource.name} · ignorar overrides";
                        }
                        else if (plan.UsesPrefabSource)
                        {
                            string sourcePath = AssetDatabase.GetAssetPath(plan.ConversionSource);
                            action = $"CONVERTIR PREFAB · {plan.ConversionSource.name}" +
                                     (string.IsNullOrEmpty(sourcePath) ? string.Empty : $" · {sourcePath}");
                        }
                        else if (plan.HasPrefabOverrides)
                        {
                            action = "CONVERTIR SEPARADO · instancia editada";
                        }
                        else
                        {
                            action = "CONVERTIR · objeto sin fuente prefab reutilizable";
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
            EditorGUILayout.LabelField("Seguridad de memoria", EditorStyles.miniBoldLabel);
            batchMemoryBudgetMb = Mathf.Clamp(EditorGUILayout.IntField(
                new GUIContent("Presupuesto por modelo (MiB)",
                    "Límite estimado de memoria temporal para una fuente. El valor debe ajustarse según la memoria disponible y la carga del Editor."),
                batchMemoryBudgetMb), 256, 8192);
            batchAdaptInitialVoxelSize = EditorGUILayout.Toggle(
                new GUIContent("Adaptar resolución automáticamente",
                    "Prueba tamaños voxel equivalentes a los LOD del perfil y usa el menor que cumple el límite de rejilla y el presupuesto de memoria."),
                batchAdaptInitialVoxelSize);
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
                    new GUIContent("Base máxima permitida",
                        "Último tamaño base que puede seleccionar la adaptación automática."),
                    batchMaximumInitialLodIndex, initialLodLabels);
                EditorGUILayout.HelpBox(
                    "Cada fuente usa la base más fina que cumple los límites. Las fuentes que todavía excedan la rejilla o el presupuesto con la base máxima se omiten.",
                    MessageType.None);
            }
            using (new EditorGUI.DisabledScope(batchAdaptInitialVoxelSize))
            {
                bool skipOverBudget = EditorGUILayout.Toggle(
                    new GUIContent("Omitir modelos sobre presupuesto",
                        batchAdaptInitialVoxelSize
                            ? "La adaptación automática siempre omite las fuentes que no caben con la base máxima."
                            : "Evita iniciar fuentes cuyo pico estimado supera el límite. Se registran como error y el resto del lote continúa."),
                    batchAdaptInitialVoxelSize || batchSkipOverMemoryBudget);
                if (!batchAdaptInitialVoxelSize) batchSkipOverMemoryBudget = skipOverBudget;
            }
            batchResumeInterrupted = EditorGUILayout.Toggle(
                new GUIContent("Reanudar lote interrumpido",
                    "Recupera familias completas registradas en Library y continúa con las fuentes pendientes. El checkpoint se descarta al terminar el lote."),
                batchResumeInterrupted);
            batchCleanupInterval = EditorGUILayout.IntSlider(
                new GUIContent("Limpiar cada N familias",
                    "Libera assets, texturas temporales y memoria administrada después de este número de fuentes únicas. 1 es el valor más seguro; 2–4 puede ser algo más rápido."),
                batchCleanupInterval, 1, 25);
            EditorGUILayout.HelpBox(
                "Cada familia terminada se registra fuera de Assets. Si Unity se cierra, la siguiente ejecución con la misma fuente y configuración reutiliza esas familias. Las carpetas parciales marcadas por la herramienta se eliminan antes de continuar.",
                MessageType.None);
            if (GUILayout.Button("Buscar y limpiar salidas incompletas"))
            {
                int cleaned = VoxelLodBatchRecovery.CleanupIncompleteFamilies(exportFolder);
                status = cleaned > 0
                    ? $"Se eliminaron {cleaned} familia(s) incompletas marcadas por Voxel Bridge."
                    : "No se encontraron familias incompletas marcadas por Voxel Bridge.";
            }
            using (new EditorGUI.DisabledScope(batchPlans.All(plan => plan.Ignored) ||
                                               styleProfile == null ||
                                               !styleProfile.TryValidate(out _)))
            {
                if (GUILayout.Button("Analizar memoria del lote")) AnalyzeAutomaticBatch();
            }
            if (batchPreflight != null)
            {
                MessageType analysisType = batchPreflight.InvalidCount > 0 ||
                                           batchPreflight.OverBudgetCount > 0
                    ? MessageType.Warning
                    : MessageType.Info;
                EditorGUILayout.HelpBox(
                    $"{batchPreflight.UniqueConversionCount} fuente(s) única(s) · " +
                    $"pico estimado {VoxelLodBatchAnalyzer.FormatBytes(batchPreflight.EstimatedPeakBytes)} · " +
                    $"{batchPreflight.TotalDenseCells:N0} celdas entre todos los LODs · " +
                    $"{batchPreflight.AdaptedCount} con base adaptada · " +
                    $"{batchPreflight.OverBudgetCount} sobre presupuesto · " +
                    $"{batchPreflight.InvalidCount} inválidas.", analysisType);
                foreach (VoxelLodBatchSourceEstimate estimate in batchPreflight.Sources
                             .Where(item => item.WasAdapted ||
                                            item.Risk is VoxelLodBatchMemoryRisk.OverBudget or
                                                VoxelLodBatchMemoryRisk.Invalid)
                             .Take(8))
                    EditorGUILayout.LabelField(
                        $"• {estimate.SourceName}: {FormatBatchEstimate(estimate)}",
                        EditorStyles.wordWrappedMiniLabel);
            }

            bool canPlaceBatch = VoxelLodBatchScenePlacement.CanPlace(batchParent);
            using (new EditorGUI.DisabledScope(!canPlaceBatch))
                batchPlaceInScene = EditorGUILayout.Toggle(
                    new GUIContent("Colocar resultado en escena",
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
                    new GUIContent("Desactivar raíz original",
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
                                    VoxelLodPipeline.IsAssetFolder(exportFolder);
            using (new EditorGUI.DisabledScope(!canGenerateBatch))
            {
                if (GUILayout.Button("Generar familias de todos los hijos", GUILayout.Height(38)))
                    GenerateAutomaticBatch();
            }
        }

        private void DrawColorSettings()
        {
            colorMode = (VoxelColorMode)EditorGUILayout.Popup("Origen del color", (int)colorMode,
                new[] { "Material + textura", "Solo material", "Color único" });
            if (colorMode == VoxelColorMode.SingleColor)
                singleColor = EditorGUILayout.ColorField("Color", singleColor);
            if (colorMode == VoxelColorMode.MaterialAndTexture)
                alphaCutoff = EditorGUILayout.Slider(
                    new GUIContent("Corte de alpha", "Descarta texeles transparentes"), alphaCutoff, 0f, 1f);
        }

        private void DrawManualSection()
        {
            EditorGUILayout.LabelField("2. LOD manual", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Parte del .vox editado anterior: duplícalo para una simplificación libre o redúcelo a la rejilla física del nuevo nivel.",
                MessageType.Info);
            manualParentVox = EditorGUILayout.ObjectField("LOD padre (.vox)", manualParentVox,
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
                manualTargetLod = EditorGUILayout.Popup("LOD destino", selected, labels) + firstTargetLod;
            }
            else if (VoxelImporterIntegration.IsVoxAsset(manualParentVox) && styleProfile != null)
            {
                EditorGUILayout.HelpBox("El LOD padre ya es el último nivel del perfil.", MessageType.Warning);
            }

            manualGenerationMode = (VoxelLodGenerationMode)EditorGUILayout.Popup(
                "Punto de partida", manualGenerationMode == VoxelLodGenerationMode.DuplicateParent ? 0 : 1,
                new[] { "Duplicar anterior (libre)", "Reducir a resolución objetivo" }) == 0
                ? VoxelLodGenerationMode.DuplicateParent
                : VoxelLodGenerationMode.ReduceParent;
            bool canGenerate = hasManualTarget && styleProfile.TryValidate(out _) &&
                               VoxelImporterIntegration.IsVoxAsset(manualParentVox);
            using (new EditorGUI.DisabledScope(!canGenerate))
            {
                if (GUILayout.Button("Crear LOD manual y actualizar prefab", GUILayout.Height(32)))
                    GenerateManualLod();
            }

            EditorGUILayout.Space(8);
            lodSetManifest = (TextAsset)EditorGUILayout.ObjectField("Manifiesto .voxset", lodSetManifest,
                typeof(TextAsset), false);
            using (new EditorGUI.DisabledScope(lodSetManifest == null))
            {
                if (GUILayout.Button("Reconstruir prefab desde manifiesto")) RebuildLodPrefab();
            }
            DrawFamilyLodEditor();
        }

        private void DrawFamilyLodEditor()
        {
            if (lodSetManifest == null || !VoxelLodPipeline.TryFindManifestForAsset(
                    AssetDatabase.GetAssetPath(lodSetManifest), out _, out VoxelLodSetManifest manifest))
                return;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Editar niveles de la familia", EditorStyles.boldLabel);
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
                    if (GUILayout.Button("Seleccionar", GUILayout.Width(80))) SelectAndPing(voxAsset);
                    if (GUILayout.Button("Editar", GUILayout.Width(58))) MagicaVoxelLauncher.OpenAsset(voxAsset);
                    if (GUILayout.Button("Usar como padre", GUILayout.Width(105)))
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
            status = $"LOD {lodIndex} seleccionado como base del siguiente LOD manual.";
        }

        private void DrawOutputSection()
        {
            EditorGUILayout.LabelField("4. Resultados", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Los niveles editables siguen siendo archivos .vox. El prefab los agrupa para la escena y, si se generó, añade el impostor de Amplify como último LOD.",
                MessageType.Info);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("LOD generado (.vox)", lastVoxAsset, typeof(Object), false);
                EditorGUILayout.ObjectField("Prefab para escena", lastPrefabAsset, typeof(Object), false);
                EditorGUILayout.ObjectField("Impostor Amplify", lastImpostorAsset, typeof(Object), false);
            }
            if (!string.IsNullOrEmpty(lastVoxAssetPath))
            {
                EditorGUILayout.LabelField("Archivo real", EditorStyles.miniBoldLabel);
                EditorGUILayout.SelectableLabel(lastVoxAssetPath, EditorStyles.textField,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
            if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);

            using (new EditorGUI.DisabledScope(lastVoxAsset == null))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Seleccionar .vox")) SelectAndPing(lastVoxAsset);
                if (GUILayout.Button("Mostrar archivo .vox"))
                    EditorUtility.RevealInFinder(VoxelLodPipeline.AssetPathToAbsolute(lastVoxAssetPath));
                if (GUILayout.Button("Abrir en MagicaVoxel")) MagicaVoxelLauncher.OpenAsset(lastVoxAsset);
                EditorGUILayout.EndHorizontal();
            }
            using (new EditorGUI.DisabledScope(lastPrefabAsset == null))
            {
                if (GUILayout.Button("Seleccionar prefab para escena")) SelectAndPing(lastPrefabAsset);
            }
            using (new EditorGUI.DisabledScope(lastImpostorAsset == null))
            {
                if (GUILayout.Button("Seleccionar asset del impostor")) SelectAndPing(lastImpostorAsset);
            }
        }

        private void DrawImpostorSection()
        {
            EditorGUILayout.LabelField("3. Impostor final", EditorStyles.boldLabel);
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

            if (impostorProfile == null)
            {
                EditorGUILayout.HelpBox(
                    "La configuración central contiene los perfiles de uso compartidos por todas las familias.",
                    MessageType.Warning);
                if (GUILayout.Button("Crear configuración de perfiles de impostor"))
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
                    EditorGUILayout.HelpBox(
                        $"{settings.ImpostorType} · atlas {settings.TextureResolution} · " +
                        $"{settings.Frames}x{settings.Frames} vistas · padding {settings.PixelPadding}px\n" +
                        $"LOD voxel → impostor: {lastVoxelTransition:0.####} · descarte: " +
                        $"{settings.CullScreenHeight:0.####} · Cross Fade: " +
                        (settings.CrossFade ? settings.FadeTransitionWidth.ToString("0.##") : "desactivado"),
                        MessageType.None);
            }

            Object currentImpostor = hasManifest && manifest.impostor != null
                ? AssetDatabase.LoadMainAssetAtPath(manifest.impostor.assetPath)
                : null;
            if (currentImpostor != null)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.ObjectField("Asset actual", currentImpostor, typeof(Object), false);
                if (GUILayout.Button("Seleccionar", GUILayout.Width(80))) SelectAndPing(currentImpostor);
                EditorGUILayout.EndHorizontal();
            }

            bool canGenerate = compatibility.CanBake && hasManifest && validProfile;
            using (new EditorGUI.DisabledScope(!canGenerate))
            {
                if (GUILayout.Button("Generar o actualizar impostor final", GUILayout.Height(36)))
                    GenerateImpostor();
            }
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
                new GUIContent("Perfil de impostor",
                    "Selecciona el conjunto de captura, atlas, silueta, transición y descarte que corresponde al uso de este modelo."),
                selected, labels)];

            EditorGUILayout.HelpBox(
                VoxelImpostorProfile.GetQualityDescription(impostorQuality), MessageType.None);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("Configuración central", impostorProfile,
                    typeof(VoxelImpostorProfile), false);
            if (GUILayout.Button("Editar valores", GUILayout.Width(100))) SelectAndPing(impostorProfile);
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
            string budget = estimate.IsOverBudget ? " · sobre presupuesto" : string.Empty;
            return $"base LOD{estimate.InitialLodIndex} · ×{estimate.InitialVoxelMultiplier}" +
                   $"{voxelSize} · {VoxelLodBatchAnalyzer.FormatBytes(estimate.EstimatedPeakBytes)}" +
                   budget;
        }

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
                        "Voxel Bridge · Análisis de memoria", message, progress));
                status = $"Análisis terminado: {batchPreflight.UniqueConversionCount} fuente(s) únicas, " +
                         $"pico {VoxelLodBatchAnalyzer.FormatBytes(batchPreflight.EstimatedPeakBytes)}.";
            }
            catch (OperationCanceledException)
            {
                status = "Análisis de memoria cancelado.";
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
                VoxelLodBuildResult result = VoxelLodPipeline.GenerateAutomatic(
                    source, styleProfile, CreateLodOptions(),
                    (progress, message) => EditorUtility.DisplayCancelableProgressBar(
                        "Voxel Bridge · LOD automático", message, progress));
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
                SelectAndPing(lastVoxAsset);
                status = $"Familia creada: {result.VoxAssetPaths.Length} archivo(s) .vox. Prefab: {result.PrefabAssetPath}";
            }
            catch (OperationCanceledException)
            {
                status = "Generación LOD cancelada.";
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
                            "Voxel Bridge · Análisis previo", message, progress));
                }
                batchOptions.Preflight = batchPreflight;
                if (!batchOptions.SkipSourcesOverMemoryBudget &&
                    batchPreflight.OverBudgetCount > 0 &&
                    !EditorUtility.DisplayDialog("Voxel Bridge · Riesgo de memoria",
                        $"{batchPreflight.OverBudgetCount} fuente(s) superan el presupuesto de " +
                        $"{batchMemoryBudgetMb} MiB. Continuar puede cerrar Unity por falta de memoria.",
                        "Continuar", "Cancelar"))
                {
                    status = "Generación por lotes cancelada antes de voxelizar.";
                    return;
                }
                VoxelLodBatchBuildResult result = VoxelLodPipeline.GenerateAutomaticBatch(
                    batchParent, styleProfile, lodOptions,
                    (progress, message) => EditorUtility.DisplayCancelableProgressBar(
                        "Voxel Bridge · Generación por lotes", message, progress),
                    batchOptions);

                foreach (VoxelLodBatchItemResult failed in result.Items.Where(item => item.Failed))
                    Debug.LogWarning($"Voxel Bridge omitió '{failed.Source.name}': {failed.Error}", failed.Source);

                VoxelLodBatchItemResult lastSuccess = result.Items.LastOrDefault(item => item.Succeeded);
                if (lastSuccess != null)
                    ApplyAutomaticBuildResult(lastSuccess.BuildResult);

                VoxelLodBatchPlacementResult? placement = null;
                if (batchPlaceInScene && VoxelLodBatchScenePlacement.CanPlace(batchParent) &&
                    result.SucceededCount > 0)
                    placement = VoxelLodBatchScenePlacement.Place(
                        batchParent, result, batchDisableOriginalRoot);

                if (placement.HasValue) SelectAndPing(placement.Value.Root);
                else SelectAndPing(lastPrefabAsset);

                int pendingCount = result.CandidateCount - result.Items.Length;
                string completion = result.Cancelled
                    ? $"Lote cancelado; quedan {pendingCount} objeto(s) sin procesar."
                    : "Lote terminado.";
                status = $"{completion} {result.CreatedFamilyCount} familia(s) creadas, " +
                         $"{result.ResumedCount} recuperadas desde checkpoint, " +
                         $"{result.ReusedCount} instancia(s) reutilizadas, {result.FailedCount} con errores y " +
                         $"{result.IgnoredCount} ignoradas.";
                if (placement.HasValue)
                {
                    if (placement.Value.OriginalRootDisabled)
                        status += $" Se colocaron {placement.Value.PlacedCount} objetos y se desactivó la raíz original.";
                    else if (!batchDisableOriginalRoot)
                        status += $" Se colocaron {placement.Value.PlacedCount} objetos; la raíz original permanece activa.";
                    else
                        status += $" Se creó '{placement.Value.Root.name}' desactivada porque el reemplazo no era completo.";
                }
                if (result.SucceededCount == 0 && result.FailedCount > 0)
                    EditorUtility.DisplayDialog("Voxel Bridge · Lote con errores", status +
                        " Revisa la Console para ver el detalle de cada objeto.", "Cerrar");
            }
            catch (OperationCanceledException)
            {
                status = "Generación por lotes cancelada.";
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
            lastImpostorAsset = null;
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
                status = $"LOD {generatedLod} creado como .vox y prefab actualizado: {result.PrefabAssetPath}";
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
                status = $"Prefab LOD reconstruido: {path}";
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
                status = $"Impostor {VoxelImpostorProfile.GetQualityName(impostorQuality)} generado " +
                         $"desde LOD0 y añadido como último nivel: {result.PrefabAssetPath}";
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
            status = $"Familia cargada: {manifest.sourceName}.";
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
            EditorUtility.DisplayDialog("Voxel Bridge", status, "Cerrar");
        }

        private static void SelectAndPing(Object value)
        {
            if (value == null) return;
            Selection.activeObject = value;
            EditorGUIUtility.PingObject(value);
        }
    }
}
