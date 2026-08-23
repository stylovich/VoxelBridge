Shader "Hidden/DynamicGI/RadianceDebug"
{
    Properties
    {
        _Exposure("Exposure", Float) = 1.0
        _MinimumAlpha("Minimum Alpha", Float) = 0.15
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "HDRenderPipeline"
            "Queue" = "Transparent+120"
            "RenderType" = "Transparent"
        }

        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

            struct RadianceDebugSample
            {
                float4 PositionAndLuminance;
                float4 ColorAndValidity;
            };

            StructuredBuffer<RadianceDebugSample> _DynamicGIRadianceDebugSamples;
            float _DynamicGIRadianceDebugScale;
            float _Exposure;
            float _MinimumAlpha;

            struct Attributes
            {
                float3 positionOS : POSITION;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                nointerpolation float3 color : TEXCOORD0;
                nointerpolation float validity : TEXCOORD1;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                RadianceDebugSample sample = _DynamicGIRadianceDebugSamples[input.instanceID];
                float3 positionAWS = sample.PositionAndLuminance.xyz + input.positionOS * _DynamicGIRadianceDebugScale;
                output.positionCS = TransformWorldToHClip(GetCameraRelativePositionWS(positionAWS));
                output.color = 1.0 - exp(-max(0.0, sample.ColorAndValidity.rgb) * max(0.0, _Exposure));
                output.validity = sample.ColorAndValidity.a;
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                clip(input.validity - 0.001);
                float alpha = lerp(_MinimumAlpha, 0.9, saturate(max(input.color.r, max(input.color.g, input.color.b))));
                return float4(input.color, alpha * input.validity);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
