using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    [CustomEditor(typeof(VoxelImpostorProfile))]
    [CanEditMultipleObjects]
    internal sealed class VoxelImpostorProfileEditor : UnityEditor.Editor
    {
        private const string ProfileSummary =
            "Esta configuración central contiene los cuatro perfiles que aparecen en la ventana de Voxel Bridge. " +
            "Cada familia conserva el perfil elegido dentro de esta configuración compartida.\n\n" +
            "Low · Atlas 512 · 8×8 vistas · HemiOctahedron.\n" +
            "Medium · Atlas 1024 · 12×12 vistas · Octahedron.\n" +
            "High · Atlas 2048 · 16×16 vistas · Octahedron.\n" +
            "Architecture · Atlas 2048 · 16×16 vistas · HemiOctahedron.";

        private void OnEnable()
        {
            foreach (UnityEngine.Object value in targets)
            {
                var profile = (VoxelImpostorProfile)value;
                if (!profile.EnsureInitialized()) continue;
                EditorUtility.SetDirty(profile);
            }
        }

        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox(ProfileSummary, MessageType.Info);
            EditorGUILayout.HelpBox(
                "Al aumentar Views per Axis, considerar también el tamaño del atlas. " +
                "Por ejemplo, 1024/16 deja aproximadamente 64 píxeles por vista, mientras que 1024/8 deja 128.",
                MessageType.None);
            DrawDefaultInspector();

            EditorGUILayout.Space(8);
            if (!GUILayout.Button("Restore Four Recommended Profiles")) return;

            Undo.RecordObjects(targets, "Restore Impostor Profiles");
            foreach (UnityEngine.Object value in targets)
            {
                var profile = (VoxelImpostorProfile)value;
                profile.ResetRecommendedProfiles();
                EditorUtility.SetDirty(profile);
            }
            serializedObject.Update();
        }
    }
}
