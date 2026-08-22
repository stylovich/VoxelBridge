using System;
using System.Collections.Generic;
using DynamicGI.Geometry;
using UnityEngine;

namespace DynamicGI.Contributors
{
    /// <summary>
    /// Explicit geometry registration contract used by runtime content and future mods.
    /// Add this component to movable/replaced geometry and call NotifyGeometryChanged
    /// after changing its mesh, renderer state, or child hierarchy.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GIGeometryContributor : MonoBehaviour
    {
        private static readonly HashSet<GIGeometryContributor> ActiveSet = new();

        internal static IReadOnlyCollection<GIGeometryContributor> ActiveContributors => ActiveSet;
        internal static event Action<GIGeometryContributor, Bounds> RegistryChanged;

        [SerializeField] private GeometryContributionType contributionType = GeometryContributionType.Dynamic;
        [SerializeField] private bool contributes = true;
        [SerializeField] private bool trackTransformChanges = true;
        [SerializeField] private Renderer[] renderers = Array.Empty<Renderer>();

        private Bounds cachedBounds;
        private bool hasCachedBounds;

        public GeometryContributionType ContributionType => contributionType;
        public bool Contributes => contributes && isActiveAndEnabled;
        public IReadOnlyList<Renderer> Renderers => renderers;

        public Bounds WorldBounds
        {
            get
            {
                if (TryCalculateBounds(out Bounds bounds))
                    return bounds;

                return new Bounds(transform.position, Vector3.zero);
            }
        }

        private void Reset()
        {
            RefreshRendererList();
        }

        private void OnEnable()
        {
            EnsureRendererList();
            TryCalculateBounds(out cachedBounds);
            hasCachedBounds = true;
            ActiveSet.Add(this);
            RegistryChanged?.Invoke(this, cachedBounds);
        }

        private void OnDisable()
        {
            Bounds dirtyBounds = hasCachedBounds ? cachedBounds : WorldBounds;
            ActiveSet.Remove(this);
            RegistryChanged?.Invoke(this, dirtyBounds);
            hasCachedBounds = false;
        }

        private void LateUpdate()
        {
            if (!trackTransformChanges || !transform.hasChanged)
                return;

            transform.hasChanged = false;
            NotifyGeometryChanged();
        }

        private void OnValidate()
        {
            if (renderers == null)
                renderers = Array.Empty<Renderer>();

            if (!isActiveAndEnabled)
                return;

            Bounds previous = hasCachedBounds ? cachedBounds : WorldBounds;
            TryCalculateBounds(out cachedBounds);
            hasCachedBounds = true;
            previous.Encapsulate(cachedBounds);
            RegistryChanged?.Invoke(this, previous);
        }

        /// <summary>
        /// Rebuilds the cached renderer list from this object and its children.
        /// This is intended for setup or mod registration, not a per-frame call.
        /// </summary>
        [ContextMenu("Refresh Renderer List")]
        public void RefreshRendererList()
        {
            Bounds previous = hasCachedBounds ? cachedBounds : WorldBounds;
            renderers = GetComponentsInChildren<Renderer>(true);
            TryCalculateBounds(out cachedBounds);
            hasCachedBounds = true;
            previous.Encapsulate(cachedBounds);

            if (isActiveAndEnabled)
                RegistryChanged?.Invoke(this, previous);
        }

        /// <summary>
        /// Invalidates the union of the previous and current bounds. Call this after
        /// replacing a mesh, opening a door without transform tracking, or changing
        /// the set of enabled renderers.
        /// </summary>
        [ContextMenu("Notify Geometry Changed")]
        public void NotifyGeometryChanged()
        {
            Bounds dirtyBounds = hasCachedBounds ? cachedBounds : WorldBounds;
            if (TryCalculateBounds(out Bounds current))
            {
                dirtyBounds.Encapsulate(current);
                cachedBounds = current;
                hasCachedBounds = true;
            }

            RegistryChanged?.Invoke(this, dirtyBounds);
        }

        /// <summary>
        /// Runtime/mod-friendly configuration entry point. No probes are required.
        /// </summary>
        public void Configure(
            Renderer[] geometryRenderers,
            GeometryContributionType type,
            bool enableContribution = true,
            bool monitorTransform = true)
        {
            Bounds previous = hasCachedBounds ? cachedBounds : WorldBounds;
            renderers = geometryRenderers ?? Array.Empty<Renderer>();
            contributionType = type;
            contributes = enableContribution;
            trackTransformChanges = monitorTransform;
            TryCalculateBounds(out cachedBounds);
            hasCachedBounds = true;
            previous.Encapsulate(cachedBounds);

            if (isActiveAndEnabled)
                RegistryChanged?.Invoke(this, previous);
        }

        private void EnsureRendererList()
        {
            if (renderers == null || renderers.Length == 0)
                renderers = GetComponentsInChildren<Renderer>(true);
        }

        private bool TryCalculateBounds(out Bounds bounds)
        {
            bounds = default;
            bool found = false;

            if (renderers == null)
                return false;

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

            return found;
        }
    }
}
