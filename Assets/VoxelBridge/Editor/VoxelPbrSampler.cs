using System;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    // HDRP/Lit Standard only. Matches LitDataIndividualLayer: Mask R/A remaps replace constants.
    internal sealed class VoxelPbrSampler : IDisposable
    {
        private readonly float metallic, smoothness, metallicMin, metallicMax, smoothnessMin, smoothnessMax;
        private readonly Vector2 scale = Vector2.one, offset;
        private readonly TextureWrapMode wrapU, wrapV;
        private readonly bool point;
        private readonly int width, height;
        private Color32[] pixels;
        internal string UnsupportedReason { get; private set; }

        internal VoxelPbrSampler(Material material)
        {
            if (material == null || material.shader == null || material.shader.name != "HDRP/Lit")
            { UnsupportedReason = "PBR inference supports HDRP/Lit only."; return; }
            if (material.GetFloat("_MaterialID") != 1 || material.GetFloat("_SurfaceType") != 0 ||
                material.GetFloat("_CoatMask") != 0 || material.IsKeywordEnabled("_MATERIAL_FEATURE_CLEAR_COAT") ||
                material.IsKeywordEnabled("_DETAIL_MAP") || material.IsKeywordEnabled("_PIXEL_DISPLACEMENT") ||
                material.IsKeywordEnabled("_VERTEX_DISPLACEMENT") || material.IsKeywordEnabled("_ENABLE_GEOMETRIC_SPECULAR_AA"))
            { UnsupportedReason = "PBR inference requires opaque Standard Lit without coat, detail, displacement or geometric specular AA."; return; }
            foreach (string keyword in new[] { "_SURFACE_TYPE_TRANSPARENT", "_MATERIAL_FEATURE_ANISOTROPY", "_MATERIAL_FEATURE_IRIDESCENCE",
                "_MATERIAL_FEATURE_SPECULAR_COLOR", "_MATERIAL_FEATURE_TRANSMISSION", "_MATERIAL_FEATURE_SUBSURFACE_SCATTERING" })
                if (material.IsKeywordEnabled(keyword)) { UnsupportedReason = "Unsupported PBR shader variant: " + keyword; return; }
            Color emissive = material.GetColor("_EmissiveColor");
            if (!float.IsFinite(emissive.r) || !float.IsFinite(emissive.g) || !float.IsFinite(emissive.b))
            { UnsupportedReason = "Nonfinite emission values; use a valid material or an explicit SurfaceID."; return; }
            if (Mathf.Max(emissive.r, Mathf.Max(emissive.g, emissive.b)) > 0 &&
                (material.GetFloat("_AlbedoAffectEmissive") != 0 ||
                 (material.IsKeywordEnabled("_EMISSIVE_COLOR_MAP") && material.GetFloat("_UVEmissive") != 0)))
            { UnsupportedReason = "Emission mapping is unsupported; use an explicit SurfaceID."; return; }
            metallic = material.GetFloat("_Metallic"); smoothness = material.GetFloat("_Smoothness");
            if (!material.IsKeywordEnabled("_MASKMAP"))
            {
                if (!Unit(metallic) || !Unit(smoothness)) UnsupportedReason = "Nonfinite or out-of-range material PBR values.";
                return;
            }
            if (material.GetFloat("_UVBase") != 0 || material.GetVector("_UVMappingMask") != new Vector4(1, 0, 0, 0) ||
                material.IsKeywordEnabled("_MAPPING_PLANAR") || material.IsKeywordEnabled("_MAPPING_TRIPLANAR"))
            { UnsupportedReason = "Mask Map requires UV0; UV1–3, planar and triplanar mapping are not inferred."; return; }
            if (!(material.GetTexture("_MaskMap") is Texture2D texture))
            { UnsupportedReason = "The enabled Mask Map is not a Texture2D."; return; }
            if (!UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsUNormFormat(texture.graphicsFormat) &&
                !UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat))
            { UnsupportedReason = "Mask Map requires an unsigned normalized texture format; HDR, float and signed formats are not inferred."; return; }
            metallicMin = material.GetFloat("_MetallicRemapMin"); metallicMax = material.GetFloat("_MetallicRemapMax");
            smoothnessMin = material.GetFloat("_SmoothnessRemapMin"); smoothnessMax = material.GetFloat("_SmoothnessRemapMax");
            // HDRP shares base-layer UV transforms with its Mask Map, not _MaskMap_ST.
            scale = material.GetTextureScale("_BaseColorMap"); offset = material.GetTextureOffset("_BaseColorMap");
            if (!Unit(metallicMin) || !Unit(metallicMax) || !Unit(smoothnessMin) || !Unit(smoothnessMax) ||
                !float.IsFinite(scale.x) || !float.IsFinite(scale.y) || !float.IsFinite(offset.x) || !float.IsFinite(offset.y))
            { UnsupportedReason = "Invalid Mask Map remaps or UV transform."; return; }
            wrapU = texture.wrapModeU; wrapV = texture.wrapModeV; point = texture.filterMode == FilterMode.Point;
            width = Mathf.Min(texture.width, 512); height = Mathf.Min(texture.height, 512);
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active; Texture2D readable = null;
            try
            {
                Graphics.Blit(texture, rt); RenderTexture.active = rt;
                readable = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
                readable.ReadPixels(new Rect(0, 0, width, height), 0, 0, false); readable.Apply(false, false);
                pixels = readable.GetPixels32();
            }
            finally
            {
                if (readable != null) UnityEngine.Object.DestroyImmediate(readable);
                RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt);
            }
        }

        internal bool TrySample(Vector2 uv, bool hasUv0, out float m, out float s, out string reason)
        {
            m = metallic; s = smoothness; reason = UnsupportedReason;
            if (reason != null) return false;
            if (pixels == null) return true;
            if (!hasUv0 || !float.IsFinite(uv.x) || !float.IsFinite(uv.y))
            { reason = "The Mask Map has no usable mesh UV0."; return false; }
            uv = Vector2.Scale(uv, scale) + offset;
            if (!float.IsFinite(uv.x) || !float.IsFinite(uv.y)) { reason = "Nonfinite transformed Mask Map UV."; return false; }
            float x = Wrap(uv.x, wrapU) * width - .5f, y = Wrap(uv.y, wrapV) * height - .5f;
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            Color value = point ? Pixel(Mathf.FloorToInt(x + .5f), Mathf.FloorToInt(y + .5f)) :
                Color.Lerp(Color.Lerp(Pixel(x0, y0), Pixel(x0 + 1, y0), x - x0),
                    Color.Lerp(Pixel(x0, y0 + 1), Pixel(x0 + 1, y0 + 1), x - x0), y - y0);
            m = Mathf.Lerp(metallicMin, metallicMax, value.r); s = Mathf.Lerp(smoothnessMin, smoothnessMax, value.a);
            return true;
        }

        private Color Pixel(int x, int y) => pixels[Address(x, width, wrapU) + Address(y, height, wrapV) * width];
        private static int Address(int i, int size, TextureWrapMode mode) => mode == TextureWrapMode.Repeat ? (i % size + size) % size : Mathf.Clamp(i, 0, size - 1);
        private static float Wrap(float value, TextureWrapMode mode) => mode switch
        {
            TextureWrapMode.Repeat => Mathf.Repeat(value, 1),
            TextureWrapMode.Mirror => Mathf.PingPong(value, 1),
            TextureWrapMode.MirrorOnce => Mathf.Clamp01(Mathf.Abs(value)),
            _ => Mathf.Clamp01(value)
        };
        private static bool Unit(float value) => float.IsFinite(value) && value >= 0 && value <= 1;
        public void Dispose() { pixels = null; }
    }
}
