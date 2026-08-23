using UnityEngine;

namespace DynamicGI.Debugging
{
    /// <summary>Keeps diagnostic instances out of gameplay cameras by default.</summary>
    internal static class DynamicGIDebugRenderUtility
    {
        public static bool TryResolveCamera(bool renderInGameView, out Camera camera)
        {
            if (renderInGameView)
            {
                camera = null;
                return true;
            }

#if UNITY_EDITOR
            UnityEditor.SceneView sceneView = UnityEditor.SceneView.lastActiveSceneView;
            camera = sceneView != null ? sceneView.camera : null;
            return camera != null;
#else
            camera = null;
            return false;
#endif
        }
    }
}
