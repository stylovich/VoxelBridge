using DynamicGI.Contributors;
using UnityEngine;

namespace DynamicGI.Debugging
{
    /// <summary>Scene-view legend for the portable Phase-7 emissive visibility test.</summary>
    [ExecuteAlways]
    public sealed class Phase7EmissiveTestGuide : MonoBehaviour
    {
        [SerializeField] private GIEmissiveContributor contributor;
        [SerializeField] private Transform visibleProbeMarker;
        [SerializeField] private Transform blockedProbeMarker;
        [SerializeField] private Transform occluder;

        private void OnDrawGizmos()
        {
            if (contributor == null)
                return;

            Bounds source = contributor.SourceBounds;
            Gizmos.color = new Color(1f, 0.05f, 0.75f, 0.9f);
            Gizmos.DrawWireSphere(source.center, 0.3f);
            DrawMarker(visibleProbeMarker, new Color(0.15f, 1f, 0.35f, 0.95f));
            DrawMarker(blockedProbeMarker, new Color(1f, 0.22f, 0.12f, 0.95f));

            if (visibleProbeMarker != null)
                Gizmos.DrawLine(source.center, visibleProbeMarker.position);
            if (blockedProbeMarker != null)
            {
                Gizmos.color = new Color(1f, 0.22f, 0.12f, 0.55f);
                Gizmos.DrawLine(source.center, blockedProbeMarker.position);
            }

#if UNITY_EDITOR
            UnityEditor.Handles.Label(source.center + Vector3.up * 0.45f, "NEON EMISSIVE");
            if (visibleProbeMarker != null)
                UnityEditor.Handles.Label(visibleProbeMarker.position + Vector3.up * 0.25f, "VISIBLE (-Z lobe)");
            if (blockedProbeMarker != null)
                UnityEditor.Handles.Label(blockedProbeMarker.position + Vector3.up * 0.25f, "BLOCKED BY VOXEL WALL");
            if (occluder != null)
                UnityEditor.Handles.Label(occluder.position + Vector3.up * 1.75f, "DDA OCCLUDER");
#endif
        }

        private static void DrawMarker(Transform marker, Color color)
        {
            if (marker == null)
                return;
            Gizmos.color = color;
            Gizmos.DrawSphere(marker.position, 0.09f);
            Gizmos.DrawWireSphere(marker.position, 0.18f);
        }
    }
}
