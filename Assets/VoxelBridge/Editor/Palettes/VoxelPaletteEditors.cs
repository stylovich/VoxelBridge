using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    [CustomEditor(typeof(VoxelColorPalette))]
    internal sealed class VoxelColorPaletteEditor : UnityEditor.Editor
    {
        private ReorderableList entriesList;

        private void OnEnable()
        {
            var palette = (VoxelColorPalette)target;
            if (palette.EnsureInitialized()) EditorUtility.SetDirty(palette);

            SerializedProperty entries = serializedObject.FindProperty("entries");
            entriesList = new ReorderableList(serializedObject, entries,
                draggable: true, displayHeader: true, displayAddButton: true,
                displayRemoveButton: true)
            {
                drawHeaderCallback = rect => EditorGUI.LabelField(rect, "ID        Nombre                         Color"),
                drawElementCallback = DrawEntry,
                onAddCallback = AddEntry,
                onRemoveCallback = RemoveEntry
            };
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.HelpBox(
                "Los IDs son permanentes. Reordenar o renombrar entradas no cambia el ColorID; " +
                "al eliminar una entrada su ID queda retirado.", MessageType.Info);
            entriesList.DoLayoutList();
            serializedObject.ApplyModifiedProperties();

            var palette = (VoxelColorPalette)target;
            DrawRetiredIds(palette.RetiredIds);
            DrawValidationAndLut(palette);

            EditorGUILayout.Space(6f);
            if (GUILayout.Button("Restaurar biblioteca maestra recomendada") &&
                EditorUtility.DisplayDialog(
                    "Restaurar biblioteca maestra",
                    "Se reemplazarán todas las entradas de color y los IDs retirados. " +
                    "Los perfiles se restauran desde la ventana de paletas globales.",
                    "Restaurar", "Cancelar"))
            {
                Undo.RecordObject(palette, "Restaurar biblioteca de colores voxel");
                palette.ResetToRecommended();
                EditorUtility.SetDirty(palette);
                serializedObject.Update();
            }
        }

        private void DrawEntry(Rect rect, int index, bool active, bool focused)
        {
            SerializedProperty element = entriesList.serializedProperty.GetArrayElementAtIndex(index);
            SerializedProperty id = element.FindPropertyRelative("id");
            SerializedProperty displayName = element.FindPropertyRelative("displayName");
            SerializedProperty color = element.FindPropertyRelative("color");

            rect.y += 1f;
            float idWidth = 42f;
            float colorWidth = 92f;
            using (new EditorGUI.DisabledScope(true))
                EditorGUI.IntField(new Rect(rect.x, rect.y, idWidth, EditorGUIUtility.singleLineHeight), id.intValue);
            EditorGUI.PropertyField(
                new Rect(rect.x + idWidth + 6f, rect.y,
                    rect.width - idWidth - colorWidth - 18f, EditorGUIUtility.singleLineHeight),
                displayName, GUIContent.none);
            EditorGUI.PropertyField(
                new Rect(rect.xMax - colorWidth, rect.y, colorWidth, EditorGUIUtility.singleLineHeight),
                color, GUIContent.none);
        }

        private void AddEntry(ReorderableList list)
        {
            serializedObject.ApplyModifiedProperties();
            var palette = (VoxelColorPalette)target;
            Undo.RecordObject(palette, "Añadir color voxel");
            if (!palette.TryAddEntry(out _, out string error))
            {
                EditorUtility.DisplayDialog("Voxel Bridge", error, "Cerrar");
                return;
            }
            EditorUtility.SetDirty(palette);
            serializedObject.Update();
            list.index = entriesList.serializedProperty.arraySize - 1;
        }

        private void RemoveEntry(ReorderableList list)
        {
            serializedObject.ApplyModifiedProperties();
            var palette = (VoxelColorPalette)target;
            Undo.RecordObject(palette, "Eliminar color voxel");
            if (!palette.TryRemoveEntryAt(list.index, out string error))
            {
                EditorUtility.DisplayDialog("Voxel Bridge", error, "Cerrar");
                return;
            }
            EditorUtility.SetDirty(palette);
            serializedObject.Update();
        }

        private static void DrawValidationAndLut(VoxelColorPalette palette)
        {
            if (!palette.TryValidate(out string error))
                EditorGUILayout.HelpBox(error, MessageType.Error);
            else if (!VoxelPaletteLutGenerator.IsCurrent(palette))
                EditorGUILayout.HelpBox("La LUT de color está desactualizada.", MessageType.Warning);
            else
                EditorGUILayout.HelpBox("Paleta válida y LUT actualizada.", MessageType.Info);

            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("LUT generada", palette.GeneratedLut, typeof(Texture2D), false);
            if (GUILayout.Button("Regenerar LUT de color") &&
                !VoxelPaletteLutGenerator.TryRebuild(palette, out _, out error))
                EditorUtility.DisplayDialog("Voxel Bridge", error, "Cerrar");
        }

        private static void DrawRetiredIds(System.Collections.Generic.IReadOnlyList<int> retiredIds)
        {
            if (retiredIds == null || retiredIds.Count == 0) return;
            EditorGUILayout.HelpBox(
                "IDs retirados: " + string.Join(", ", retiredIds.Select(id => id.ToString())),
                MessageType.None);
        }
    }

    [CustomEditor(typeof(VoxelSurfacePalette))]
    internal sealed class VoxelSurfacePaletteEditor : UnityEditor.Editor
    {
        private ReorderableList entriesList;

        private void OnEnable()
        {
            var palette = (VoxelSurfacePalette)target;
            if (palette.EnsureInitialized()) EditorUtility.SetDirty(palette);

            SerializedProperty entries = serializedObject.FindProperty("entries");
            entriesList = new ReorderableList(serializedObject, entries,
                draggable: true, displayHeader: true, displayAddButton: true,
                displayRemoveButton: true)
            {
                elementHeightCallback = _ => EditorGUIUtility.singleLineHeight * 3f + 8f,
                drawHeaderCallback = rect => EditorGUI.LabelField(
                    rect, "ID        Nombre                         Clase de render"),
                drawElementCallback = DrawEntry,
                onAddCallback = AddEntry,
                onRemoveCallback = RemoveEntry
            };
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.HelpBox(
                "Cada voxel referencia un SurfaceID. Los valores PBR se almacenan una sola vez en esta paleta; " +
                "la clase de render determina el material compartido que utilizará el mesher.",
                MessageType.Info);
            entriesList.DoLayoutList();
            serializedObject.ApplyModifiedProperties();

            var palette = (VoxelSurfacePalette)target;
            DrawRetiredIds(palette.RetiredIds);
            DrawValidationAndLut(palette);

            EditorGUILayout.Space(6f);
            if (GUILayout.Button("Restaurar superficies recomendadas") &&
                EditorUtility.DisplayDialog(
                    "Restaurar superficies", "Se reemplazarán todas las entradas y los IDs retirados.",
                    "Restaurar", "Cancelar"))
            {
                Undo.RecordObject(palette, "Restaurar superficies voxel");
                palette.ResetRecommendedProfiles();
                EditorUtility.SetDirty(palette);
                serializedObject.Update();
            }
        }

        private void DrawEntry(Rect rect, int index, bool active, bool focused)
        {
            SerializedProperty element = entriesList.serializedProperty.GetArrayElementAtIndex(index);
            SerializedProperty id = element.FindPropertyRelative("id");
            SerializedProperty displayName = element.FindPropertyRelative("displayName");
            SerializedProperty renderClass = element.FindPropertyRelative("renderClass");
            SerializedProperty metallic = element.FindPropertyRelative("metallic");
            SerializedProperty smoothness = element.FindPropertyRelative("smoothness");
            SerializedProperty emission = element.FindPropertyRelative("emission");
            SerializedProperty occlusion = element.FindPropertyRelative("occlusionMultiplier");

            float line = EditorGUIUtility.singleLineHeight;
            float y = rect.y + 1f;
            float idWidth = 42f;
            float classWidth = 142f;
            using (new EditorGUI.DisabledScope(true))
                EditorGUI.IntField(new Rect(rect.x, y, idWidth, line), id.intValue);
            EditorGUI.PropertyField(
                new Rect(rect.x + idWidth + 6f, y, rect.width - idWidth - classWidth - 18f, line),
                displayName, GUIContent.none);
            EditorGUI.PropertyField(
                new Rect(rect.xMax - classWidth, y, classWidth, line),
                renderClass, GUIContent.none);

            y += line + 3f;
            float half = (rect.width - 8f) * 0.5f;
            EditorGUI.PropertyField(new Rect(rect.x, y, half, line), metallic, new GUIContent("Metallic"));
            EditorGUI.PropertyField(new Rect(rect.x + half + 8f, y, half, line),
                smoothness, new GUIContent("Smoothness"));

            y += line + 3f;
            EditorGUI.PropertyField(new Rect(rect.x, y, half, line), emission, new GUIContent("Emission"));
            EditorGUI.PropertyField(new Rect(rect.x + half + 8f, y, half, line),
                occlusion, new GUIContent("Occlusion"));
        }

        private void AddEntry(ReorderableList list)
        {
            serializedObject.ApplyModifiedProperties();
            var palette = (VoxelSurfacePalette)target;
            Undo.RecordObject(palette, "Añadir superficie voxel");
            if (!palette.TryAddEntry(out _, out string error))
            {
                EditorUtility.DisplayDialog("Voxel Bridge", error, "Cerrar");
                return;
            }
            EditorUtility.SetDirty(palette);
            serializedObject.Update();
            list.index = entriesList.serializedProperty.arraySize - 1;
        }

        private void RemoveEntry(ReorderableList list)
        {
            serializedObject.ApplyModifiedProperties();
            var palette = (VoxelSurfacePalette)target;
            Undo.RecordObject(palette, "Eliminar superficie voxel");
            if (!palette.TryRemoveEntryAt(list.index, out string error))
            {
                EditorUtility.DisplayDialog("Voxel Bridge", error, "Cerrar");
                return;
            }
            EditorUtility.SetDirty(palette);
            serializedObject.Update();
        }

        private static void DrawValidationAndLut(VoxelSurfacePalette palette)
        {
            if (!palette.TryValidate(out string error))
                EditorGUILayout.HelpBox(error, MessageType.Error);
            else if (!VoxelPaletteLutGenerator.IsCurrent(palette))
                EditorGUILayout.HelpBox("La LUT de superficies está desactualizada.", MessageType.Warning);
            else
                EditorGUILayout.HelpBox("Paleta válida y LUT actualizada.", MessageType.Info);

            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("LUT generada", palette.GeneratedLut, typeof(Texture2D), false);
            if (GUILayout.Button("Regenerar LUT de superficies") &&
                !VoxelPaletteLutGenerator.TryRebuild(palette, out _, out error))
                EditorUtility.DisplayDialog("Voxel Bridge", error, "Cerrar");
        }

        private static void DrawRetiredIds(System.Collections.Generic.IReadOnlyList<int> retiredIds)
        {
            if (retiredIds == null || retiredIds.Count == 0) return;
            EditorGUILayout.HelpBox(
                "IDs retirados: " + string.Join(", ", retiredIds.Select(id => id.ToString())),
                MessageType.None);
        }
    }

    [CustomEditor(typeof(VoxelColorMappingProfile))]
    internal sealed class VoxelColorMappingProfileEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox(
                "El perfil limita los ColorIDs candidatos durante la vinculación de un .vox. " +
                "Los rangos son inclusivos y los IDs adicionales permiten combinar una base con acentos.",
                MessageType.Info);
            DrawDefaultInspector();

            var profile = (VoxelColorMappingProfile)target;
            if (!profile.TryGetAllowedColors(out VoxelColorDefinition[] colors, out string error))
            {
                EditorGUILayout.HelpBox(error, MessageType.Error);
                return;
            }

            EditorGUILayout.HelpBox(
                $"El perfil permite {colors.Length} ColorID activo(s). La distancia se calcula en OKLab; " +
                "los valores sobre el máximo no se asignan automáticamente.", MessageType.Info);
        }
    }
}
