using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

namespace LocalModels.VoxelBridge
{
    internal static class VoxelBlockSpomLab
    {
        internal const string ShaderPath = "Assets/VoxelBridge/Shaders/VoxelWorldBlockSpom.shadergraph";

        [MenuItem("Tools/Voxel Bridge/Experiments/Create SPOM Block Lab", false, 160)]
        static void CreateAndLocate()
        {
            string path = CreateLab();
            // Do not enter Prefab Mode automatically: interactive rendering needs a separate stability check.
            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(path));
        }

        // Creates only new, locally ignored experiment assets. No global palette or scene is saved.
        internal static string CreateLab()
        {
            var spomShader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            var pomShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/VoxelBridge/Shaders/VoxelWorldOpaquePomDepth.shadergraph");
            if (!spomShader || !pomShader || ShaderUtil.ShaderHasError(spomShader) || ShaderUtil.ShaderHasError(pomShader))
                throw new InvalidOperationException("Both experimental shaders must import without errors before creating the lab.");
            const string parent = "Assets/VoxelBridge/Prototypes";
            if (!AssetDatabase.IsValidFolder(parent)) AssetDatabase.CreateFolder("Assets/VoxelBridge", "Prototypes");
            string folder = AssetDatabase.GenerateUniqueAssetPath(parent + "/BlockSpomLab");
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(folder));

            var palette = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
            palette.name = "BlockSurfaces";
            palette.MutableEntries.Clear();
            palette.MutableEntries.Add(new VoxelSurfaceDefinition(0, "Default", VoxelSurfaceRenderClass.Opaque,
                0, .3f, 0, 1, cellHeightVariation: .16f));
            AssetDatabase.CreateAsset(palette, folder + "/BlockSurfaces.asset");
            if (!VoxelPaletteLutGenerator.TryRebuild(palette, out var surfaces, out string error))
                throw new InvalidOperationException(error);
            var colors = new Texture2D(256, 1, TextureFormat.RGBA32, false, false)
                { name = "BlockColors", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            colors.SetPixels(Enumerable.Repeat(new Color(.65f, .58f, .45f, 1), 256).ToArray());
            colors.Apply();
            AssetDatabase.CreateAsset(colors, folder + "/BlockColors.asset");
            var mesh = UnityEngine.Object.Instantiate(Resources.GetBuiltinResource<Mesh>("Cube.fbx"));
            mesh.name = "UnitCubeSemantic";
            mesh.uv4 = Enumerable.Repeat(Vector2.zero, mesh.vertexCount).ToArray();
            AssetDatabase.CreateAsset(mesh, folder + "/UnitCubeSemantic.asset");

            var previewScene = EditorSceneManager.NewPreviewScene();
            try
            {
                var root = new GameObject("SPOM Block Lab");
                SceneManager.MoveGameObjectToScene(root, previewScene);
                for (int i = 0; i < 2; i++)
                {
                    var material = new Material(i == 0 ? pomShader : spomShader) { name = i == 0 ? "POM_Depth" : "SPOM_Block" };
                    material.SetTexture("_PaletteColor", colors);
                    material.SetTexture("_PaletteSurface", surfaces);
                    material.SetFloat("_EmissionIntensity", 0);
                    material.SetFloat("_GridEnabled", 1);
                    material.SetFloat("_GridAnchor", 1);
                    material.SetFloat("_GridCellSize", .0625f);
                    material.SetFloat("_GridMultiscale", 0);
                    material.SetFloat("_GridProfile", 1);
                    material.SetFloat("_GridJointWidth", .04f);
                    material.SetFloat("_GridBevelWidth", .04f);
                    material.SetFloat("_GridJointDepth", .025f);
                    material.SetFloat("_GridNormalStrength", 1);
                    material.SetFloat("_GridPomEnabled", 1);
                    material.SetFloat("_GridPomMaxDepth", .01f);
                    material.SetFloat("_DepthOffsetEnable", 1);
                    material.SetFloat("_ConservativeDepthOffsetEnable", 1);
                    HDMaterial.ValidateMaterial(material);
                    AssetDatabase.CreateAsset(material, folder + "/" + material.name + ".mat");
                    var block = new GameObject(i == 0 ? "01_POM_Depth_Reference" : "02_SPOM_ClosedVolume");
                    block.transform.SetParent(root.transform, false);
                    block.transform.localPosition = new Vector3(i == 0 ? -.4f : .4f, .25f, 0);
                    block.transform.localScale = Vector3.one * .5f;
                    block.AddComponent<MeshFilter>().sharedMesh = mesh;
                    block.AddComponent<MeshRenderer>().sharedMaterial = material;
                }
                string path = folder + "/SPOM_Block_Lab.prefab";
                if (!PrefabUtility.SaveAsPrefabAsset(root, path)) throw new InvalidOperationException("Could not save the SPOM lab prefab.");
                return path;
            }
            finally { EditorSceneManager.ClosePreviewScene(previewScene); }
        }
    }
}
