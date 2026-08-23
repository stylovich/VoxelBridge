using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace DynamicGI.Rendering
{
    /// <summary>
    /// HDRP compatibility bridge for stock opaque materials. ExistingPlusDynamic is
    /// strictly additive. DynamicOnly can first apply an approximate accessibility
    /// darkening pass, then add Dynamic GI. Custom materials remain the exact path
    /// because they can replace only the indirect lobe without attenuating direct or
    /// specular lighting.
    /// </summary>
    [Serializable]
    public sealed class DynamicGIHDRPCompositePass : CustomPass
    {
        [SerializeField] private Shader compositeShader;
        [SerializeField] private bool renderInSceneView = true;
        [SerializeField] private bool renderInReflectionCameras;

        [NonSerialized] private Material material;

        public Shader CompositeShader => compositeShader;
        public bool RenderInSceneView => renderInSceneView;
        public bool RenderInReflectionCameras => renderInReflectionCameras;

        protected override bool executeInSceneView => renderInSceneView;

        public void Configure(Shader shader, bool sceneView = true, bool reflectionCameras = false)
        {
            compositeShader = shader;
            renderInSceneView = sceneView;
            renderInReflectionCameras = reflectionCameras;
        }

        protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd)
        {
            Shader shader = compositeShader != null
                ? compositeShader
                : Shader.Find("Hidden/DynamicGI/HDRPComposite");
            if (shader == null)
            {
                Debug.LogError("Dynamic GI HDRP composite shader is missing. Re-run the Phase 10 setup.");
                return;
            }
            material = CoreUtils.CreateEngineMaterial(shader);
        }

        protected override void Execute(CustomPassContext ctx)
        {
            DynamicGIShaderGlobals controls = DynamicGIShaderGlobals.Active;
            if (material == null || controls == null || !controls.ScreenSpaceBridgeEnabled || controls.DynamicGIStrength <= 0f)
                return;

            Camera camera = ctx.hdCamera.camera;
            if (camera == null || camera.cameraType == CameraType.Preview ||
                (!renderInReflectionCameras && camera.cameraType == CameraType.Reflection))
                return;

            if (controls.ProviderMode == IndirectLightingProviderMode.DynamicOnly &&
                controls.ScreenSpaceReplacementDarkening &&
                controls.ReplacementDarkeningStrength > 0f)
            {
                CoreUtils.DrawFullScreen(ctx.cmd, material, ctx.propertyBlock, shaderPassId: 1);
            }
            CoreUtils.DrawFullScreen(ctx.cmd, material, ctx.propertyBlock, shaderPassId: 0);
        }

        protected override void Cleanup()
        {
            CoreUtils.Destroy(material);
            material = null;
        }
    }
}
