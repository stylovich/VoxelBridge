using System;
using System.Collections.Generic;
using DynamicGI.Radiance;
using UnityEngine;

namespace DynamicGI.Contributors
{
    /// <summary>
    /// Runtime/mod-facing contract for an isotropic emissive GI source. Registration
    /// and changes invalidate only the source influence bounds; no probe placement is
    /// exposed to content authors.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class GIEmissiveContributor : MonoBehaviour
    {
        private static readonly HashSet<GIEmissiveContributor> ActiveSet = new();

        internal static IReadOnlyCollection<GIEmissiveContributor> ActiveContributors => ActiveSet;
        internal static event Action<GIEmissiveContributor, EmissiveContributorChange> RegistryChanged;

        [SerializeField] private bool contributes = true;
        [SerializeField, ColorUsage(false, true)] private Color emissionColor = new(1f, 0.05f, 0.8f, 1f);
        [SerializeField, Min(0f)] private float emissionIntensity = 2f;
        [SerializeField, Min(0.1f)] private float influenceRange = 4f;
        [SerializeField, Range(0, 3)] private int maximumCascadeIndex = 1;
        [SerializeField] private bool trackBoundsChanges = true;
        [SerializeField] private Renderer[] renderers = Array.Empty<Renderer>();
        [SerializeField] private Vector3 fallbackLocalBoundsCenter;
        [SerializeField] private Vector3 fallbackLocalBoundsSize = Vector3.one;
        [SerializeField] private bool showBoundsGizmos = true;

        private Bounds cachedSourceBounds;
        private Bounds cachedInfluenceBounds;
        private int cachedMaximumCascadeIndex;
        private bool hasCachedState;

        public bool Contributes => contributes && emissionIntensity > 0f && isActiveAndEnabled;
        public Color EmissionColor => emissionColor;
        public float EmissionIntensity => emissionIntensity;
        public float InfluenceRange => influenceRange;
        public int MaximumCascadeIndex => maximumCascadeIndex;
        public IReadOnlyList<Renderer> Renderers => renderers;
        public Bounds SourceBounds => CalculateSourceBounds();
        public Bounds InfluenceBounds => ExpandBounds(SourceBounds, influenceRange);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRegistry()
        {
            ActiveSet.Clear();
            RegistryChanged = null;
        }

        private void Reset() => RefreshRendererList();

        private void OnEnable()
        {
            EnsureRendererList();
            CacheCurrentState();
            if (ActiveSet.Add(this))
                RegistryChanged?.Invoke(this, new EmissiveContributorChange(cachedInfluenceBounds, maximumCascadeIndex));
        }

        private void OnDisable()
        {
            Bounds dirty = hasCachedState ? cachedInfluenceBounds : InfluenceBounds;
            int cascade = hasCachedState ? cachedMaximumCascadeIndex : maximumCascadeIndex;
            if (ActiveSet.Remove(this))
                RegistryChanged?.Invoke(this, new EmissiveContributorChange(dirty, cascade));
            hasCachedState = false;
        }

        private void LateUpdate()
        {
            if (!trackBoundsChanges || !hasCachedState)
                return;
            Bounds current = CalculateSourceBounds();
            if (!BoundsApproximatelyEqual(current, cachedSourceBounds))
                NotifyEmissionChanged();
        }

        private void OnValidate()
        {
            emissionIntensity = Mathf.Max(0f, emissionIntensity);
            influenceRange = Mathf.Max(0.1f, influenceRange);
            maximumCascadeIndex = Mathf.Clamp(maximumCascadeIndex, 0, 3);
            fallbackLocalBoundsSize = new Vector3(
                Mathf.Max(0.01f, fallbackLocalBoundsSize.x),
                Mathf.Max(0.01f, fallbackLocalBoundsSize.y),
                Mathf.Max(0.01f, fallbackLocalBoundsSize.z));
            renderers ??= Array.Empty<Renderer>();
            if (ActiveSet.Contains(this))
                NotifyEmissionChanged();
        }

        /// <summary>Refreshes child renderers during authoring or mod registration.</summary>
        [ContextMenu("Refresh Renderer List")]
        public void RefreshRendererList()
        {
            renderers = GetComponentsInChildren<Renderer>(true);
            if (ActiveSet.Contains(this))
                NotifyEmissionChanged();
        }

        /// <summary>
        /// Runtime/mod-friendly setup. The influence range extends beyond renderer
        /// bounds and the maximum cascade defaults to the two nearest levels.
        /// </summary>
        public void Configure(
            Renderer[] sourceRenderers,
            Color color,
            float intensity,
            float range,
            int maximumCascade = 1,
            bool enableContribution = true)
        {
            Bounds previous = hasCachedState ? cachedInfluenceBounds : InfluenceBounds;
            int previousCascade = hasCachedState ? cachedMaximumCascadeIndex : maximumCascadeIndex;
            renderers = sourceRenderers ?? Array.Empty<Renderer>();
            emissionColor = color;
            emissionIntensity = Mathf.Max(0f, intensity);
            influenceRange = Mathf.Max(0.1f, range);
            maximumCascadeIndex = Mathf.Clamp(maximumCascade, 0, 3);
            contributes = enableContribution;
            CacheCurrentState();
            previous.Encapsulate(cachedInfluenceBounds);
            if (ActiveSet.Contains(this))
            {
                RegistryChanged?.Invoke(this, new EmissiveContributorChange(
                    previous,
                    Mathf.Max(previousCascade, maximumCascadeIndex)));
            }
        }

        public void SetContributionEnabled(bool enabled)
        {
            if (contributes == enabled)
                return;
            contributes = enabled;
            NotifyEmissionChanged();
        }

        /// <summary>Call after changing emission data not routed through Configure.</summary>
        [ContextMenu("Notify Emission Changed")]
        public void NotifyEmissionChanged()
        {
            Bounds dirty = hasCachedState ? cachedInfluenceBounds : InfluenceBounds;
            int cascade = hasCachedState ? cachedMaximumCascadeIndex : maximumCascadeIndex;
            CacheCurrentState();
            dirty.Encapsulate(cachedInfluenceBounds);
            if (ActiveSet.Contains(this))
            {
                RegistryChanged?.Invoke(this, new EmissiveContributorChange(
                    dirty,
                    Mathf.Max(cascade, maximumCascadeIndex)));
            }
        }

        internal bool TryBuildGpuData(out EmissiveContributorGpuData value)
        {
            value = default;
            if (!Contributes)
                return false;
            Bounds bounds = SourceBounds;
            Color linear = emissionColor.linear;
            Vector3 radiance = new(linear.r * emissionIntensity, linear.g * emissionIntensity, linear.b * emissionIntensity);
            value.CenterAndRange = new Vector4(bounds.center.x, bounds.center.y, bounds.center.z, influenceRange);
            value.Radiance = new Vector4(radiance.x, radiance.y, radiance.z, 0f);
            value.BoundsExtentsAndMaxCascade = new Vector4(
                bounds.extents.x,
                bounds.extents.y,
                bounds.extents.z,
                maximumCascadeIndex);
            return true;
        }

        private void EnsureRendererList()
        {
            if (renderers == null || renderers.Length == 0)
                renderers = GetComponentsInChildren<Renderer>(true);
        }

        private void CacheCurrentState()
        {
            cachedSourceBounds = CalculateSourceBounds();
            cachedInfluenceBounds = ExpandBounds(cachedSourceBounds, influenceRange);
            cachedMaximumCascadeIndex = maximumCascadeIndex;
            hasCachedState = true;
        }

        private Bounds CalculateSourceBounds()
        {
            Bounds bounds = default;
            bool found = false;
            if (renderers != null)
            {
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer candidate = renderers[i];
                    if (candidate == null)
                        continue;
                    if (!found)
                    {
                        bounds = candidate.bounds;
                        found = true;
                    }
                    else
                    {
                        bounds.Encapsulate(candidate.bounds);
                    }
                }
            }
            return found ? bounds : TransformLocalBounds(fallbackLocalBoundsCenter, fallbackLocalBoundsSize);
        }

        private Bounds TransformLocalBounds(Vector3 localCenter, Vector3 localSize)
        {
            Matrix4x4 matrix = transform.localToWorldMatrix;
            Vector3 localExtents = localSize * 0.5f;
            Vector3 axisX = matrix.MultiplyVector(new Vector3(localExtents.x, 0f, 0f));
            Vector3 axisY = matrix.MultiplyVector(new Vector3(0f, localExtents.y, 0f));
            Vector3 axisZ = matrix.MultiplyVector(new Vector3(0f, 0f, localExtents.z));
            Vector3 extents = Abs(axisX) + Abs(axisY) + Abs(axisZ);
            return new Bounds(matrix.MultiplyPoint3x4(localCenter), extents * 2f);
        }

        private void OnDrawGizmosSelected()
        {
            if (!showBoundsGizmos)
                return;
            Bounds source = SourceBounds;
            Gizmos.color = new Color(emissionColor.r, emissionColor.g, emissionColor.b, 0.95f);
            Gizmos.DrawWireCube(source.center, source.size);
            Bounds influence = ExpandBounds(source, influenceRange);
            Gizmos.color = new Color(emissionColor.r, emissionColor.g, emissionColor.b, 0.28f);
            Gizmos.DrawWireCube(influence.center, influence.size);
        }

        private static Bounds ExpandBounds(Bounds source, float range)
        {
            source.Expand(Mathf.Max(0.1f, range) * 2f);
            return source;
        }

        private static bool BoundsApproximatelyEqual(Bounds a, Bounds b) =>
            (a.center - b.center).sqrMagnitude <= 0.000001f &&
            (a.size - b.size).sqrMagnitude <= 0.000001f;

        private static Vector3 Abs(Vector3 value) => new(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
    }

    internal readonly struct EmissiveContributorChange
    {
        public readonly Bounds InfluenceBounds;
        public readonly int MaximumCascadeIndex;

        public EmissiveContributorChange(Bounds influenceBounds, int maximumCascadeIndex)
        {
            InfluenceBounds = influenceBounds;
            MaximumCascadeIndex = maximumCascadeIndex;
        }
    }
}
