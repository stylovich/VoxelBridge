using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace LocalModels.EditorTools
{
    /// <summary>Temporary scene experiment, independent of Voxel Bridge and its assets.</summary>
    internal sealed class HierarchyLodPreviewWindow : EditorWindow
    {
        [SerializeField] private GameObject parent;
        [SerializeField] private int lodIndex = 2;
        [SerializeField] private bool usePhysicalSize = true;
        [SerializeField] private float targetSize = .125f;
        private readonly List<LODGroup> forcedGroups = new();
        private string status = "Sin previsualización activa.";

        // Read-only projection of the exported manifest; no Voxel Bridge assembly dependency.
        [Serializable] private sealed class FamilyInfo { public float baseVoxelSize; public LevelInfo[] lods; }
        [Serializable] private sealed class LevelInfo { public int lodIndex; public int multiplier; }

        [MenuItem("Tools/LocalModels/Preview LOD de jerarquía")]
        private static void Open()
        {
            var window = GetWindow<HierarchyLodPreviewWindow>("Preview LOD");
            window.minSize = new Vector2(420, 340);
            if (IsSceneObject(Selection.activeGameObject)) window.parent = Selection.activeGameObject;
        }

        private void OnEnable()
        {
            AssemblyReloadEvents.beforeAssemblyReload += RestoreAutomatic;
            EditorApplication.quitting += RestoreAutomatic;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private void OnDisable()
        {
            RestoreAutomatic();
            AssemblyReloadEvents.beforeAssemblyReload -= RestoreAutomatic;
            EditorApplication.quitting -= RestoreAutomatic;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        }

        private void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode) RestoreAutomatic();
        }

        private static bool IsSceneObject(GameObject root) => root && !EditorUtility.IsPersistent(root)
            && root.scene.IsValid() && root.scene.isLoaded;

        private void OnGUI()
        {
            EditorGUILayout.HelpBox("Fija un nivel existente para comparar el estilo. No borra LODs, " +
                "no modifica prefabs y no guarda cambios en la escena.", MessageType.None);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                parent = (GameObject)EditorGUILayout.ObjectField("GO padre", parent, typeof(GameObject), true);
                usePhysicalSize = EditorGUILayout.Toggle("Elegir por tamaño físico", usePhysicalSize);
                if (usePhysicalSize) targetSize = EditorGUILayout.FloatField("Celda mundial (m)", targetSize);
                else lodIndex = Mathf.Clamp(EditorGUILayout.IntField("Índice LOD", lodIndex), 0, 15);
                EditorGUILayout.HelpBox(usePhysicalSize
                    ? "Lee el manifiesto exportado y la escala mundial del LODGroup. Sólo fuerza niveles " +
                      "que coincidan con el tamaño solicitado; omite familias sin metadatos o con escala no uniforme."
                    : "Base 0,03125 y escala ×1: LOD2 = 0,125 m. Con escala ×2: LOD1 = 0,125 m. " +
                      "El índice por sí solo no garantiza un tamaño mundial.", MessageType.Info);
                using (new EditorGUI.DisabledScope(!IsSceneObject(parent)))
                {
                    if (GUILayout.Button("Previsualizar nivel fijo"))
                    {
                        if (usePhysicalSize && (!float.IsFinite(targetSize) || targetSize <= 0))
                            status = "El tamaño debe ser un número positivo y finito.";
                        else ApplyPreview(parent, lodIndex, usePhysicalSize ? targetSize : 0);
                    }
                }
            }
            using (new EditorGUI.DisabledScope(forcedGroups.Count == 0))
            {
                if (GUILayout.Button("Volver a LOD automático")) RestoreAutomatic();
            }
            EditorGUILayout.LabelField(status, EditorStyles.wordWrappedLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.HelpBox("Se restaura el modo automático al cerrar, recompilar o entrar en Play. " +
                "No combinar con otro control de ForceLOD. Esta prueba no ajusta la rejilla del shader " +
                "ni demuestra cómo quedaría una nueva conversión.", MessageType.None);
        }

        internal void ApplyPreview(GameObject root, int index, float physicalSize = 0)
        {
            if (!IsSceneObject(root)) throw new ArgumentException("Seleccionar un GO de escena.", nameof(root));
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("La prueba requiere Edit Mode.");
            if (index < 0 || index > 15) throw new ArgumentOutOfRangeException(nameof(index));
            if (!float.IsFinite(physicalSize) || physicalSize < 0) throw new ArgumentOutOfRangeException(nameof(physicalSize));
            RestoreAutomatic();
            int inactive = 0, missing = 0;
            var manifestCache = new Dictionary<string, FamilyInfo>();
            var chosen = new SortedDictionary<int, int>();
            try
            {
                foreach (var group in root.GetComponentsInChildren<LODGroup>(true))
                {
                    if (!group.enabled || !group.gameObject.activeInHierarchy) { inactive++; continue; }
                    var levels = group.GetLODs();
                    int selected = physicalSize > 0 ? ResolvePhysicalLevel(group, physicalSize, manifestCache) : index;
                    if (selected < 0 || selected >= levels.Length || !HasRenderer(levels[selected])) { missing++; continue; }
                    forcedGroups.Add(group);
                    group.ForceLOD(selected);
                    chosen.TryGetValue(selected, out int count); chosen[selected] = count + 1;
                }
            }
            catch
            {
                RestoreAutomatic();
                throw;
            }
            var summary = new List<string>();
            foreach (var pair in chosen) summary.Add($"LOD{pair.Key}: {pair.Value}");
            status = $"Fijados: {forcedGroups.Count}. Sin nivel compatible: {missing}. Inactivos: {inactive}.\n" + string.Join(" · ", summary);
            SceneView.RepaintAll();
            Repaint();
        }

        private static int ResolvePhysicalLevel(LODGroup group, float target, Dictionary<string, FamilyInfo> cache)
        {
            Vector3 scale = group.transform.lossyScale;
            float x = Mathf.Abs(scale.x), y = Mathf.Abs(scale.y), z = Mathf.Abs(scale.z);
            if (!float.IsFinite(x + y + z) || x < .00001f || Mathf.Abs(x - y) > x * .001f || Mathf.Abs(x - z) > x * .001f)
                return -1;
            // A sheared transform does not represent cubic world-space cells.
            var matrix = group.transform.localToWorldMatrix;
            Vector3 a = matrix.GetColumn(0), b = matrix.GetColumn(1), c = matrix.GetColumn(2);
            if (Mathf.Abs(Vector3.Dot(a.normalized, b.normalized)) > .001f ||
                Mathf.Abs(Vector3.Dot(a.normalized, c.normalized)) > .001f ||
                Mathf.Abs(Vector3.Dot(b.normalized, c.normalized)) > .001f) return -1;
            string prefab = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(group.gameObject);
            if (string.IsNullOrEmpty(prefab)) return -1;
            string path = Path.ChangeExtension(prefab, ".voxset.json");
            if (!cache.TryGetValue(path, out var family))
            {
                if (!File.Exists(path)) { cache[path] = null; return -1; }
                try { family = JsonUtility.FromJson<FamilyInfo>(File.ReadAllText(path)); }
                catch (Exception e) when (e is IOException || e is ArgumentException || e is UnauthorizedAccessException)
                {
                    Debug.LogWarning($"No se pudo leer el manifiesto de preview '{path}': {e.Message}");
                    family = null;
                }
                cache[path] = family;
            }
            if (family?.lods == null || !float.IsFinite(family.baseVoxelSize) || family.baseVoxelSize <= 0) return -1;
            foreach (var level in family.lods)
            {
                if (level == null || level.multiplier <= 0) continue;
                float size = family.baseVoxelSize * level.multiplier * x;
                if (float.IsFinite(size) && Mathf.Abs(size - target) <= target * .001f) return level.lodIndex;
            }
            return -1;
        }

        private static bool HasRenderer(LOD level)
        {
            foreach (var renderer in level.renderers)
                if (renderer) return true;
            return false;
        }

        internal void RestoreAutomatic()
        {
            foreach (var group in forcedGroups)
                if (group) group.ForceLOD(-1);
            forcedGroups.Clear();
            status = "Sin previsualización activa. LOD automático.";
            SceneView.RepaintAll();
            Repaint();
        }
    }
}
