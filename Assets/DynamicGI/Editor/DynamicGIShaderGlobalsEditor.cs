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
                "Geometry-aware surface sampling rejects probes hidden behind voxel geometry. Surface Normal Bias and HDRP View Bias establish the visible-side ray origin; keep them near the C0 probe spacing and validate them against your smallest rooms.",
                MessageType.Info);
            if (controls.ScreenSpaceBridgeEnabled &&
                controls.ProviderMode == IndirectLightingProviderMode.ExistingPlusDynamic)
            {
                EditorGUILayout.HelpBox(
                    "Existing Plus Dynamic preserves Unity/APV ambient and adds Dynamic GI on top. Start with Strength around 0.15-0.35; Replacement Darkening is intentionally ignored in this coexistence mode.",
                    MessageType.Info);
            }
            if (controls.ScreenSpaceBridgeEnabled &&
                controls.ProviderMode == IndirectLightingProviderMode.DynamicOnly)
            {
                EditorGUILayout.HelpBox(
                    controls.ScreenSpaceReplacementDarkening
                        ? "DynamicOnly uses an approximate stock-material replacement preview: sky accessibility multiplies the complete opaque camera color before Dynamic GI is added. It can darken Unity ambient/APV, but also attenuates direct and specular lighting. IndirectLightingProvider.hlsl inside the material is the exact replacement path."
                        : "Replacement darkening is disabled, so the stock-material bridge remains additive and cannot remove APV or Unity ambient lighting.",
                    MessageType.Warning);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Provider presets", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("APV Coexistence"))
            {
                Undo.RecordObject(controls, "Apply Dynamic GI APV Coexistence Preset");
                controls.ApplyExistingPlusDynamicPreset();
                EditorUtility.SetDirty(controls);
            }
            if (GUILayout.Button("Dynamic Replacement Preview"))
            {
                Undo.RecordObject(controls, "Apply Dynamic GI Replacement Preview Preset");
                controls.ApplyDynamicOnlyPreviewPreset();
                EditorUtility.SetDirty(controls);
            }
            EditorGUILayout.EndHorizontal();

            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button("Publish Globals Now"))
                    controls.PublishNow();
            }
        }
    }
}
