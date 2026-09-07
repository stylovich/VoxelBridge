Shader "Hidden/Voxel Bridge/Selection Faces"
{
    Properties
    {
        _Color("Color", Color) = (1, 0.8, 0.05, 1)
        _FillOpacity("Fill Opacity", Range(0, 1)) = 0
    }
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
                float _FillOpacity;
            CBUFFER_END
            struct Attributes { float3 positionOS : POSITION; float2 uv : TEXCOORD0; float4 edges : TEXCOORD1; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float4 edges : TEXCOORD1; UNITY_VERTEX_OUTPUT_STEREO };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformWorldToHClip(TransformObjectToWorld(input.positionOS));
                output.uv = input.uv;
                output.edges = input.edges;
                return output;
            }
            float4 Frag(Varyings input) : SV_Target
            {
                float2 pixelWidth = max(fwidth(input.uv), 0.00001);
                float4 distancePixels = float4(input.uv.x, 1 - input.uv.x, input.uv.y, 1 - input.uv.y) / pixelWidth.xxyy;
                float4 lines = (1 - smoothstep(0.6, 1.3, distancePixels)) * input.edges;
                float border = max(max(lines.x, lines.y), max(lines.z, lines.w));
                return float4(_Color.rgb, _Color.a * lerp(_FillOpacity, 0.9, border));
            }
            ENDHLSL
        }
    }
    Fallback Off
}
