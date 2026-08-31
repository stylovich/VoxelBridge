Shader "Hidden/Voxel Bridge/Impostor Bake HDRP"
{
    Properties
    {
        _BaseColorMap("Base Color Map", 2D) = "white" {}
        _BaseColor("Base Color", Color) = (1, 1, 1, 1)
        _Metallic("Metallic", Range(0, 1)) = 0
        _Smoothness("Smoothness", Range(0, 1)) = 0
        _EmissiveColor("Emissive Color", Color) = (0, 0, 0, 0)
    }

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }
        Cull Back

        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }

            Blend One Zero
            ZWrite On
            ZTest LEqual
            ColorMask RGBA

            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 d3d12 vulkan metal xboxone ps4 switch
            #pragma multi_compile_instancing
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            #ifndef SHADERCONFIG_CS_HLSL
            #define SHADERCONFIG_CS_HLSL
            #define PROBEVOLUMESEVALUATIONMODES_DISABLED (0)
            #define PROBEVOLUMESEVALUATIONMODES_LIGHT_LOOP (1)
            #define PROBEVOLUMESEVALUATIONMODES_MATERIAL_PASS (2)
            #define SHADEROPTIONS_CAMERA_RELATIVE_RENDERING 0
            #endif

            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/FragInputs.hlsl"
            #define SHADERPASS SHADERPASS_FORWARD
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariablesMatrixDefsHDCamera.hlsl"

            float4x4 glstate_matrix_projection;
            float4x4 unity_MatrixV;
            float4x4 unity_MatrixInvV;
            float4x4 unity_MatrixVP;

            #undef UNITY_MATRIX_V
            #define UNITY_MATRIX_V unity_MatrixV
            #undef UNITY_MATRIX_I_V
            #define UNITY_MATRIX_I_V unity_MatrixInvV
            #undef UNITY_MATRIX_P
            #define UNITY_MATRIX_P OptimizeProjectionMatrix(glstate_matrix_projection)
            #undef UNITY_MATRIX_VP
            #define UNITY_MATRIX_VP unity_MatrixVP

            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

            sampler2D _BaseColorMap;

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColorMap_ST;
                float4 _BaseColor;
                float4 _EmissiveColor;
                float _Metallic;
                float _Smoothness;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv0 : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float2 uv0 : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output;
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv0 = input.uv0 * _BaseColorMap_ST.xy + _BaseColorMap_ST.zw;
                return output;
            }

            void Frag(
                Varyings input,
                out half4 outAlbedo : SV_Target0,
                out half4 outNormalDepth : SV_Target1,
                out half4 outSpecularSmoothness : SV_Target2,
                out half4 outOcclusion : SV_Target3,
                out half4 outEmission : SV_Target4,
                out half4 outPosition : SV_Target5,
                out float outDepth : SV_Depth)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                float4 albedo = tex2D(_BaseColorMap, input.uv0) * _BaseColor;
                float3 normalWS = normalize(input.normalWS);
                float3 specular = lerp(0.04.xxx, albedo.rgb, saturate(_Metallic));

                outAlbedo = float4(albedo.rgb, 1.0);
                outNormalDepth = float4(normalWS * 0.5 + 0.5, input.positionCS.z);
                outSpecularSmoothness = float4(specular, saturate(_Smoothness));
                outOcclusion = float4(1.0, 1.0, 1.0, 0.0);
                outEmission = float4(_EmissiveColor.rgb, 0.0);
                outPosition = 0.0;
                outDepth = input.positionCS.z;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
