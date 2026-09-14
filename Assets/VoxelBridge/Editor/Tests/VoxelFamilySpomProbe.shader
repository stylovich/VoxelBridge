Shader "Hidden/Voxel Bridge/Family SPOM Probe"
{
    Properties
    {
        _Volume("Volume", 3D) = "white" {}
        _Surfaces("Surfaces", 2D) = "white" {}
        _Origin("Origin", Vector) = (0.1,0.1,-1,0)
        _Ray("Ray", Vector) = (0,0,1,0)
        _Size("Size", Vector) = (8,8,8,0)
        _Depth("Depth", Float) = 0.01
    }
    SubShader
    {
        Pass
        {
            ZTest Always ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Texture.hlsl"
            #define SHADERGRAPH_PREVIEW 1
            #include "../../Shaders/VoxelFamilySpom.hlsl"
            TEXTURE3D(_Volume); SAMPLER(sampler_Volume);
            TEXTURE2D(_Surfaces); SAMPLER(sampler_Surfaces);
            float4 _Surfaces_TexelSize, _Surfaces_ST;
            float4 _Origin, _Ray, _Size;
            float _Depth;
            float4 Vert(uint vertexId : SV_VertexID) : SV_POSITION { return GetFullScreenTriangleVertexPosition(vertexId); }
            float4 frag() : SV_Target
            {
                float t, joint; float3 normal; float2 ids;
                bool hit = VoxelFamilyTrace(UnityBuildTexture3DStruct(_Volume), UnityBuildTexture2DStruct(_Surfaces),
                    (int3)_Size.xyz, _Origin.xyz, normalize(_Ray.xyz), 0.0625, 0, .04, .04, .025, _Depth,
                    t, normal, ids, joint);
                return float4(hit ? t : -1, ids, normal.z);
            }
            ENDHLSL
        }
    }
}
