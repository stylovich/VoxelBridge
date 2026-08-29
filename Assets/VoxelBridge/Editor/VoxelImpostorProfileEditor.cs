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
            "Cada familia recuerda el nivel elegido; no necesitas crear ni intercambiar archivos de perfil.\n\n" +
            "BAJO · Atlas 512 · 8×8 vistas · HemiOctahedron · sin Cross Fade.\n" +
            "MEDIO · Atlas 1024 · 12×12 vistas · Octahedron · Cross Fade 0.15.\n" +
            "ALTO · Atlas 2048 · 16×16 vistas · Octahedron · Cross Fade 0.25.\n" +
            "ARQUITECTURA · Atlas 2048 · 16×16 vistas · HemiOctahedron · Cross Fade 0.30.";

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
                "Regla importante: al aumentar Vistas por eje también debes considerar aumentar el atlas. " +
                "Por ejemplo, 1024/16 deja aproximadamente 64 píxeles por vista, mientras que 1024/8 deja 128.",
                MessageType.None);
            DrawDefaultInspector();

            EditorGUILayout.Space(8);
            if (!GUILayout.Button("Restaurar los cuatro perfiles recomendados")) return;

            Undo.RecordObjects(targets, "Restaurar perfiles de impostor");
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
