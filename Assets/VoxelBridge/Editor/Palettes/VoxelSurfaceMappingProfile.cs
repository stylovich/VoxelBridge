using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    [CreateAssetMenu(fileName = "VoxelSurfaceMappingProfile", menuName = "Voxel Bridge/Surface Mapping Profile")]
    public sealed class VoxelSurfaceMappingProfile : ScriptableObject
    {
        public VoxelSurfacePalette surfacePalette;
        [Tooltip("Candidatos opacos no emisivos. Limitar el conjunto al contexto artístico para evitar coincidencias ambiguas.")]
        public List<int> candidateSurfaceIds = new() { 0, 3, 4, 5, 6, 7, 14, 15 };
        [Min(0)] public float metallicWeight = 1;
        [Min(0)] public float smoothnessWeight = 1;
        [Range(0, 1)] public float maximumDistance = .18f;
        [Range(0, 1)] public float minimumSeparation = .035f;

        internal VoxelSurfaceDefinition[] GetCandidates()
        {
            if (surfacePalette == null || !surfacePalette.TryValidate(out _))
                throw new InvalidDataException("Assign a valid surface palette to the surface mapping profile.");
            if (candidateSurfaceIds == null || candidateSurfaceIds.Count == 0 || candidateSurfaceIds.Count > 256 ||
                candidateSurfaceIds.Distinct().Count() != candidateSurfaceIds.Count)
                throw new InvalidDataException("Choose between 1 and 256 distinct SurfaceID candidates.");
            if (!float.IsFinite(metallicWeight) || !float.IsFinite(smoothnessWeight) || metallicWeight < 0 || smoothnessWeight < 0 ||
                !float.IsFinite(metallicWeight + smoothnessWeight) || metallicWeight + smoothnessWeight <= 0 ||
                !Unit(maximumDistance) || !Unit(minimumSeparation))
                throw new InvalidDataException("Mapping weights must be finite, nonnegative and not both zero; thresholds must be in 0..1.");
            return candidateSurfaceIds.OrderBy(id => id).Select(id =>
            {
                if (id < 0 || id > 255 || !surfacePalette.TryGetSurface(id, out var surface) ||
                    surface.RenderClass != VoxelSurfaceRenderClass.Opaque || surface.Emission > 0)
                    throw new InvalidDataException($"SurfaceID {id} is not an active opaque, non-emissive candidate.");
                return surface;
            }).ToArray();
        }

        private static bool Unit(float value) => float.IsFinite(value) && value >= 0 && value <= 1;
    }

    internal enum VoxelSurfaceDecision : byte { Unassigned, Explicit, EmissiveFallback, Automatic, Ambiguous, TooDistant, Unsupported }

    internal readonly struct VoxelSurfaceMatch
    {
        internal readonly int SurfaceId;
        internal readonly VoxelSurfaceDecision Decision;
        internal readonly float Distance;
        internal VoxelSurfaceMatch(int id, VoxelSurfaceDecision decision, float distance)
        { SurfaceId = id; Decision = decision; Distance = distance; }
    }

    internal sealed class VoxelSurfaceMatcher
    {
        private readonly VoxelSurfaceDefinition[] candidates;
        private readonly float metallicWeight, smoothnessWeight, maximumDistance, minimumSeparation;
        private readonly Dictionary<Vector2, VoxelSurfaceMatch> cache = new();
        internal VoxelSurfaceMatcher(VoxelSurfaceMappingProfile profile)
        {
            candidates = profile.GetCandidates();
            float sum = profile.metallicWeight + profile.smoothnessWeight;
            metallicWeight = profile.metallicWeight / sum; smoothnessWeight = profile.smoothnessWeight / sum;
            maximumDistance = profile.maximumDistance; minimumSeparation = profile.minimumSeparation;
        }

        internal VoxelSurfaceMatch Match(float metallic, float smoothness)
        {
            if (!float.IsFinite(metallic) || !float.IsFinite(smoothness) || metallic < 0 || metallic > 1 || smoothness < 0 || smoothness > 1)
                throw new ArgumentOutOfRangeException("PBR samples must be finite and in 0..1.");
            var key = new Vector2(metallic, smoothness);
            if (cache.TryGetValue(key, out var cached)) return cached;
            float best = float.PositiveInfinity, second = float.PositiveInfinity;
            int id = -1;
            foreach (var candidate in candidates)
            {
                float dm = metallic - candidate.Metallic, ds = smoothness - candidate.Smoothness;
                float distance = Mathf.Sqrt(metallicWeight * dm * dm + smoothnessWeight * ds * ds);
                if (distance < best) { second = best; best = distance; id = candidate.Id; }
                else second = Mathf.Min(second, distance);
            }
            var decision = best > maximumDistance ? VoxelSurfaceDecision.TooDistant :
                second - best <= 0.000001f || second - best < minimumSeparation ? VoxelSurfaceDecision.Ambiguous : VoxelSurfaceDecision.Automatic;
            var result = new VoxelSurfaceMatch(decision == VoxelSurfaceDecision.Automatic ? id : -1, decision, best);
            if (cache.Count >= 65536) cache.Clear();
            cache.Add(key, result);
            return result;
        }
    }

    [CustomEditor(typeof(VoxelSurfaceMappingProfile))]
    internal sealed class VoxelSurfaceMappingProfileEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var profile = (VoxelSurfaceMappingProfile)target;
            EditorGUILayout.HelpBox("Semejanza de metallic/smoothness, no identificación física. Los empates y las coincidencias débiles conservan el fallback. AO y emisión no participan en la distancia.", MessageType.Info);
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("surfacePalette"));
            var ids = serializedObject.FindProperty("candidateSurfaceIds");
            for (int i = 0; i < ids.arraySize; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    VoxelConversionProfileEditor.DrawSurface(EditorGUILayout.GetControlRect(), ids.GetArrayElementAtIndex(i), profile.surfacePalette, false);
                    if (GUILayout.Button("−", GUILayout.Width(24))) { ids.DeleteArrayElementAtIndex(i); break; }
                }
            }
            if (ids.arraySize < 256 && GUILayout.Button("Add Candidate")) { int i = ids.arraySize++; ids.GetArrayElementAtIndex(i).intValue = 0; }
            foreach (string field in new[] { "metallicWeight", "smoothnessWeight", "maximumDistance", "minimumSeparation" })
                EditorGUILayout.PropertyField(serializedObject.FindProperty(field));
            serializedObject.ApplyModifiedProperties();
            try { profile.GetCandidates(); }
            catch (Exception exception) { EditorGUILayout.HelpBox(exception.Message, MessageType.Error); }
        }
    }
}
