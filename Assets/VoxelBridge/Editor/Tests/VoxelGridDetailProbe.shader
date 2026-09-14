Shader "Hidden/Voxel Bridge/Grid Detail Probe"
{
    Properties { _TestDistance("Distance", Float) = 1 _TestEnabled("Enabled", Float) = 1
        _TestOffset("Offset", Float) = 0 _TestSpan("Span", Float) = 0.25
        _TestMultiscale("Multiscale", Float) = 0 _TestTargetPixels("Target", Float) = 12
        _TestMaxScaleLevels("Max Levels", Float) = 4
        _TestDistanceStart("Distance Start", Float) = 12 _TestDistanceStep("Distance Step", Float) = 20
        _TestLevelOnly("Level Only", Float) = 0
        _TestProfile("Profile", Float) = 0 _TestBevelWidth("Bevel", Float) = .06
        _TestJointDepth("Depth", Float) = .025 _TestPatternOnly("Pattern Only", Float) = 0
        _TestPom("POM", Float) = 0 _TestPomMaxDepth("POM cap", Float) = .003
        _TestPomTrace("Trace", Float) = 0 _TestView("View", Vector) = (0,0,1,0)
        _TestVariation("Variation", Float) = 0 _TestPlane("Plane", Vector) = (0,0,0,0)
        _TestCellU("Cell U", Vector) = (1,0,0,0) _TestCellV("Cell V", Vector) = (0,1,0,0) }
    SubShader
    {
        Pass
        {
            ZTest Always ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_local _ TEST_DEPTH_WRITE
            #pragma multi_compile_local _ TEST_SHADOW_PASS
            #if defined(TEST_DEPTH_WRITE)
            #define _DEPTHOFFSET_ON 1
            #endif
            #if defined(TEST_SHADOW_PASS)
            #define SHADERPASS 1
            #define SHADERPASS_SHADOWS 1
            #define SHADERPASS_LIGHT_TRANSPORT 2
            #endif
            #include "UnityCG.cginc"
            float4x4 _TestObjectToWorld, _TestWorldToObject;
            float4x4 GetObjectToWorldMatrix() { return _TestObjectToWorld; }
            float3 TransformWorldToObject(float3 p) { return mul(_TestWorldToObject, float4(p, 1)).xyz; }
            float3 GetAbsolutePositionWS(float3 p) { return p; }
            #include "../../Shaders/VoxelGridDetail.hlsl"
            float _TestDistance, _TestEnabled, _TestOffset, _TestSpan, _TestSpanY;
            float _TestMultiscale, _TestTargetPixels, _TestMaxScaleLevels;
            float _TestAnchor;
            float _TestDistanceStart, _TestDistanceStep, _TestLevelOnly;
            float _TestProfile, _TestBevelWidth, _TestJointDepth, _TestPatternOnly;
            float _TestPom, _TestPomMaxDepth, _TestPomTrace;
            float4 _TestView;
            float _TestVariation;
            float4 _TestPlane, _TestCellU, _TestCellV;
            float4 frag(v2f_img i) : SV_Target
            {
                float3 n; float s; float pixelDepth;
                if (_TestPomTrace > .5)
                {
                    float2 q = i.uv * float2(_TestSpan, _TestSpanY) + _TestOffset;
                    float3 hit;
                    if (_TestVariation > 0)
                        hit = VoxelGridParallax(q, max(fwidth(q), .0001), .04, _TestBevelWidth,
                            _TestJointDepth + _TestVariation, normalize(_TestView.xyz), _TestJointDepth, _TestVariation,
                            _TestPlane.xyz, _TestCellU.xyz, _TestCellV.xyz);
                    else
                        hit = VoxelGridParallax(q, max(fwidth(q), .0001), .04, _TestBevelWidth,
                            _TestJointDepth, normalize(_TestView.xyz));
                    return float4(hit.xy - q, hit.z, 1);
                }
                if (_TestLevelOnly > .5)
                    return VoxelGridDistanceLevel(_TestDistance, _TestDistanceStart, _TestDistanceStep, _TestMaxScaleLevels);
                if (_TestPatternOnly > .5)
                {
                    float2 q = i.uv * float2(_TestSpan, _TestSpanY) + _TestOffset;
                    return VoxelGridVariedBevel(q, max(fwidth(q), .0001), .04, _TestBevelWidth, _TestJointDepth,
                        _TestVariation, _TestPlane.xyz, _TestCellU.xyz, _TestCellV.xyz);
                }
                // A translated camera and surface make distance independent of grid phase.
                float3 p = mul(_TestObjectToWorld, float4(i.uv * float2(_TestSpan, _TestSpanY) + _TestOffset, _TestDistance, 1)).xyz;
                float3 normal = normalize(mul(float3(0,0,1), (float3x3)_TestWorldToObject));
#if defined(TEST_DEPTH_WRITE)
                // The depth fixture faces the camera; legacy normal-output fixtures keep their frame.
                normal = -normal;
#endif
                float3 tangent = normalize(mul((float3x3)_TestObjectToWorld, float3(1,0,0)));
                float3 bitangent = normalize(mul((float3x3)_TestObjectToWorld, float3(0,1,0)));
                VoxelGridDetailDepth_float(p, normal, tangent, bitangent,
                    _TestEnabled, .03125, .2, .2, .2, 2, 8, .6,
                    _TestMultiscale, _TestTargetPixels, _TestMaxScaleLevels, _TestAnchor,
                    _TestDistanceStart, _TestDistanceStep, _TestProfile, _TestBevelWidth, _TestJointDepth,
                    _TestPom, _TestPomMaxDepth, _TestVariation, n, s, pixelDepth);
#if defined(TEST_DEPTH_WRITE)
                return float4(pixelDepth, n.xy, s);
#endif
                return float4(n * .5 + .5, s);
            }
            ENDHLSL
        }
    }
}
