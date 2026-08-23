using UnityEngine;

namespace DynamicGI.Debugging
{
    /// <summary>Scene-view annotations for the generated east-window laboratory.</summary>
    [ExecuteAlways]
    public sealed class Phase4TestRoomGuide : MonoBehaviour
    {
        [SerializeField] private Transform eastWindowMarker;
        [SerializeField] private Transform litProbeMarker;
        [SerializeField] private Transform blockedProbeMarker;
        [SerializeField] private Light sunLight;
        [SerializeField] private Vector3 roomCenter = new(0f, 2.5f, 0f);

#if UNITY_EDITOR
        private GUIStyle titleStyle;
#endif

        private void OnDrawGizmos()
        {
            if (eastWindowMarker != null)
            {
                Gizmos.color = new Color(0.1f, 1f, 0.85f, 0.95f);
                Gizmos.DrawWireCube(eastWindowMarker.position, new Vector3(0.45f, 2.8f, 3.2f));
            }
            if (litProbeMarker != null)
            {
                Gizmos.color = new Color(1f, 0.75f, 0.05f, 1f);
                Gizmos.DrawSphere(litProbeMarker.position, 0.12f);
            }
            if (blockedProbeMarker != null)
            {
                Gizmos.color = new Color(1f, 0.12f, 0.12f, 1f);
                Gizmos.DrawSphere(blockedProbeMarker.position, 0.12f);
            }

#if UNITY_EDITOR
            titleStyle ??= new GUIStyle(UnityEditor.EditorStyles.helpBox) { fontSize = 11 };
            titleStyle.normal.textColor = Color.white;
            if (eastWindowMarker != null)
                UnityEditor.Handles.Label(eastWindowMarker.position + Vector3.up * 1.65f, "VENTANA ESTE  (+X)", titleStyle);
            if (litProbeMarker != null)
                UnityEditor.Handles.Label(litProbeMarker.position + Vector3.up * 0.2f, "probe con línea a ventana", titleStyle);
            if (blockedProbeMarker != null)
                UnityEditor.Handles.Label(blockedProbeMarker.position + Vector3.up * 0.2f, "probe bloqueado por pared", titleStyle);

            UnityEditor.Handles.color = Color.red;
            UnityEditor.Handles.ArrowHandleCap(0, roomCenter, Quaternion.LookRotation(Vector3.right), 1.5f, EventType.Repaint);
            UnityEditor.Handles.Label(roomCenter + Vector3.right * 1.7f, "+X / ESTE", titleStyle);
            if (sunLight != null)
            {
                Vector3 toSun = -sunLight.transform.forward.normalized;
                UnityEditor.Handles.color = new Color(1f, 0.75f, 0.05f, 1f);
                UnityEditor.Handles.ArrowHandleCap(0, roomCenter + Vector3.up, Quaternion.LookRotation(toSun), 2f, EventType.Repaint);
            }
#endif
        }
    }
}
