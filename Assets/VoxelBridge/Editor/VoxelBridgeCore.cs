using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    internal enum VoxelColorMode
    {
        MaterialAndTexture,
        MaterialOnly,
        SingleColor
    }

    internal enum VoxelLodGenerationMode
    {
        SourceMesh,
        DuplicateParent,
        ReduceParent
    }

    [Serializable]
    internal sealed class VoxelChunkMetadata
    {
        public int modelIndex;
        public Vector3Int gridOffset;
        public Vector3Int gridSize;
        public int voxelCount;
    }

    [Serializable]
    internal sealed class VoxelBridgeMetadata
    {
        public VoxelSourceAxes sourceAxes;
        public int formatVersion = 3;
        public string sourceName;
        public string sourceAssetPath;
        public int resolution;
        public int padding;
        public bool fillInterior;
        public bool hideInternalCavities = true;
        public float voxelSize;
        public Vector3 gridOrigin;
        public Vector3Int unityGridSize;
        public Vector3Int voxGridSize;
        public string axisMapping = "VOX(x,y,z) = UnityGrid(x,z,y)";
        public int voxelCount;
        public int paletteColorCount;
        public string familyId;
        public string lodSetAssetPath;
        public int lodIndex;
        public int lodMultiplier = 1;
        public VoxelLodGenerationMode lodGenerationMode;
        public string parentVoxAssetPath;
        public float baseVoxelSize;
        public int chunkCellSize = 256;
        public Vector3 sourceBoundsMin;
        public Vector3 sourceBoundsMax;
        public Vector3 importGridOrigin;
        public Vector3Int importGridSize;
        public VoxelChunkMetadata[] chunks;
        public VoxelSemanticMetadata semantic;
        public string retainedGeometryGuid;
    }

    [Serializable]
    internal sealed class VoxelSemanticSlotMetadata
    {
        public int slot;
        public int colorId;
        public int surfaceId;
        public Color32 displayColor;
    }

    [Serializable]
    internal sealed class VoxelSemanticMetadata
    {
        public int formatVersion = 1;
        public string colorPaletteGuid;
        public string colorPaletteAssetPath;
        public string colorPaletteHash;
        public string colorMappingProfileGuid;
        public string colorMappingProfileAssetPath;
        public string surfacePaletteGuid;
        public string surfacePaletteAssetPath;
        public string surfacePaletteHash;
        public string slotTableHash;
        public VoxelSemanticSlotMetadata[] slots;
    }

    [Serializable]
    internal sealed class VoxelLodEntry
    {
        public int lodIndex;
        public int multiplier = 1;
        public VoxelLodGenerationMode generationMode;
        public string voxAssetPath;
        public float screenRelativeTransitionHeight;
        public string sourceGuid;
        public string parentSourceHash;
        public string builtSourceHash;
        public string[] meshGuids;
    }

    [Serializable]
    internal sealed class VoxelImpostorEntry
    {
        public string assetPath;
        public string profileAssetPath;
        public VoxelImpostorQuality quality;
        public string amplifyVersion;
        public int sourceLodIndex;
        public float cullScreenHeight;
    }

    [Serializable]
    internal sealed class VoxelLodSetManifest
    {
        public bool normalizedScale;
        public Vector3 bakedRootScale = Vector3.one;
        public VoxelSourceAxes sourceAxes;
        public string retainedGeometryGuid;
        public int formatVersion = 4;
        public string familyId;
        public string sourceName;
        public string sourceAssetPath;
        public float baseVoxelSize;
        public int initialVoxelMultiplier = 1;
        public int chunkCellSize;
        public string profileAssetPath;
        public string prefabAssetPath;
        public float lodGroupSize;
        public VoxelLodEntry[] lods;
        public VoxelImpostorEntry impostor;
        public bool impostorDisabled;
        public bool productionMeshes;
        public string profileGuid;
        public string prefabGuid;
    }

    internal sealed class VoxelGrid
    {
        public readonly Vector3Int Size;
        public readonly Vector3 Origin;
        public readonly float VoxelSize;
        public readonly bool[] Occupied;
        public readonly Color32[] Colors;
        public readonly ushort[] SemanticIds;

        public bool IsSemantic => SemanticIds != null;

        public VoxelGrid(Vector3Int size, Vector3 origin, float voxelSize, bool semantic = false)
        {
            Size = size;
            Origin = origin;
            VoxelSize = voxelSize;
            int count = checked(size.x * size.y * size.z);
            Occupied = new bool[count];
            if (semantic)
                SemanticIds = new ushort[count];
            else
                Colors = new Color32[count];
        }

        public int Index(int x, int y, int z)
        {
            return x + Size.x * (y + Size.y * z);
        }

        public void Coordinates(int index, out int x, out int y, out int z)
        {
            x = index % Size.x;
            int yz = index / Size.x;
            y = yz % Size.y;
            z = yz / Size.y;
        }

        public int CountOccupied()
        {
            int count = 0;
            for (int i = 0; i < Occupied.Length; i++)
                if (Occupied[i]) count++;
            return count;
        }
    }

    internal readonly struct QuantizedVoxels
    {
        public readonly byte[] Indices;
        public readonly Color32[] Palette;
        public readonly VoxelSemanticSlotMetadata[] SemanticSlots;

        public QuantizedVoxels(byte[] indices, Color32[] palette,
            VoxelSemanticSlotMetadata[] semanticSlots = null)
        {
            Indices = indices;
            Palette = palette;
            SemanticSlots = semanticSlots;
        }
    }

    internal static class VoxelColorQuantizer
    {
        private sealed class Entry
        {
            public int Key;
            public long Count;
            public long R;
            public long G;
            public long B;
            public byte MeanR => (byte)(R / Count);
            public byte MeanG => (byte)(G / Count);
            public byte MeanB => (byte)(B / Count);
        }

        private sealed class Box
        {
            public readonly List<Entry> Entries;
            public long Weight;
            public int RMin, RMax, GMin, GMax, BMin, BMax;

            public Box(List<Entry> entries)
            {
                Entries = entries;
                Recalculate();
            }

            public int Range => Mathf.Max(RMax - RMin, Mathf.Max(GMax - GMin, BMax - BMin));
            public long Score => (long)Range * Weight;

            public void Recalculate()
            {
                RMin = GMin = BMin = 255;
                RMax = GMax = BMax = 0;
                Weight = 0;
                foreach (Entry entry in Entries)
                {
                    int r = entry.MeanR, g = entry.MeanG, b = entry.MeanB;
                    RMin = Math.Min(RMin, r); RMax = Math.Max(RMax, r);
                    GMin = Math.Min(GMin, g); GMax = Math.Max(GMax, g);
                    BMin = Math.Min(BMin, b); BMax = Math.Max(BMax, b);
                    Weight += entry.Count;
                }
            }
        }

        public static QuantizedVoxels Quantize(VoxelGrid grid, int maxColors = 255)
        {
            var histogram = new Dictionary<int, Entry>();
            for (int i = 0; i < grid.Occupied.Length; i++)
            {
                if (!grid.Occupied[i]) continue;
                Color32 color = grid.Colors[i];
                int key = ((color.r >> 3) << 10) | ((color.g >> 3) << 5) | (color.b >> 3);
                if (!histogram.TryGetValue(key, out Entry entry))
                {
                    entry = new Entry { Key = key };
                    histogram.Add(key, entry);
                }
                entry.Count++;
                entry.R += color.r;
                entry.G += color.g;
                entry.B += color.b;
            }

            if (histogram.Count == 0)
                throw new InvalidOperationException("There are no voxels to export.");

            var boxes = new List<Box> { new Box(histogram.Values.ToList()) };
            while (boxes.Count < maxColors)
            {
                Box candidate = boxes
                    .Where(box => box.Entries.Count > 1 && box.Range > 0)
                    .OrderByDescending(box => box.Score)
                    .FirstOrDefault();
                if (candidate == null) break;

                int rRange = candidate.RMax - candidate.RMin;
                int gRange = candidate.GMax - candidate.GMin;
                int bRange = candidate.BMax - candidate.BMin;
                if (rRange >= gRange && rRange >= bRange)
                    candidate.Entries.Sort((a, b) => a.MeanR.CompareTo(b.MeanR));
                else if (gRange >= bRange)
                    candidate.Entries.Sort((a, b) => a.MeanG.CompareTo(b.MeanG));
                else
                    candidate.Entries.Sort((a, b) => a.MeanB.CompareTo(b.MeanB));

                long half = candidate.Weight / 2;
                long accumulated = 0;
                int split = 1;
                for (; split < candidate.Entries.Count; split++)
                {
                    accumulated += candidate.Entries[split - 1].Count;
                    if (accumulated >= half) break;
                }
                split = Mathf.Clamp(split, 1, candidate.Entries.Count - 1);
                var left = candidate.Entries.GetRange(0, split);
                var right = candidate.Entries.GetRange(split, candidate.Entries.Count - split);
                boxes.Remove(candidate);
                boxes.Add(new Box(left));
                boxes.Add(new Box(right));
            }

            boxes = boxes.OrderByDescending(box => box.Weight).ToList();
            var palette = new Color32[boxes.Count];
            var keyToIndex = new Dictionary<int, byte>(histogram.Count);
            for (int paletteIndex = 0; paletteIndex < boxes.Count; paletteIndex++)
            {
                Box box = boxes[paletteIndex];
                long r = 0, g = 0, b = 0;
                foreach (Entry entry in box.Entries)
                {
                    r += entry.R; g += entry.G; b += entry.B;
                    keyToIndex[entry.Key] = (byte)(paletteIndex + 1);
                }
                palette[paletteIndex] = new Color32(
                    (byte)(r / box.Weight), (byte)(g / box.Weight), (byte)(b / box.Weight), 255);
            }

            var indices = new byte[grid.Occupied.Length];
            for (int i = 0; i < indices.Length; i++)
            {
                if (!grid.Occupied[i]) continue;
                Color32 color = grid.Colors[i];
                int key = ((color.r >> 3) << 10) | ((color.g >> 3) << 5) | (color.b >> 3);
                indices[i] = keyToIndex[key];
            }
            return new QuantizedVoxels(indices, palette);
        }
    }

    internal static class VoxWriter
    {
        public static void Write(string path, VoxelGrid grid, QuantizedVoxels quantized)
        {
            int voxelCount = grid.CountOccupied();
            int sizeChunkBytes = 12 + 12;
            int xyziContentBytes = 4 + voxelCount * 4;
            int xyziChunkBytes = 12 + xyziContentBytes;
            int rgbaChunkBytes = 12 + 1024;
            int childrenBytes = checked(sizeChunkBytes + xyziChunkBytes + rgbaChunkBytes);

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                WriteId(writer, "VOX ");
                writer.Write(150);
                WriteChunkHeader(writer, "MAIN", 0, childrenBytes);

                WriteChunkHeader(writer, "SIZE", 12, 0);
                writer.Write(grid.Size.x);
                writer.Write(grid.Size.z); // MagicaVoxel Y <- Unity Z
                writer.Write(grid.Size.y); // MagicaVoxel Z <- Unity Y (up)

                WriteChunkHeader(writer, "XYZI", xyziContentBytes, 0);
                writer.Write(voxelCount);
                for (int index = 0; index < grid.Occupied.Length; index++)
                {
                    if (!grid.Occupied[index]) continue;
                    grid.Coordinates(index, out int x, out int y, out int z);
                    writer.Write((byte)x);
                    writer.Write((byte)z);
                    writer.Write((byte)y);
                    writer.Write(quantized.Indices[index]);
                }

                WriteChunkHeader(writer, "RGBA", 1024, 0);
                for (int i = 0; i < 256; i++)
                {
                    Color32 color = i < quantized.Palette.Length
                        ? quantized.Palette[i]
                        : new Color32(0, 0, 0, 255);
                    writer.Write(color.r);
                    writer.Write(color.g);
                    writer.Write(color.b);
                    writer.Write(color.a);
                }
            }
        }

        private static void WriteChunkHeader(BinaryWriter writer, string id, int contentBytes, int childrenBytes)
        {
            WriteId(writer, id);
            writer.Write(contentBytes);
            writer.Write(childrenBytes);
        }

        private static void WriteId(BinaryWriter writer, string id)
        {
            if (id.Length != 4) throw new ArgumentException("A VOX chunk id must contain four characters.");
            for (int i = 0; i < 4; i++) writer.Write((byte)id[i]);
        }
    }
}
