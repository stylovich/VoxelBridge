Shader "Hidden/Voxel Bridge/Block SPOM Probe"
{
    Properties
    {
        _TestRay("Ray", Vector) = (0,0,1,0)
        _TestMode("Mode", Float) = 0
    }
    SubShader
    {
        Pass
        {
            ZTest Always ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            // Only the context-independent volume kernel is exercised by this probe.
            #define SHADERGRAPH_PREVIEW 1
            #include "../../Shaders/VoxelBlockSpom.hlsl"
            float4 _TestRay;
            float _TestMode;
            float4 frag(v2f_img i) : SV_Target
            {
                float3 ray = normalize(_TestRay.xyz);
                float3 right = normalize(cross(float3(0,1,0), ray));
                float3 up = cross(ray, right);
                float3 origin = -ray + right * (i.uv.x - 0.5) * 0.9 + up * (i.uv.y - 0.5) * 0.9;
                float t, leave, joint; float3 normal;
                bool hit;
                if (_TestMode > 0.5)
                {
                    float halfSize = _TestMode < 1.5 ? 0.25 : 0.24;
                    hit = VoxelBlockBox(origin, ray, -halfSize, halfSize, 0, t, leave, normal);
                }
                else hit = VoxelBlockTrace(origin, ray, float3(0.25,0.25,0.25), 0.0625,
                    0.04, 0.06, 0.025, 0.25, 0.01, t, normal, joint);
                return float4(hit ? max(t,0) : -1, hit ? normal : float3(0,0,0));
            }
            ENDHLSL
        }
    }
}
