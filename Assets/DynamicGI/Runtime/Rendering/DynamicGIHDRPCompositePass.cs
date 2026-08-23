using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace DynamicGI.Rendering
{
    /// <summary>
    /// HDRP compatibility bridge for stock opaque materials. It adds only the
    /// Dynamic GI term to camera color at BeforeTransparent. Custom materials should
    /// use IndirectLightingProvider.hlsl instead, where Existing/APV can be supplied
    /// or omitted explicitly.
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

            CoreUtils.DrawFullScreen(ctx.cmd, material, ctx.propertyBlock, shaderPassId: 0);
        }

        protected override void Cleanup()
        {
            CoreUtils.Destroy(material);
            material = null;
        }
    }
}
