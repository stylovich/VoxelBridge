Shader "Hidden/Voxel Bridge/Selection Faces"
{
    Properties { _Color("Color", Color) = (1, 0.8, 0.05, 1) }
    SubShader
    {
        Tags { "RenderPipeline"="HDRenderPipeline" "RenderType"="Transparent" "Queue"="Transparent+100" }
        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode"="ForwardOnly" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
            CBUFFER_END
            struct Attributes { float3 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformWorldToHClip(TransformObjectToWorld(input.positionOS));
                output.uv = input.uv;
                return output;
            }
            float4 Frag(Varyings input) : SV_Target
            {
                float2 distancePixels = min(input.uv, 1 - input.uv) / max(fwidth(input.uv), 0.00001);
                float border = 1 - smoothstep(0.6, 1.3, min(distancePixels.x, distancePixels.y));
                return float4(_Color.rgb, _Color.a * lerp(0.07, 0.9, border));
            }
            ENDHLSL
        }
    }
    Fallback Off
}
