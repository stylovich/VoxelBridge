using DynamicGI.Contributors;
using UnityEditor;
using UnityEngine;

namespace DynamicGI.Editor
{
    [CustomEditor(typeof(GIEmissiveContributor))]
    public sealed class GIEmissiveContributorEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            GIEmissiveContributor contributor = (GIEmissiveContributor)target;

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "This is the mod-facing GI contract. Renderer bounds locate the source; Influence Range and Maximum Cascade limit injection and regional invalidation. No manual probe placement is required.",
                MessageType.Info);
            EditorGUILayout.LabelField("Source bounds", FormatBounds(contributor.SourceBounds));
            EditorGUILayout.LabelField("Influence bounds", FormatBounds(contributor.InfluenceBounds));
            EditorGUILayout.LabelField("Runtime contribution", contributor.Contributes ? "Enabled" : "Disabled");

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Refresh Renderers"))
                {
                    Undo.RecordObject(contributor, "Refresh GI Emissive Renderers");
                    contributor.RefreshRendererList();
                    EditorUtility.SetDirty(contributor);
                }
                if (GUILayout.Button("Notify Changed"))
                    contributor.NotifyEmissionChanged();
            }
        }

        private static string FormatBounds(Bounds bounds) =>
            $"center {bounds.center:F2}, size {bounds.size:F2}";
    }
}
