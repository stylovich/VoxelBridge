using DynamicGI.Rendering;
using UnityEditor;
using UnityEngine;

namespace DynamicGI.Editor
{
    [CustomEditor(typeof(DynamicGIShaderGlobals))]
    public sealed class DynamicGIShaderGlobalsEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            DynamicGIShaderGlobals controls = (DynamicGIShaderGlobals)target;

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Occlusion Strength is exposed as ambient/sky accessibility. It is not multiplied over APV or the complete Dynamic GI result.",
                MessageType.Info);
            EditorGUILayout.HelpBox(
                "Surface Normal Bias keeps trilinear samples on the visible side of thin shells. HDRP View Bias additionally handles visible T-junctions in the stock-material bridge; keep both near the C0 probe spacing and validate them against your smallest rooms.",
                MessageType.Info);
            if (controls.ScreenSpaceBridgeEnabled &&
                controls.ProviderMode == IndirectLightingProviderMode.DynamicOnly)
            {
                EditorGUILayout.HelpBox(
                    "DynamicOnly is exact only inside materials using IndirectLightingProvider.hlsl. The HDRP stock-material bridge is additive and cannot remove APV already evaluated by HDRP.",
                    MessageType.Warning);
            }

            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button("Publish Globals Now"))
                    controls.PublishNow();
            }
        }
    }
}
