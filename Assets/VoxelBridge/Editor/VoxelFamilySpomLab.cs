using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge
{
    internal static class VoxelFamilySpomData
    {
        // 32 MiB RGBA8 ceiling; the normal production grid validation remains authoritative too.
        internal static Color32[] BuildPixels(VoxelGrid grid, VoxelSurfacePalette palette)
        {
            if (grid == null || !grid.IsSemantic || palette == null) throw new ArgumentException("A semantic grid and surface palette are required.");
            VoxelSemanticMesher.ValidateGrid(grid.Size, grid.Origin, grid.VoxelSize);
            if (grid.Size.x > 256 || grid.Size.y > 256 || grid.Size.z > 256)
                throw new InvalidDataException("The family SPOM prototype supports at most 256 cells per axis.");
            if (!palette.TryValidate(out string error)) throw new InvalidDataException(error);
            var opaque = new bool[256];
            var known = new bool[256];
            foreach (var entry in palette.Entries)
            {
                known[entry.Id] = true;
                opaque[entry.Id] = entry.RenderClass == VoxelSurfaceRenderClass.Opaque;
            }
            var pixels = new Color32[grid.Occupied.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                if (!grid.Occupied[i]) continue;
                int surface = VoxelSemanticEncoding.SurfaceId(grid.SemanticIds[i]);
                if (!known[surface]) throw new InvalidDataException($"Unknown SurfaceID {surface}.");
                if (opaque[surface]) pixels[i] = new Color32((byte)VoxelSemanticEncoding.ColorId(grid.SemanticIds[i]), (byte)surface, 0, 255);
            }
            for (int z = 0; z < grid.Size.z; z++)
                for (int y = 0; y < grid.Size.y; y++)
                    for (int x = 0; x < grid.Size.x; x++)
                    {
                        int i = grid.Index(x, y, z);
                        if (pixels[i].a == 0) continue;
                        byte mask = 0;
                        if (x == 0 || pixels[i - 1].a == 0) mask |= 1;
                        if (x == grid.Size.x - 1 || pixels[i + 1].a == 0) mask |= 2;
                        if (y == 0 || pixels[i - grid.Size.x].a == 0) mask |= 4;
                        if (y == grid.Size.y - 1 || pixels[i + grid.Size.x].a == 0) mask |= 8;
                        int plane = grid.Size.x * grid.Size.y;
                        if (z == 0 || pixels[i - plane].a == 0) mask |= 16;
                        if (z == grid.Size.z - 1 || pixels[i + plane].a == 0) mask |= 32;
                        pixels[i].b = mask;
                    }
            return pixels;
        }
    }

    internal static class VoxelFamilySpomLab
    {
        internal const string ShaderPath = "Assets/VoxelBridge/Shaders/VoxelWorldFamilySpom.shadergraph";

        [MenuItem("Tools/Voxel Bridge/Experiments/Create Family SPOM Copy", false, 161)]
        static void CreateSelected()
        {
            var selected = Selection.activeGameObject;
            var group = selected ? selected.GetComponentInParent<LODGroup>() : null;
            if (!group || EditorApplication.isPlaying || PrefabStageUtility.GetCurrentPrefabStage() != null)
                throw new InvalidOperationException("Select a production family or one of its chunks in the scene, outside Play and Prefab Mode.");
            if (!EditorUtility.IsPersistent(group) && !group.gameObject.activeInHierarchy)
                throw new InvalidOperationException("Restore the active original before creating another comparison copy.");
            ValidateFrame(group.transform.localToWorldMatrix);
            var renderer = selected.GetComponent<Renderer>();
            Material style = renderer && renderer.sharedMaterials.Length > 0 ? renderer.sharedMaterials[0] : null;
            string path = CreateLab(VoxelProductionLink.PrefabPath(group.gameObject), style);
            if (EditorUtility.IsPersistent(group)) { EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(path)); return; }
            var source = group.gameObject;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Preview family SPOM on a copy");
            var copy = (GameObject)PrefabUtility.InstantiatePrefab(prefab, source.scene);
            Undo.RegisterCreatedObjectUndo(copy, "Create family SPOM copy");
            copy.transform.SetParent(source.transform.parent, false);
            copy.transform.localPosition = source.transform.localPosition;
            copy.transform.localRotation = source.transform.localRotation;
            copy.transform.localScale = source.transform.localScale;
            Undo.RecordObject(source, "Hide original during SPOM comparison");
            source.SetActive(false);
            Undo.CollapseUndoOperations(undoGroup);
            Selection.activeGameObject = copy;
            // The scene is intentionally not saved; Undo restores the original in one operation.
        }

        internal static void ValidateFrame(Matrix4x4 matrix)
        {
            Vector3 x = matrix.GetColumn(0), y = matrix.GetColumn(1), z = matrix.GetColumn(2);
            float deviation = Mathf.Abs(x.magnitude - 1) + Mathf.Abs(y.magnitude - 1) + Mathf.Abs(z.magnitude - 1)
                + Mathf.Abs(Vector3.Dot(x, y)) + Mathf.Abs(Vector3.Dot(x, z)) + Mathf.Abs(Vector3.Dot(y, z));
            if (!float.IsFinite(deviation) || deviation > .0001f || Vector3.Dot(Vector3.Cross(x, y), z) < .9999f)
                throw new InvalidDataException("Family SPOM requires normalized scale 1 with no reflection or shear, including parent transforms.");
        }

        // Fresh assets only; the VOX, original prefab, palettes, and other families are never rewritten.
        internal static string CreateLab(string sourcePrefabPath, Material style = null)
        {
            var link = VoxelProductionLink.Load(sourcePrefabPath);
            var grid = VoxelProductionExporter.ReadGrid(link.SourcePath, out var metadata, out var colors, out var surfaces, out _);
            if (metadata.lodIndex != 0) throw new InvalidDataException("The experiment requires the family's LOD0 source.");
            if (!SystemInfo.supports3DTextures || Mathf.Max(grid.Size.x, grid.Size.y, grid.Size.z) > SystemInfo.maxTexture3DSize)
                throw new InvalidOperationException("The device cannot hold this 3D occupancy texture.");
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (!shader || !shader.isSupported || ShaderUtil.ShaderHasError(shader)) throw new InvalidOperationException("The family SPOM shader must compile first.");
            var pixels = VoxelFamilySpomData.BuildPixels(grid, surfaces);
            var sourcePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePrefabPath);
            var sourceGlass = sourcePrefab.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials)
                .FirstOrDefault(m => m && m.shader.name == "Voxel Bridge/VoxelGlass");
            Mesh[] meshes = VoxelSemanticMesher.BuildChunks(grid, Mathf.Clamp(metadata.chunkCellSize, 16, 256), metadata.hideInternalCavities, palette: surfaces);
            var preview = EditorSceneManager.NewPreviewScene();
            try
            {
                if (meshes.Any(m => m.subMeshCount > 1) && !sourceGlass) throw new InvalidDataException("The family has glass but its glass material is missing.");
                const string parent = "Assets/VoxelBridge/Prototypes";
                VoxelLodPipeline.EnsureAssetFolder(parent);
                string folder = AssetDatabase.GenerateUniqueAssetPath(parent + "/" + VoxelLodPipeline.MakeSafeFileName(metadata.sourceName) + "_FamilySpom");
                VoxelLodPipeline.EnsureAssetFolder(folder);
                var colorCopy = Object.Instantiate(colors); colorCopy.name = "FamilyColors"; colorCopy.SetGeneratedLut(null, null);
                var surfaceCopy = Object.Instantiate(surfaces); surfaceCopy.name = "FamilySurfaces"; surfaceCopy.SetGeneratedLut(null, null);
                AssetDatabase.CreateAsset(colorCopy, folder + "/FamilyColors.asset");
                AssetDatabase.CreateAsset(surfaceCopy, folder + "/FamilySurfaces.asset");
                if (!VoxelPaletteLutGenerator.TryRebuild(colorCopy, out var colorLut, out string error) ||
                    !VoxelPaletteLutGenerator.TryRebuild(surfaceCopy, out var surfaceLut, out error)) throw new InvalidOperationException(error);
                var volume = new Texture3D(grid.Size.x, grid.Size.y, grid.Size.z,
                    UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm,
                    UnityEngine.Experimental.Rendering.TextureCreationFlags.None)
                { name = "FamilyOccupancy", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                volume.SetPixels32(pixels); volume.Apply(false, false);
                AssetDatabase.CreateAsset(volume, folder + "/FamilyOccupancy.asset");
                var material = new Material(shader) { name = "FamilySPOM" };
                material.SetTexture("_SpomVolume", volume);
                material.SetVector("_SpomOrigin", grid.Origin);
                material.SetVector("_SpomSize", (Vector3)grid.Size);
                material.SetFloat("_SpomVoxelSize", grid.VoxelSize);
                material.SetTexture("_PaletteColor", colorLut); material.SetTexture("_PaletteSurface", surfaceLut);
                material.SetFloat("_GridEnabled", 1); material.SetFloat("_GridPomEnabled", 1);
                material.SetFloat("_GridJointWidth", .04f); material.SetFloat("_GridBevelWidth", .04f);
                material.SetFloat("_GridJointDepth", .025f); material.SetFloat("_GridPomMaxDepth", .01f);
                foreach (string property in new[] { "_GridJointWidth", "_GridBevelWidth", "_GridJointDepth", "_GridPomMaxDepth", "_GridRoughnessStrength", "_EmissionIntensity" })
                    if (style && style.HasProperty(property)) material.SetFloat(property, style.GetFloat(property));
                material.SetFloat("_DepthOffsetEnable", 1); material.SetFloat("_ConservativeDepthOffsetEnable", 1);
                HDMaterial.ValidateMaterial(material);
                AssetDatabase.CreateAsset(material, folder + "/FamilySPOM.mat");
                Material glass = null;
                if (sourceGlass)
                {
                    glass = new Material(sourceGlass) { name = "FamilyGlass" };
                    glass.SetTexture("_PaletteColor", colorLut); glass.SetTexture("_PaletteSurface", surfaceLut);
                    AssetDatabase.CreateAsset(glass, folder + "/FamilyGlass.mat");
                }
                var root = new GameObject(metadata.sourceName + "_SPOM_Test_LOD0");
                SceneManager.MoveGameObjectToScene(root, preview);
                foreach (var mesh in meshes)
                {
                    if (mesh.vertexCount == 0) continue;
                    AssetDatabase.CreateAsset(mesh, folder + "/" + mesh.name + ".asset");
                    var chunk = new GameObject(mesh.name); chunk.transform.SetParent(root.transform, false);
                    chunk.AddComponent<MeshFilter>().sharedMesh = mesh;
                    chunk.AddComponent<MeshRenderer>().sharedMaterials = mesh.subMeshCount > 1 ? new[] { material, glass } : new[] { material };
                }
                VoxelRetainedGeometry.Attach(root.transform, metadata.retainedGeometryGuid);
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                    renderer.rayTracingMode = UnityEngine.Experimental.Rendering.RayTracingMode.Off;
                string path = folder + "/" + root.name + ".prefab";
                if (!PrefabUtility.SaveAsPrefabAsset(root, path)) throw new IOException("Could not save the family SPOM copy.");
                return path;
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
                foreach (var mesh in meshes) if (mesh && !AssetDatabase.Contains(mesh)) Object.DestroyImmediate(mesh);
            }
        }
    }
}
