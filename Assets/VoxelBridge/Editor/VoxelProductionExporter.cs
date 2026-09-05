using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge
{
    internal static class VoxelProductionExporter
    {
        internal const string ShaderPath = "Assets/VoxelBridge/Shaders/VoxelWorldOpaque.shadergraph";
        internal const string SharedMaterialFolder = "Assets/VoxelBridgeImports/SharedMaterials";

        public static GameObject Export(string voxAssetPath, string outputFolder, Action<float> progress = null)
        {
            if (!VoxelLodPipeline.IsAssetFolder(outputFolder))
                throw new ArgumentException("Choose an output folder under Assets.", nameof(outputFolder));
            if (!VoxelImporterIntegration.TryLoadMetadata(voxAssetPath, out VoxelBridgeMetadata metadata, out string error))
                throw new InvalidDataException(error);
            if (metadata.formatVersion != 4 || metadata.lodIndex != 0)
                throw new InvalidDataException("Production export requires a semantic LOD0. Save its ColorID and SurfaceID bindings first.");
            VoxelSemanticMesher.ValidateGrid(metadata.unityGridSize, metadata.gridOrigin, metadata.voxelSize);
            string absolutePath = VoxelLodPipeline.AssetPathToAbsolute(voxAssetPath);
            if (new FileInfo(absolutePath).Length > 64L * 1024 * 1024)
                throw new InvalidDataException("The VOX file exceeds the standalone reader's 64 MiB safety limit.");
            if (!VoxelSemanticTransport.TryLoadPalettes(metadata.semantic,
                    out VoxelColorPalette colors, out VoxelSurfacePalette surfaces, out error))
                throw new InvalidDataException(error);
            ValidatePalettes(colors, surfaces);
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null || !shader.isSupported || ShaderUtil.ShaderHasError(shader))
                throw new InvalidOperationException("The Voxel Bridge HDRP shader is missing, unsupported, or has compilation errors.");

            VoxelGrid grid = VoxelVolumeReader.Read(absolutePath, metadata);
            ValidateSurfaces(grid, surfaces);
            Mesh mesh = null;
            GameObject root = null;
            string createdFolder = null;
            string createdMaterial = null;
            bool saved = false;
            try
            {
                mesh = VoxelSemanticMesher.Build(grid, metadata.hideInternalCavities, progress);
                // All source validation and meshing complete before creating any output assets.
                Material material = GetSharedMaterial(colors, surfaces, shader, out createdMaterial);
                VoxelLodPipeline.EnsureAssetFolder(outputFolder);
                string name = VoxelLodPipeline.MakeSafeFileName(Path.GetFileNameWithoutExtension(voxAssetPath));
                createdFolder = AssetDatabase.GenerateUniqueAssetPath($"{outputFolder}/{name}_Production");
                VoxelLodPipeline.EnsureAssetFolder(createdFolder);
                mesh.name = name + "_Mesh";
                AssetDatabase.CreateAsset(mesh, createdFolder + "/LOD0.asset");
                root = new GameObject(string.IsNullOrWhiteSpace(metadata.sourceName) ? name : metadata.sourceName);
                root.AddComponent<MeshFilter>().sharedMesh = mesh;
                root.AddComponent<MeshRenderer>().sharedMaterial = material;
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, createdFolder + "/" + name + ".prefab");
                if (prefab == null) throw new IOException("Could not save the production prefab.");
                AssetDatabase.SaveAssets();
                saved = true;
                return prefab;
            }
            finally
            {
                if (root != null) Object.DestroyImmediate(root);
                if (!saved)
                {
                    if (createdFolder != null) AssetDatabase.DeleteAsset(createdFolder);
                    if (createdMaterial != null) AssetDatabase.DeleteAsset(createdMaterial);
                }
                if (mesh != null && !AssetDatabase.Contains(mesh)) Object.DestroyImmediate(mesh);
            }
        }

        internal static void ValidatePalettes(VoxelColorPalette colors, VoxelSurfacePalette surfaces)
        {
            if (colors == null || surfaces == null)
                throw new InvalidDataException("Both global palettes are required.");
            if (!colors.TryValidate(out string colorError)) throw new InvalidDataException(colorError);
            if (!surfaces.TryValidate(out string surfaceError)) throw new InvalidDataException(surfaceError);
            if (!VoxelPaletteLutGenerator.IsCurrent(colors) || !VoxelPaletteLutGenerator.IsCurrent(surfaces))
                throw new InvalidDataException("A palette LUT is missing, outdated, or incorrectly configured. Rebuild both LUTs in Global Palettes before exporting.");
        }

        internal static void ValidateSurfaces(VoxelGrid grid, VoxelSurfacePalette surfaces)
        {
            var seen = new bool[256];
            for (int i = 0; i < grid.Occupied.Length; i++)
            {
                if (!grid.Occupied[i]) continue;
                int id = VoxelSemanticEncoding.SurfaceId(grid.SemanticIds[i]);
                if (seen[id]) continue;
                seen[id] = true;
                if (!surfaces.TryGetSurface(id, out VoxelSurfaceDefinition surface))
                    throw new InvalidDataException($"SurfaceID {id} does not exist.");
                if (surface.RenderClass != VoxelSurfaceRenderClass.Opaque)
                    throw new InvalidDataException($"SurfaceID {id} ({surface.DisplayName}) uses {surface.RenderClass}. Standalone production export supports only Opaque surfaces.");
            }
        }

        private static Material GetSharedMaterial(VoxelColorPalette colors, VoxelSurfacePalette surfaces,
            Shader shader, out string createdPath)
        {
            createdPath = null;
            string key = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(colors)) + ":" +
                         AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(surfaces));
            string path = $"{SharedMaterialFolder}/VoxelWorld_{Hash128.Compute(key)}.mat";
            if (!VoxelPaletteLutGenerator.TryValidateAssetType<Material>(path, out string error))
                throw new InvalidDataException(error);
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null)
            {
                if (material.shader != shader || material.GetTexture("_PaletteColor") != colors.GeneratedLut ||
                    material.GetTexture("_PaletteSurface") != surfaces.GeneratedLut)
                    throw new InvalidDataException("The shared production material has incompatible shader or LUT references. Restore its references before exporting.");
                return material;
            }
            VoxelLodPipeline.EnsureAssetFolder(SharedMaterialFolder);
            material = new Material(shader) { name = "VoxelWorldOpaque", enableInstancing = true };
            try
            {
                material.SetTexture("_PaletteColor", colors.GeneratedLut);
                material.SetTexture("_PaletteSurface", surfaces.GeneratedLut);
                material.SetFloat("_EmissionIntensity", 1f);
                AssetDatabase.CreateAsset(material, path);
                createdPath = path;
                return material;
            }
            catch { if (!AssetDatabase.Contains(material)) Object.DestroyImmediate(material); throw; }
        }
    }
}
