using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    [CustomEditor(typeof(VoxelConversionProfile))]
    internal sealed class VoxelConversionProfileEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var profile = (VoxelConversionProfile)target;
            EditorGUILayout.HelpBox("Las reglas utilizan referencias a materiales, no sus nombres. El componente Conversion Rule tiene prioridad. Keep Original conserva solamente geometría y materiales estáticos.", MessageType.Info);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("colorMapping"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("surfacePalette"));
            DrawSurface(EditorGUILayout.GetControlRect(), serializedObject.FindProperty("defaultSurfaceId"), profile.surfacePalette, false);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("materialRules"), true);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("detectEmission"));
            if (serializedObject.FindProperty("detectEmission").boolValue)
            {
                DrawSurface(EditorGUILayout.GetControlRect(), serializedObject.FindProperty("emissiveSurfaceId"), profile.surfacePalette, false);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("emissionThreshold"));
            }
            serializedObject.ApplyModifiedProperties();
            try { profile.Validate(); }
            catch (Exception exception) { EditorGUILayout.HelpBox(exception.Message, MessageType.Error); }
        }

        internal static void DrawSurface(Rect rect, SerializedProperty property, VoxelSurfacePalette palette, bool automatic)
        {
            if (palette == null) { EditorGUI.PropertyField(rect, property); return; }
            var entries = palette.Entries.Where(e => e != null).OrderBy(e => e.Id).ToArray();
            int[] ids = (automatic ? new[] { -1 } : Array.Empty<int>()).Concat(entries.Select(e => e.Id)).ToArray();
            string[] labels = (automatic ? new[] { "Automatic / Inherit" } : Array.Empty<string>())
                .Concat(entries.Select(e => $"{e.Id:000} · {e.DisplayName}")).ToArray();
            if (!ids.Contains(property.intValue))
            { ids = ids.Append(property.intValue).ToArray(); labels = labels.Append($"Missing SurfaceID {property.intValue}").ToArray(); }
            EditorGUI.BeginProperty(rect, GUIContent.none, property);
            int selected = EditorGUI.IntPopup(rect, new GUIContent(property.displayName, property.tooltip), property.intValue,
                labels.Select(label => new GUIContent(label)).ToArray(), ids);
            if (selected != property.intValue) property.intValue = selected;
            EditorGUI.EndProperty();
        }
    }

    [CustomPropertyDrawer(typeof(VoxelMaterialConversionRule))]
    internal sealed class VoxelMaterialConversionRuleDrawer : PropertyDrawer
    {
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) => (EditorGUIUtility.singleLineHeight + 2) * 3;
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            position.height = EditorGUIUtility.singleLineHeight;
            EditorGUI.PropertyField(position, property.FindPropertyRelative("material"));
            position.y += position.height + 2;
            EditorGUI.PropertyField(position, property.FindPropertyRelative("action"));
            position.y += position.height + 2;
            using (new EditorGUI.DisabledScope(property.FindPropertyRelative("action").enumValueIndex != (int)VoxelConversionAction.Voxelize))
                VoxelConversionProfileEditor.DrawSurface(position, property.FindPropertyRelative("surfaceId"),
                    (property.serializedObject.targetObject as VoxelConversionProfile)?.surfacePalette, true);
            EditorGUI.EndProperty();
        }
    }
}
