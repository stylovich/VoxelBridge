using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    internal static class VoxelPaletteLutBuilder
    {
        public static bool TryBuildColorPixels(VoxelColorPalette palette,
            out Color32[] pixels, out string contentHash, out string error)
        {
            pixels = null;
            contentHash = null;
            if (palette == null)
            {
                error = "The color palette is not assigned.";
                return false;
            }
            if (!palette.TryValidate(out error)) return false;
            if (!palette.TryGetColor(VoxelPaletteConstants.DefaultId, out Color32 fallback))
            {
                error = "Could not resolve ColorID 0.";
                return false;
            }

            pixels = CreateFilledPixels(fallback);
            foreach (VoxelColorDefinition entry in palette.Entries)
                pixels[entry.Id] = entry.Color;
            contentHash = ComputeHash("color-v1", pixels, null);
            error = null;
            return true;
        }

        public static bool TryBuildSurfacePixels(VoxelSurfacePalette palette,
            out Color32[] pixels, out string contentHash, out string error)
        {
            pixels = null;
            contentHash = null;
            if (palette == null)
            {
                error = "The surface palette is not assigned.";
                return false;
            }
            if (!palette.TryValidate(out error)) return false;
            if (!palette.TryGetSurface(VoxelPaletteConstants.DefaultId,
                    out VoxelSurfaceDefinition fallback))
            {
                error = "Could not resolve SurfaceID 0.";
                return false;
            }

            Color32 fallbackPixel = EncodeSurface(fallback);
            pixels = CreateFilledPixels(fallbackPixel);
            var renderClasses = new byte[VoxelPaletteConstants.EntryCount];
            byte fallbackRenderClass = (byte)fallback.RenderClass;
            for (int i = 0; i < renderClasses.Length; i++)
                renderClasses[i] = fallbackRenderClass;

            foreach (VoxelSurfaceDefinition entry in palette.Entries)
            {
                pixels[entry.Id] = EncodeSurface(entry);
                renderClasses[entry.Id] = (byte)entry.RenderClass;
            }

            contentHash = ComputeHash("surface-v1", pixels, renderClasses);
            error = null;
            return true;
        }

        internal static Color32 EncodeSurface(VoxelSurfaceDefinition surface) => new(
            EncodeUnit(surface.Metallic),
            EncodeUnit(surface.Smoothness),
            EncodeUnit(surface.Emission),
            // Glass uses alpha for opacity; opaque encoding and its existing LUT stay unchanged.
            EncodeUnit(surface.RenderClass == VoxelSurfaceRenderClass.Transparent ? surface.Opacity : surface.OcclusionMultiplier));

        private static Color32[] CreateFilledPixels(Color32 value)
        {
            var pixels = new Color32[VoxelPaletteConstants.EntryCount];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = value;
            return pixels;
        }

        private static byte EncodeUnit(float value) =>
            (byte)Mathf.RoundToInt(Mathf.Clamp01(value) * byte.MaxValue);

        private static string ComputeHash(string prefix, IReadOnlyList<Color32> pixels,
            IReadOnlyList<byte> renderClasses)
        {
            var text = new StringBuilder(prefix, prefix.Length + pixels.Count * 10);
            foreach (Color32 pixel in pixels)
            {
                text.Append(pixel.r.ToString("X2"));
                text.Append(pixel.g.ToString("X2"));
                text.Append(pixel.b.ToString("X2"));
                text.Append(pixel.a.ToString("X2"));
            }
            if (renderClasses != null)
            {
                text.Append(':');
                foreach (byte renderClass in renderClasses)
                    text.Append(renderClass.ToString("X2"));
            }
            return Hash128.Compute(text.ToString()).ToString();
        }
    }

    internal static class VoxelPaletteLutGenerator
    {
        public static bool IsCurrent(VoxelColorPalette palette)
        {
            if (!VoxelPaletteLutBuilder.TryBuildColorPixels(
                    palette, out Color32[] pixels, out string hash, out _))
                return false;
            return IsCurrent(palette.GeneratedLut, palette.GeneratedContentHash,
                hash, pixels, expectSrgb: true);
        }

        public static bool IsCurrent(VoxelSurfacePalette palette)
        {
            if (!VoxelPaletteLutBuilder.TryBuildSurfacePixels(
                    palette, out Color32[] pixels, out string hash, out _))
                return false;
            return IsCurrent(palette.GeneratedLut, palette.GeneratedContentHash,
                hash, pixels, expectSrgb: false);
        }

        public static bool TryRebuild(VoxelColorPalette palette,
            out Texture2D texture, out string error)
        {
            texture = null;
            if (!VoxelPaletteLutBuilder.TryBuildColorPixels(
                    palette, out Color32[] pixels, out string hash, out error))
                return false;
            if (!TryCreateOrUpdateTexture(palette, pixels, linear: false,
                    out texture, out error))
                return false;

            Undo.RecordObject(palette, "Rebuild Voxel Color LUT");
            palette.SetGeneratedLut(texture, hash);
            EditorUtility.SetDirty(palette);
            AssetDatabase.SaveAssets();
            return true;
        }

        public static bool TryRebuild(VoxelSurfacePalette palette,
            out Texture2D texture, out string error)
        {
            texture = null;
            if (!VoxelPaletteLutBuilder.TryBuildSurfacePixels(
                    palette, out Color32[] pixels, out string hash, out error))
                return false;
            if (!TryCreateOrUpdateTexture(palette, pixels, linear: true,
                    out texture, out error))
                return false;

            Undo.RecordObject(palette, "Rebuild Voxel Surface LUT");
            palette.SetGeneratedLut(texture, hash);
            EditorUtility.SetDirty(palette);
            AssetDatabase.SaveAssets();
            return true;
        }

        private static bool TryCreateOrUpdateTexture(UnityEngine.Object palette,
            Color32[] pixels, bool linear, out Texture2D texture, out string error)
        {
            texture = null;
            string palettePath = AssetDatabase.GetAssetPath(palette);
            if (string.IsNullOrEmpty(palettePath))
            {
                error = "Save the palette as an asset before generating its LUT.";
                return false;
            }

            string texturePath = GetGeneratedTextureAssetPath(palettePath, palette.name);
            if (!TryValidateAssetType<Texture2D>(texturePath, out error)) return false;
            EnsureAssetFolder(Path.GetDirectoryName(texturePath)?.Replace('\\', '/'));
            Texture2D existing = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            Texture2D generated = CreateTexture(palette.name + "_LUT", pixels, linear);

            if (existing == null)
            {
                AssetDatabase.CreateAsset(generated, texturePath);
                texture = generated;
            }
            else
            {
                Undo.RecordObject(existing, "Rebuild Voxel LUT");
                EditorUtility.CopySerialized(generated, existing);
                UnityEngine.Object.DestroyImmediate(generated);
                EditorUtility.SetDirty(existing);
                texture = existing;
            }

            error = null;
            return true;
        }

        internal static string GetGeneratedTextureAssetPath(string palettePath, string paletteName) =>
            Path.GetDirectoryName(palettePath)?.Replace('\\', '/') + "/Generated/" + paletteName + "_LUT.asset";

        internal static bool TryValidateAssetType<T>(string assetPath, out string error)
            where T : UnityEngine.Object
        {
            UnityEngine.Object existing = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (existing != null && existing is T)
            {
                error = null;
                return true;
            }

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            string absolutePath = Path.Combine(projectRoot ?? string.Empty, assetPath);
            if (existing != null || File.Exists(absolutePath) || Directory.Exists(absolutePath))
            {
                error = $"'{assetPath}' already exists and is not a readable {typeof(T).Name} asset. " +
                        "Move or rename the conflicting asset before continuing.";
                return false;
            }
            error = null;
            return true;
        }

        private static Texture2D CreateTexture(string textureName, Color32[] pixels, bool linear)
        {
            var texture = new Texture2D(
                VoxelPaletteConstants.EntryCount, 1, TextureFormat.RGBA32,
                mipChain: false, linear: linear)
            {
                name = textureName,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0
            };
            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            return texture;
        }

        private static bool IsCurrent(Texture2D texture, string storedHash,
            string expectedHash, IReadOnlyList<Color32> expectedPixels, bool expectSrgb)
        {
            if (texture == null || string.IsNullOrEmpty(storedHash) ||
                !string.Equals(storedHash, expectedHash, StringComparison.Ordinal))
                return false;
            if (texture.width != VoxelPaletteConstants.EntryCount || texture.height != 1 ||
                texture.format != TextureFormat.RGBA32 || texture.mipmapCount != 1 ||
                texture.filterMode != FilterMode.Point || texture.wrapMode != TextureWrapMode.Clamp ||
                texture.anisoLevel != 0 || texture.isDataSRGB != expectSrgb || !texture.isReadable)
                return false;

            Color32[] currentPixels = texture.GetPixels32();
            if (currentPixels.Length != expectedPixels.Count) return false;
            for (int i = 0; i < currentPixels.Length; i++)
            {
                if (!currentPixels[i].Equals(expectedPixels[i])) return false;
            }
            return true;
        }

        internal static void EnsureAssetFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;

            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            string name = Path.GetFileName(folder);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
                throw new InvalidOperationException($"Invalid asset folder path: {folder}");
            EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }
    }
}
