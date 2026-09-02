using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    internal sealed class VoxelPaletteWindow : EditorWindow
    {
        [SerializeField] private VoxelColorPalette colorPalette;
        [SerializeField] private VoxelSurfacePalette surfacePalette;
        private Vector2 scroll;

        [MenuItem("Tools/Voxel Bridge/Paletas globales", false, 130)]
        private static void Open()
        {
            VoxelPaletteWindow window = GetWindow<VoxelPaletteWindow>("Paletas voxel");
            window.minSize = new Vector2(470f, 360f);
            window.LoadDefaultsIfAvailable();
            window.Show();
        }

        private void OnEnable() => LoadDefaultsIfAvailable();

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("Paletas globales", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Los modelos almacenan ColorID y SurfaceID estables. Las propiedades PBR pertenecen a la " +
                "paleta de superficies y las LUT se generan como texturas RGBA32 de 256×1.",
                MessageType.Info);
            EditorGUILayout.HelpBox(
                "Capacidad global: 256 colores × 256 superficies = 65.536 combinaciones. " +
                "Un archivo .vox puede transportar hasta 255 pares locales utilizados.",
                MessageType.None);

            EditorGUILayout.Space(8f);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Crear o cargar paletas canónicas"))
                    CreateOrLoadCanonicalPalettes();
                if (GUILayout.Button("Regenerar ambas LUT"))
                    RebuildBoth();
            }
            if (GUILayout.Button("Instalar biblioteca y perfiles de color recomendados"))
                InstallRecommendedColorLibrary();

            EditorGUILayout.Space(10f);
            DrawColorSection();
            EditorGUILayout.Space(12f);
            DrawSurfaceSection();
            EditorGUILayout.EndScrollView();
        }

        private void DrawColorSection()
        {
            EditorGUILayout.LabelField("Colores", EditorStyles.boldLabel);
            colorPalette = (VoxelColorPalette)EditorGUILayout.ObjectField(
                "Paleta", colorPalette, typeof(VoxelColorPalette), false);
            if (colorPalette == null)
            {
                EditorGUILayout.HelpBox("Asigna o crea una paleta global de colores.", MessageType.Warning);
                return;
            }

            DrawStatus(colorPalette.TryValidate(out string error), error,
                VoxelPaletteLutGenerator.IsCurrent(colorPalette));
            EditorGUILayout.HelpBox(
                "La biblioteca recomendada utiliza ColorID 0–223 y reserva 224–255. " +
                "Los perfiles seleccionan subconjuntos de esta misma paleta.", MessageType.None);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Seleccionar paleta")) Select(colorPalette);
                if (GUILayout.Button("Regenerar LUT de color")) Rebuild(colorPalette);
            }
            DrawGeneratedTexture(colorPalette.GeneratedLut);
        }

        private void DrawSurfaceSection()
        {
            EditorGUILayout.LabelField("Superficies", EditorStyles.boldLabel);
            surfacePalette = (VoxelSurfacePalette)EditorGUILayout.ObjectField(
                "Paleta", surfacePalette, typeof(VoxelSurfacePalette), false);
            if (surfacePalette == null)
            {
                EditorGUILayout.HelpBox("Asigna o crea una paleta global de superficies.", MessageType.Warning);
                return;
            }

            DrawStatus(surfacePalette.TryValidate(out string error), error,
                VoxelPaletteLutGenerator.IsCurrent(surfacePalette));
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Seleccionar paleta")) Select(surfacePalette);
                if (GUILayout.Button("Regenerar LUT de superficie")) Rebuild(surfacePalette);
            }
            DrawGeneratedTexture(surfacePalette.GeneratedLut);
        }

        private static void DrawStatus(bool valid, string error, bool lutCurrent)
        {
            if (!valid)
                EditorGUILayout.HelpBox(error, MessageType.Error);
            else if (!lutCurrent)
                EditorGUILayout.HelpBox("La LUT no existe o no coincide con la paleta.", MessageType.Warning);
            else
                EditorGUILayout.HelpBox("Paleta válida y LUT actualizada.", MessageType.Info);
        }

        private static void DrawGeneratedTexture(Texture2D texture)
        {
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("LUT generada", texture, typeof(Texture2D), false);
        }

        private void CreateOrLoadCanonicalPalettes()
        {
            if (!VoxelPaletteAssetUtility.TryEnsureCanonicalAssets(
                    out colorPalette, out surfacePalette, out string error))
            {
                EditorUtility.DisplayDialog("Voxel Bridge", error, "Cerrar");
                return;
            }
            Repaint();
        }

        private void InstallRecommendedColorLibrary()
        {
            if (!EditorUtility.DisplayDialog(
                    "Instalar biblioteca recomendada",
                    "Se reemplazará la paleta global de colores y se crearán o restaurarán los " +
                    "perfiles recomendados. Los GUID de assets existentes se conservarán.",
                    "Instalar", "Cancelar"))
                return;

            if (!VoxelPaletteAssetUtility.TryInstallRecommendedColorLibrary(
                    out colorPalette, out VoxelColorMappingProfile[] profiles, out string error))
            {
                EditorUtility.DisplayDialog("Voxel Bridge", error, "Cerrar");
                return;
            }
            Selection.objects = new UnityEngine.Object[] { colorPalette }
                .Concat(profiles).ToArray();
            Repaint();
        }

        private void RebuildBoth()
        {
            if (colorPalette == null || surfacePalette == null)
            {
                EditorUtility.DisplayDialog(
                    "Voxel Bridge", "Asigna ambas paletas antes de regenerar las LUT.", "Cerrar");
                return;
            }
            bool colorOk = VoxelPaletteLutGenerator.TryRebuild(
                colorPalette, out _, out string colorError);
            bool surfaceOk = VoxelPaletteLutGenerator.TryRebuild(
                surfacePalette, out _, out string surfaceError);
            if (!colorOk || !surfaceOk)
            {
                EditorUtility.DisplayDialog(
                    "Voxel Bridge", colorError ?? surfaceError, "Cerrar");
            }
            Repaint();
        }

        private static void Rebuild(VoxelColorPalette palette)
        {
            if (!VoxelPaletteLutGenerator.TryRebuild(palette, out Texture2D texture, out string error))
                EditorUtility.DisplayDialog("Voxel Bridge", error, "Cerrar");
            else
                EditorGUIUtility.PingObject(texture);
        }

        private static void Rebuild(VoxelSurfacePalette palette)
        {
            if (!VoxelPaletteLutGenerator.TryRebuild(palette, out Texture2D texture, out string error))
                EditorUtility.DisplayDialog("Voxel Bridge", error, "Cerrar");
            else
                EditorGUIUtility.PingObject(texture);
        }

        private static void Select(UnityEngine.Object target)
        {
            Selection.activeObject = target;
            EditorGUIUtility.PingObject(target);
        }

        private void LoadDefaultsIfAvailable()
        {
            colorPalette ??= AssetDatabase.LoadAssetAtPath<VoxelColorPalette>(
                VoxelPaletteAssetUtility.ColorPalettePath);
            surfacePalette ??= AssetDatabase.LoadAssetAtPath<VoxelSurfacePalette>(
                VoxelPaletteAssetUtility.SurfacePalettePath);
        }
    }

    public static class VoxelPaletteAssetUtility
    {
        internal const string PaletteFolder = "Assets/VoxelBridge/Palettes";
        internal const string ColorPalettePath = PaletteFolder + "/VoxelColorPalette.asset";
        internal const string SurfacePalettePath = PaletteFolder + "/VoxelSurfacePalette.asset";

        [MenuItem("Assets/Create/Voxel Bridge/Crear paletas globales canónicas", false, 301)]
        private static void CreateCanonicalAssetsMenu()
        {
            if (!TryEnsureCanonicalAssets(
                    out VoxelColorPalette color, out VoxelSurfacePalette surface, out string error))
            {
                EditorUtility.DisplayDialog("Voxel Bridge", error, "Cerrar");
                return;
            }
            Selection.objects = new UnityEngine.Object[] { color, surface };
        }

        internal static bool TryEnsureCanonicalAssets(out VoxelColorPalette colorPalette,
            out VoxelSurfacePalette surfacePalette, out string error)
        {
            VoxelPaletteLutGenerator.EnsureAssetFolder(PaletteFolder);
            colorPalette = AssetDatabase.LoadAssetAtPath<VoxelColorPalette>(ColorPalettePath);
            surfacePalette = AssetDatabase.LoadAssetAtPath<VoxelSurfacePalette>(SurfacePalettePath);

            if (colorPalette == null)
            {
                colorPalette = ScriptableObject.CreateInstance<VoxelColorPalette>();
                AssetDatabase.CreateAsset(colorPalette, ColorPalettePath);
            }
            if (surfacePalette == null)
            {
                surfacePalette = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
                AssetDatabase.CreateAsset(surfacePalette, SurfacePalettePath);
            }

            if (colorPalette.EnsureInitialized()) EditorUtility.SetDirty(colorPalette);
            if (surfacePalette.EnsureInitialized()) EditorUtility.SetDirty(surfacePalette);

            if (!VoxelPaletteLutGenerator.TryRebuild(colorPalette, out _, out error)) return false;
            if (!VoxelPaletteLutGenerator.TryRebuild(surfacePalette, out _, out error)) return false;
            AssetDatabase.SaveAssets();
            error = null;
            return true;
        }

        internal static bool TryInstallRecommendedColorLibrary(
            out VoxelColorPalette colorPalette,
            out VoxelColorMappingProfile[] profiles, out string error)
        {
            profiles = null;
            if (!TryEnsureCanonicalAssets(
                    out colorPalette, out _, out error))
                return false;

            Undo.RecordObject(colorPalette, "Instalar biblioteca de colores recomendada");
            colorPalette.ResetToRecommended();
            EditorUtility.SetDirty(colorPalette);
            if (!VoxelRecommendedColorProfiles.TryCreateOrResetAssets(
                    colorPalette, replaceExisting: true, out profiles, out error))
                return false;
            if (!VoxelPaletteLutGenerator.TryRebuild(colorPalette, out _, out error))
                return false;
            AssetDatabase.SaveAssets();
            return true;
        }

        public static void ApplyRecommendedColorLibrary()
        {
            if (!TryInstallRecommendedColorLibrary(
                    out _, out VoxelColorMappingProfile[] profiles, out string error))
                throw new System.InvalidOperationException(error);
            Debug.Log($"Voxel Bridge instaló la biblioteca maestra y {profiles.Length} perfiles de color.");
        }
    }
}
