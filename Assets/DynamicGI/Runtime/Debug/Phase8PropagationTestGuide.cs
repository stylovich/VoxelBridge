using DynamicGI.Contributors;
using UnityEngine;

namespace DynamicGI.Debugging
{
    /// <summary>Scene-view legend for the Phase-8 ceiling bounce and wall-leak test.</summary>
    [ExecuteAlways]
    public sealed class Phase8PropagationTestGuide : MonoBehaviour
    {
        [SerializeField] private GIEmissiveContributor contributor;
        [SerializeField] private Transform ceilingBounceMarker;
        [SerializeField] private Transform blockedMarker;
        [SerializeField] private Transform occluder;

        private void OnDrawGizmos()
        {
            if (contributor == null)
                return;

            Vector3 source = contributor.SourceBounds.center;
            DrawMarker(ceilingBounceMarker, new Color(0.75f, 0.2f, 1f, 0.95f));
            DrawMarker(blockedMarker, new Color(1f, 0.15f, 0.1f, 0.95f));
            if (ceilingBounceMarker != null)
            {
                Gizmos.color = new Color(0.9f, 0.25f, 1f, 0.65f);
                Gizmos.DrawLine(source, ceilingBounceMarker.position);
            }
            if (blockedMarker != null)
            {
                Gizmos.color = new Color(1f, 0.15f, 0.1f, 0.45f);
                Gizmos.DrawLine(source, blockedMarker.position);
            }

#if UNITY_EDITOR
            if (ceilingBounceMarker != null)
                UnityEditor.Handles.Label(
                    ceilingBounceMarker.position + Vector3.up * 0.25f,
                    "PHASE 8: PROPAGATED CEILING (-Y)");
            if (blockedMarker != null)
                UnityEditor.Handles.Label(
                    blockedMarker.position + Vector3.up * 0.25f,
                    "NO WALL LEAK EXPECTED");
            if (occluder != null)
                UnityEditor.Handles.Label(
                    occluder.position + Vector3.up * 1.95f,
                    "6-NEIGHBOR DDA BARRIER");
#endif
        }

        private static void DrawMarker(Transform marker, Color color)
        {
            if (marker == null)
                return;
            Gizmos.color = color;
            Gizmos.DrawSphere(marker.position, 0.09f);
            Gizmos.DrawWireSphere(marker.position, 0.2f);
        }
    }
}
