Shader "Hidden/DynamicGI/GeometryFieldDebug"
{
    Properties
    {
        _OccupiedColor("Occupied Color", Color) = (0.1, 1.0, 0.35, 0.65)
        _EmptyColor("Empty Color", Color) = (0.15, 0.55, 1.0, 0.08)
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "HDRenderPipeline"
            "Queue" = "Transparent+100"
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

            StructuredBuffer<float4> _DynamicGIDebugVoxels;
            float _DynamicGIDebugVoxelScale;
            float4 _OccupiedColor;
            float4 _EmptyColor;

            struct Attributes
            {
                float3 positionOS : POSITION;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                nointerpolation float occupied : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float4 voxel = _DynamicGIDebugVoxels[input.instanceID];
                // The compute buffer stores absolute world-space positions. HDRP's
                // view-projection matrices use camera-relative world space, so feeding
                // absolute positions directly makes the instances drift with the camera.
                float3 positionAWS = voxel.xyz + input.positionOS * _DynamicGIDebugVoxelScale;
                float3 positionRWS = GetCameraRelativePositionWS(positionAWS);
                output.positionCS = TransformWorldToHClip(positionRWS);
                output.occupied = voxel.w;
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                return input.occupied > 0.5 ? _OccupiedColor : _EmptyColor;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
