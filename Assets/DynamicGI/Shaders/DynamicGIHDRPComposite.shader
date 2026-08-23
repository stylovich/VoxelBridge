Shader "Hidden/DynamicGI/HDRPComposite"
{
    HLSLINCLUDE

    #pragma vertex Vert
    #pragma target 4.5
    #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch switch2

    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/NormalBuffer.hlsl"
    #include "IndirectLightingProvider.hlsl"

    TEXTURE2D_X(_GBufferTexture0);
    float _DynamicGI_HDRPAlbedoWeight;
    float _DynamicGI_HDRPViewBias;

    float4 DynamicGIComposite(Varyings varyings) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(varyings);
        uint2 pixel = (uint2)varyings.positionCS.xy;
        float depth = LoadCameraDepth(pixel);
        if (depth == UNITY_RAW_FAR_CLIP_VALUE)
            return 0.0;

        PositionInputs positionInput = GetPositionInput(
            pixel,
            _ScreenSize.zw,
            depth,
            UNITY_MATRIX_I_VP,
            UNITY_MATRIX_V);
        NormalData normalData;
        DecodeFromNormalBuffer(positionInput.positionSS, normalData);

        // HDRP may reconstruct camera-relative positions. The fields are explicitly
        // world-space, so convert before sampling to avoid a camera-anchored result.
        float3 positionAWS = GetAbsolutePositionWS(positionInput.positionWS);
        float3 cameraAWS = GetAbsolutePositionWS(GetPrimaryCameraPosition());
        float3 toCamera = cameraAWS - positionAWS;
        positionAWS += toCamera * rsqrt(max(dot(toCamera, toCamera), 1e-6)) * max(0.0, _DynamicGI_HDRPViewBias);
        float3 dynamicIndirect = SampleDynamicGIForAdditiveBridge(positionAWS, normalData.normalWS);

        // GBuffer0 is optional weighting for deferred Lit. Keep its default at zero
        // so forward opaque materials never depend on an unavailable GBuffer albedo.
        float3 surfaceTint = 1.0;
        if (_DynamicGI_HDRPAlbedoWeight > 0.0)
        {
            float3 gbufferAlbedo = saturate(LOAD_TEXTURE2D_X(_GBufferTexture0, positionInput.positionSS).rgb);
            surfaceTint = lerp(1.0.xxx, gbufferAlbedo, saturate(_DynamicGI_HDRPAlbedoWeight));
        }

        // Camera color is pre-exposed at this injection point.
        return float4(dynamicIndirect * surfaceTint * GetCurrentExposureMultiplier(), 0.0);
    }

    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }
        Pass
        {
            Name "Dynamic GI Additive"
            ZWrite Off
            ZTest Always
            Blend One One
            Cull Off

            HLSLPROGRAM
            #pragma fragment DynamicGIComposite
            ENDHLSL
        }
    }
    Fallback Off
}
