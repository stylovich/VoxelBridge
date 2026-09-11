Shader "Hidden/Voxel Bridge/Painter Diagnostic"
{
    Properties
    {
        _PaletteColor("Color Palette", 2D) = "white" {}
        _PaletteSurface("Surface Palette", 2D) = "black" {}
        _ViewMode("View Mode", Float) = 1
    }
    SubShader
    {
        Tags { "RenderPipeline"="HDRenderPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode"="ForwardOnly" }
            ZWrite On
            ZTest LEqual
            Cull Back
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
            TEXTURE2D(_PaletteColor);
            TEXTURE2D(_PaletteSurface);
            CBUFFER_START(UnityPerMaterial)
                float _ViewMode;
            CBUFFER_END
            struct Attributes { float3 positionOS : POSITION; float2 colorId : TEXCOORD0; float2 flag : TEXCOORD1; float2 surfaceId : TEXCOORD3; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; nointerpolation float2 ids : TEXCOORD0; nointerpolation float flag : TEXCOORD1; UNITY_VERTEX_OUTPUT_STEREO };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformWorldToHClip(TransformObjectToWorld(input.positionOS));
                output.ids = float2(input.colorId.x, input.surfaceId.x);
                output.flag = input.flag.x;
                return output;
            }
            float4 Frag(Varyings input) : SV_Target
            {
                int2 ids = (int2)round(input.ids);
                float3 rgb = LOAD_TEXTURE2D(_PaletteColor, int2(ids.x, 0)).rgb;
                if (_ViewMode == 2)
                {
                    // Stable ID colors, matching DiagnosticSurfaceColor in the editor.
                    float h = frac(ids.y * 0.61803398875);
                    float3 hue = saturate(abs(frac(h + float3(0, 2.0/3.0, 1.0/3.0)) * 6 - 3) - 1);
                    float3 srgb = ids.y == 0 ? float3(0.5, 0.5, 0.5) : lerp(1.0, hue, 0.68) * 0.9;
                    rgb = SRGBToLinear(srgb);
                }
                else if (_ViewMode == 3)
                {
                    // Presence in the production LUT, not bloom, HDR intensity or an inferred material type.
                    float emission = LOAD_TEXTURE2D(_PaletteSurface, int2(ids.y, 0)).b;
                    rgb = emission > 0 ? lerp(rgb, 1.0, 0.12) : dot(rgb, float3(0.2126, 0.7152, 0.0722)) * 0.025;
                }
                if (_ViewMode == 4)
                    rgb = input.flag > 0.5 ? SRGBToLinear(float3(1, 0.25, 0.12)) : rgb * 0.3;
                return float4(rgb, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
