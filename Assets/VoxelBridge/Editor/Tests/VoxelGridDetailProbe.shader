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
        _TestPomTrace("Trace", Float) = 0 _TestView("View", Vector) = (0,0,1,0) }
    SubShader
    {
        Pass
        {
            ZTest Always ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 4.5
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
            float4 frag(v2f_img i) : SV_Target
            {
                float3 n; float s;
                if (_TestPomTrace > .5)
                {
                    float2 q = i.uv * float2(_TestSpan, _TestSpanY) + _TestOffset;
                    float3 hit = VoxelGridParallax(q, max(fwidth(q), .0001), .04, _TestBevelWidth,
                        _TestJointDepth, normalize(_TestView.xyz));
                    return float4(hit.xy - q, hit.z, 1);
                }
                if (_TestLevelOnly > .5)
                    return VoxelGridDistanceLevel(_TestDistance, _TestDistanceStart, _TestDistanceStep, _TestMaxScaleLevels);
                if (_TestPatternOnly > .5)
                {
                    float2 q = i.uv * float2(_TestSpan, _TestSpanY) + _TestOffset;
                    return VoxelGridBevelPattern(q, max(fwidth(q), .0001), .04, _TestBevelWidth, _TestJointDepth);
                }
                // A translated camera and surface make distance independent of grid phase.
                float3 p = mul(_TestObjectToWorld, float4(i.uv * float2(_TestSpan, _TestSpanY) + _TestOffset, _TestDistance, 1)).xyz;
                float3 normal = normalize(mul(float3(0,0,1), (float3x3)_TestWorldToObject));
                float3 tangent = normalize(mul((float3x3)_TestObjectToWorld, float3(1,0,0)));
                float3 bitangent = normalize(mul((float3x3)_TestObjectToWorld, float3(0,1,0)));
                VoxelGridDetail_float(p, normal, tangent, bitangent,
                    _TestEnabled, .03125, .2, .2, .2, 2, 8, .6,
                    _TestMultiscale, _TestTargetPixels, _TestMaxScaleLevels, _TestAnchor,
                    _TestDistanceStart, _TestDistanceStep, _TestProfile, _TestBevelWidth, _TestJointDepth,
                    _TestPom, _TestPomMaxDepth, n, s);
                return float4(n * .5 + .5, s);
            }
            ENDHLSL
        }
    }
}
