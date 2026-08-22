Shader "Hidden/DynamicGI/SkyVisibilityDebug"
{
    Properties
    {
        _EnclosedColor("Enclosed Color", Color) = (0.9, 0.05, 0.25, 0.7)
        _OpenColor("Open Color", Color) = (0.15, 0.85, 1.0, 0.18)
        _Contrast("Contrast", Float) = 1.0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "HDRenderPipeline"
            "Queue" = "Transparent+110"
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

            StructuredBuffer<float4> _DynamicGISkyDebugSamples;
            float _DynamicGISkyDebugSampleScale;
            float _Contrast;
            float4 _EnclosedColor;
            float4 _OpenColor;

            struct Attributes
            {
                float3 positionOS : POSITION;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                nointerpolation float visibility : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float4 sample = _DynamicGISkyDebugSamples[input.instanceID];
                float3 positionAWS = sample.xyz + input.positionOS * _DynamicGISkyDebugSampleScale;
                float3 positionRWS = GetCameraRelativePositionWS(positionAWS);
                output.positionCS = TransformWorldToHClip(positionRWS);
                output.visibility = saturate(sample.w);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float visibility = pow(saturate(input.visibility), max(0.01, _Contrast));
                return lerp(_EnclosedColor, _OpenColor, visibility);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
