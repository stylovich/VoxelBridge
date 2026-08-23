using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace DynamicGI.Radiance
{
    [Serializable]
    public sealed class RadianceCascadeSettings
    {
        [SerializeField] private string name = "Cascade";
        [SerializeField] private bool enabled = true;
        [SerializeField, Range(8, 64)] private int horizontalResolution = 16;
        [SerializeField, Range(4, 32)] private int verticalResolution = 8;
        [SerializeField, Min(0.25f)] private float probeSpacing = 0.5f;
        [SerializeField, Range(1, 8)] private int tileResolution = 2;
        [SerializeField, Min(1)] private int updateIntervalFrames = 1;
        [SerializeField, Min(1)] private int updateBudgetTiles = 16;

        public string Name => string.IsNullOrWhiteSpace(name) ? "Cascade" : name;
        public bool Enabled => enabled;
        public int HorizontalResolution => horizontalResolution;
        public int VerticalResolution => verticalResolution;
        public float ProbeSpacing => probeSpacing;
        public int TileResolution => tileResolution;
        public int UpdateIntervalFrames => updateIntervalFrames;
        public int UpdateBudgetTiles => updateBudgetTiles;

        public RadianceCascadeSettings()
        {
        }

        public RadianceCascadeSettings(
            string name,
            int horizontalResolution,
            int verticalResolution,
            float probeSpacing,
            int tileResolution,
            int updateIntervalFrames,
            int updateBudgetTiles)
        {
            this.name = name;
            this.horizontalResolution = horizontalResolution;
            this.verticalResolution = verticalResolution;
            this.probeSpacing = probeSpacing;
            this.tileResolution = tileResolution;
            this.updateIntervalFrames = updateIntervalFrames;
            this.updateBudgetTiles = updateBudgetTiles;
        }

        public void Sanitize()
        {
            horizontalResolution = Mathf.ClosestPowerOfTwo(Mathf.Clamp(horizontalResolution, 8, 64));
            verticalResolution = Mathf.ClosestPowerOfTwo(Mathf.Clamp(verticalResolution, 4, 32));
            probeSpacing = Mathf.Max(0.25f, probeSpacing);
            tileResolution = Mathf.ClosestPowerOfTwo(Mathf.Clamp(tileResolution, 1, 8));
            tileResolution = Mathf.Min(tileResolution, verticalResolution);
            updateIntervalFrames = Mathf.Max(1, updateIntervalFrames);
            updateBudgetTiles = Mathf.Max(1, updateBudgetTiles);
        }

    }

    /// <summary>
    /// Runtime storage and toroidal addressing for one radiance clipmap level. Dirty
    /// work is keyed by snapped global tile coordinates, so pending updates survive
    /// scrolling and only tiles that leave the window are discarded.
    /// </summary>
    public sealed class RadianceCascade : IDisposable
    {
        private readonly HashSet<Vector3Int> dirtySet = new();
        private Queue<Vector3Int> dirtyQueue = new();
        private Queue<Vector3Int> queueScratch = new();
        private readonly RenderTexture[] textures = new RenderTexture[6];
        private readonly GraphicsFormat textureFormat;

        private Vector3Int originGlobalTile;
        private Vector3Int ringOffset;
        private bool hasOrigin;

        public int Index { get; }
        public string Name { get; }
        public Vector3Int Resolution { get; }
        public Vector3Int TileGridResolution { get; }
        public float ProbeSpacing { get; }
        public int TileResolution { get; }
        public int UpdateIntervalFrames { get; }
        public int UpdateBudgetTiles { get; }
        public Vector3Int OriginGlobalTile => originGlobalTile;
        public Vector3Int RingOffset => ringOffset;
        public Vector3 OriginWS => (Vector3)originGlobalTile * TileWorldSize;
        public Vector3 SizeWS => Vector3.Scale((Vector3)Resolution, Vector3.one * ProbeSpacing);
        public Bounds WorldBounds => new(OriginWS + SizeWS * 0.5f, SizeWS);
        public float TileWorldSize => ProbeSpacing * TileResolution;
        public int ProbeCount => Resolution.x * Resolution.y * Resolution.z;
        public int TotalTileCount => TileGridResolution.x * TileGridResolution.y * TileGridResolution.z;
        public int DirtyTileCount => dirtySet.Count;
        public int LastExposedTiles { get; private set; }
        public int LastExposedProbes { get; private set; }
        public int LastRecycledProbes { get; private set; }
        public IReadOnlyList<RenderTexture> Textures => textures;

        public RadianceCascade(int index, RadianceCascadeSettings settings, GraphicsFormat format, Vector3 targetPosition)
        {
            settings.Sanitize();
            Index = index;
            Name = settings.Name;
            ProbeSpacing = settings.ProbeSpacing;
            TileResolution = settings.TileResolution;
            UpdateIntervalFrames = settings.UpdateIntervalFrames;
            UpdateBudgetTiles = settings.UpdateBudgetTiles;
            Resolution = new Vector3Int(
                settings.HorizontalResolution,
                settings.VerticalResolution,
                settings.HorizontalResolution);
            TileGridResolution = new Vector3Int(
                Resolution.x / TileResolution,
                Resolution.y / TileResolution,
                Resolution.z / TileResolution);
            textureFormat = format;

            for (int i = 0; i < textures.Length; i++)
                textures[i] = CreateTexture(i);
            ResetToTarget(targetPosition);
        }

        public bool UpdateOrigin(Vector3 targetPosition)
        {
            Vector3Int desired = CalculateOriginGlobalTile(targetPosition);
            LastExposedTiles = 0;
            LastExposedProbes = 0;
            LastRecycledProbes = ProbeCount;
            if (hasOrigin && desired == originGlobalTile)
                return false;

            if (!hasOrigin)
            {
                ResetToTarget(targetPosition);
                return true;
            }

            Vector3Int previousOrigin = originGlobalTile;
            Vector3Int deltaTiles = desired - previousOrigin;
            originGlobalTile = desired;

            if (Mathf.Abs(deltaTiles.x) >= TileGridResolution.x ||
                Mathf.Abs(deltaTiles.y) >= TileGridResolution.y ||
                Mathf.Abs(deltaTiles.z) >= TileGridResolution.z)
            {
                ringOffset = Vector3Int.zero;
                dirtyQueue.Clear();
                dirtySet.Clear();
                EnqueueAllTiles();
                LastExposedTiles = TotalTileCount;
                LastExposedProbes = ProbeCount;
                LastRecycledProbes = 0;
                return true;
            }

            ringOffset = PositiveModulo(
                ringOffset + deltaTiles * TileResolution,
                Resolution);
            FilterPendingTilesToCurrentWindow();

            for (int z = 0; z < TileGridResolution.z; z++)
            for (int y = 0; y < TileGridResolution.y; y++)
            for (int x = 0; x < TileGridResolution.x; x++)
            {
                Vector3Int globalTile = originGlobalTile + new Vector3Int(x, y, z);
                if (!ContainsGlobalTile(globalTile, previousOrigin))
                {
                    if (EnqueueGlobalTile(globalTile))
                        LastExposedTiles++;
                }
            }

            int probesPerTile = TileResolution * TileResolution * TileResolution;
            LastExposedProbes = Mathf.Min(ProbeCount, LastExposedTiles * probesPerTile);
            LastRecycledProbes = ProbeCount - LastExposedProbes;
            return true;
        }

        public void ResetToTarget(Vector3 targetPosition)
        {
            originGlobalTile = CalculateOriginGlobalTile(targetPosition);
            ringOffset = Vector3Int.zero;
            hasOrigin = true;
            dirtyQueue.Clear();
            dirtySet.Clear();
            EnqueueAllTiles();
            LastExposedTiles = TotalTileCount;
            LastExposedProbes = ProbeCount;
            LastRecycledProbes = 0;
        }

        public void InvalidateAll()
        {
            EnqueueAllTiles();
        }

        public void InvalidateWorldBounds(Bounds worldBounds)
        {
            Bounds intersection = WorldBounds;
            Vector3 minimum = Vector3.Max(intersection.min, worldBounds.min);
            Vector3 maximum = Vector3.Min(intersection.max, worldBounds.max);
            if (minimum.x >= maximum.x || minimum.y >= maximum.y || minimum.z >= maximum.z)
                return;

            float tileSize = TileWorldSize;
            Vector3Int minimumGlobal = FloorToInt(minimum / tileSize);
            Vector3 adjustedMaximum = maximum - Vector3.one * (tileSize * 0.0001f);
            Vector3Int maximumGlobal = FloorToInt(adjustedMaximum / tileSize);
            Vector3Int windowMaximum = originGlobalTile + TileGridResolution - Vector3Int.one;
            minimumGlobal = Vector3Int.Max(minimumGlobal, originGlobalTile);
            maximumGlobal = Vector3Int.Min(maximumGlobal, windowMaximum);

            for (int z = minimumGlobal.z; z <= maximumGlobal.z; z++)
            for (int y = minimumGlobal.y; y <= maximumGlobal.y; y++)
            for (int x = minimumGlobal.x; x <= maximumGlobal.x; x++)
                EnqueueGlobalTile(new Vector3Int(x, y, z));
        }

        public bool TryDequeueDirty(out Vector3Int globalTile, out Vector3Int localTile)
        {
            while (dirtyQueue.Count > 0)
            {
                globalTile = dirtyQueue.Dequeue();
                if (!dirtySet.Remove(globalTile) || !ContainsGlobalTile(globalTile, originGlobalTile))
                    continue;
                localTile = globalTile - originGlobalTile;
                return true;
            }
            globalTile = default;
            localTile = default;
            return false;
        }

        public void GetDirtyTileBounds(List<Bounds> destination)
        {
            if (destination == null)
                return;
            destination.Clear();
            foreach (Vector3Int globalTile in dirtySet)
            {
                if (ContainsGlobalTile(globalTile, originGlobalTile))
                    destination.Add(GetGlobalTileBounds(globalTile));
            }
        }

        public Bounds GetGlobalTileBounds(Vector3Int globalTile)
        {
            Vector3 minimum = (Vector3)globalTile * TileWorldSize;
            return new Bounds(minimum + Vector3.one * TileWorldSize * 0.5f, Vector3.one * TileWorldSize);
        }

        public long EstimateGpuBytes()
        {
            long bytesPerTexel = textureFormat == GraphicsFormat.R16G16B16A16_SFloat ? 8L : 16L;
            return (long)ProbeCount * bytesPerTexel * textures.Length;
        }

        public void Dispose()
        {
            dirtyQueue.Clear();
            queueScratch.Clear();
            dirtySet.Clear();
            for (int i = 0; i < textures.Length; i++)
            {
                RenderTexture texture = textures[i];
                if (texture == null)
                    continue;
                texture.Release();
                if (Application.isPlaying)
                    UnityEngine.Object.Destroy(texture);
                else
                    UnityEngine.Object.DestroyImmediate(texture);
                textures[i] = null;
            }
        }

        private RenderTexture CreateTexture(int directionIndex)
        {
            RenderTextureDescriptor descriptor = new(Resolution.x, Resolution.y)
            {
                dimension = TextureDimension.Tex3D,
                volumeDepth = Resolution.z,
                graphicsFormat = textureFormat,
                depthBufferBits = 0,
                msaaSamples = 1,
                enableRandomWrite = true,
                useMipMap = false,
                autoGenerateMips = false,
                sRGB = false
            };
            RenderTexture texture = new(descriptor)
            {
                name = $"Dynamic GI {Name} {DirectionName(directionIndex)} {Resolution.x}x{Resolution.y}x{Resolution.z}",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
                hideFlags = HideFlags.HideAndDontSave
            };
            if (!texture.Create())
                throw new InvalidOperationException($"Could not create radiance cascade texture {texture.name}.");
            return texture;
        }

        private Vector3Int CalculateOriginGlobalTile(Vector3 targetPosition)
        {
            Vector3Int targetTile = FloorToInt(targetPosition / TileWorldSize);
            return targetTile - new Vector3Int(
                TileGridResolution.x / 2,
                TileGridResolution.y / 2,
                TileGridResolution.z / 2);
        }

        private void EnqueueAllTiles()
        {
            for (int z = 0; z < TileGridResolution.z; z++)
            for (int y = 0; y < TileGridResolution.y; y++)
            for (int x = 0; x < TileGridResolution.x; x++)
                EnqueueGlobalTile(originGlobalTile + new Vector3Int(x, y, z));
        }

        private bool EnqueueGlobalTile(Vector3Int globalTile)
        {
            if (!dirtySet.Add(globalTile))
                return false;
            dirtyQueue.Enqueue(globalTile);
            return true;
        }

        private void FilterPendingTilesToCurrentWindow()
        {
            queueScratch.Clear();
            dirtySet.Clear();
            while (dirtyQueue.Count > 0)
            {
                Vector3Int tile = dirtyQueue.Dequeue();
                if (ContainsGlobalTile(tile, originGlobalTile) && dirtySet.Add(tile))
                    queueScratch.Enqueue(tile);
            }
            Queue<Vector3Int> swap = dirtyQueue;
            dirtyQueue = queueScratch;
            queueScratch = swap;
        }

        private bool ContainsGlobalTile(Vector3Int value, Vector3Int windowOrigin)
        {
            Vector3Int relative = value - windowOrigin;
            return relative.x >= 0 && relative.y >= 0 && relative.z >= 0 &&
                   relative.x < TileGridResolution.x &&
                   relative.y < TileGridResolution.y &&
                   relative.z < TileGridResolution.z;
        }

        private static Vector3Int FloorToInt(Vector3 value) => new(
            Mathf.FloorToInt(value.x),
            Mathf.FloorToInt(value.y),
            Mathf.FloorToInt(value.z));

        private static Vector3Int PositiveModulo(Vector3Int value, Vector3Int modulo) => new(
            ((value.x % modulo.x) + modulo.x) % modulo.x,
            ((value.y % modulo.y) + modulo.y) % modulo.y,
            ((value.z % modulo.z) + modulo.z) % modulo.z);

        private static string DirectionName(int index) => index switch
        {
            0 => "+X",
            1 => "-X",
            2 => "+Y",
            3 => "-Y",
            4 => "+Z",
            _ => "-Z"
        };
    }

    public readonly struct RadianceCascadeRuntimeStats
    {
        public readonly int Index;
        public readonly string Name;
        public readonly Vector3Int Resolution;
        public readonly float Spacing;
        public readonly Bounds Bounds;
        public readonly Vector3Int RingOffset;
        public readonly int DirtyTiles;
        public readonly int TotalTiles;
        public readonly int ExposedProbes;
        public readonly int RecycledProbes;
        public readonly long EstimatedGpuBytes;

        public RadianceCascadeRuntimeStats(RadianceCascade cascade)
        {
            Index = cascade.Index;
            Name = cascade.Name;
            Resolution = cascade.Resolution;
            Spacing = cascade.ProbeSpacing;
            Bounds = cascade.WorldBounds;
            RingOffset = cascade.RingOffset;
            DirtyTiles = cascade.DirtyTileCount;
            TotalTiles = cascade.TotalTileCount;
            ExposedProbes = cascade.LastExposedProbes;
            RecycledProbes = cascade.LastRecycledProbes;
            EstimatedGpuBytes = cascade.EstimateGpuBytes();
        }
    }
}
