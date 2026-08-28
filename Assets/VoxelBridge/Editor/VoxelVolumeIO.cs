using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    internal readonly struct VoxWriteResult
    {
        public readonly VoxelChunkMetadata[] Chunks;
        public readonly bool UsesSceneGraph;

        public VoxWriteResult(VoxelChunkMetadata[] chunks, bool usesSceneGraph)
        {
            Chunks = chunks;
            UsesSceneGraph = usesSceneGraph;
        }
    }

    internal static class VoxelChunkedVoxWriter
    {
        private sealed class ChunkBuild
        {
            public Vector3Int Coordinate;
            public Vector3Int Offset;
            public Vector3Int Size;
            public readonly List<int> GlobalIndices = new();
        }

        public static VoxWriteResult Write(string path, VoxelGrid grid, QuantizedVoxels quantized, int chunkCellSize)
        {
            chunkCellSize = Mathf.Clamp(chunkCellSize, 16, 256);
            var byCoordinate = new Dictionary<Vector3Int, ChunkBuild>();
            for (int index = 0; index < grid.Occupied.Length; index++)
            {
                if (!grid.Occupied[index]) continue;
                grid.Coordinates(index, out int x, out int y, out int z);
                Vector3Int coordinate = new(x / chunkCellSize, y / chunkCellSize, z / chunkCellSize);
                if (!byCoordinate.TryGetValue(coordinate, out ChunkBuild chunk))
                {
                    Vector3Int offset = coordinate * chunkCellSize;
                    chunk = new ChunkBuild
                    {
                        Coordinate = coordinate,
                        Offset = offset,
                        Size = new Vector3Int(
                            Mathf.Min(chunkCellSize, grid.Size.x - offset.x),
                            Mathf.Min(chunkCellSize, grid.Size.y - offset.y),
                            Mathf.Min(chunkCellSize, grid.Size.z - offset.z))
                    };
                    byCoordinate.Add(coordinate, chunk);
                }
                chunk.GlobalIndices.Add(index);
            }
            if (byCoordinate.Count == 0) throw new InvalidOperationException("No hay vóxeles para exportar.");

            var chunks = new List<ChunkBuild>(byCoordinate.Values);
            chunks.Sort((a, b) =>
            {
                int z = a.Coordinate.z.CompareTo(b.Coordinate.z);
                if (z != 0) return z;
                int y = a.Coordinate.y.CompareTo(b.Coordinate.y);
                return y != 0 ? y : a.Coordinate.x.CompareTo(b.Coordinate.x);
            });

            bool sceneGraph = chunks.Count > 1 || chunks[0].Offset != Vector3Int.zero ||
                              grid.Size.x > 256 || grid.Size.y > 256 || grid.Size.z > 256;
            var topLevelChunks = new List<byte[]>();
            if (sceneGraph)
                topLevelChunks.Add(BuildChunk("PACK", writer => writer.Write(chunks.Count)));

            var metadata = new VoxelChunkMetadata[chunks.Count];
            for (int modelIndex = 0; modelIndex < chunks.Count; modelIndex++)
            {
                ChunkBuild chunk = chunks[modelIndex];
                topLevelChunks.Add(BuildChunk("SIZE", writer =>
                {
                    writer.Write(chunk.Size.x);
                    writer.Write(chunk.Size.z);
                    writer.Write(chunk.Size.y);
                }));
                topLevelChunks.Add(BuildChunk("XYZI", writer =>
                {
                    writer.Write(chunk.GlobalIndices.Count);
                    foreach (int globalIndex in chunk.GlobalIndices)
                    {
                        grid.Coordinates(globalIndex, out int x, out int y, out int z);
                        writer.Write((byte)(x - chunk.Offset.x));
                        writer.Write((byte)(z - chunk.Offset.z));
                        writer.Write((byte)(y - chunk.Offset.y));
                        writer.Write(quantized.Indices[globalIndex]);
                    }
                }));
                metadata[modelIndex] = new VoxelChunkMetadata
                {
                    modelIndex = modelIndex,
                    gridOffset = chunk.Offset,
                    gridSize = chunk.Size,
                    voxelCount = chunk.GlobalIndices.Count
                };
            }

            if (sceneGraph)
                AddSceneGraph(topLevelChunks, chunks);

            topLevelChunks.Add(BuildChunk("RGBA", writer =>
            {
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
            }));

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var output = new BinaryWriter(stream);
            WriteId(output, "VOX ");
            output.Write(sceneGraph ? 200 : 150);
            int childrenBytes = 0;
            foreach (byte[] chunk in topLevelChunks) childrenBytes = checked(childrenBytes + chunk.Length);
            WriteChunkHeader(output, "MAIN", 0, childrenBytes);
            foreach (byte[] chunk in topLevelChunks) output.Write(chunk);
            return new VoxWriteResult(metadata, sceneGraph);
        }

        private static void AddSceneGraph(List<byte[]> output, List<ChunkBuild> chunks)
        {
            output.Add(BuildChunk("nTRN", writer =>
            {
                writer.Write(0);
                WriteDictionary(writer);
                writer.Write(1);
                writer.Write(-1);
                writer.Write(-1);
                writer.Write(1);
                WriteDictionary(writer);
            }));
            output.Add(BuildChunk("nGRP", writer =>
            {
                writer.Write(1);
                WriteDictionary(writer);
                writer.Write(chunks.Count);
                for (int i = 0; i < chunks.Count; i++) writer.Write(2 + i * 2);
            }));

            for (int i = 0; i < chunks.Count; i++)
            {
                int transformId = 2 + i * 2;
                int shapeId = transformId + 1;
                int modelId = i;
                ChunkBuild chunk = chunks[i];
                Vector3Int voxOffset = new(chunk.Offset.x, chunk.Offset.z, chunk.Offset.y);
                Vector3Int voxSize = new(chunk.Size.x, chunk.Size.z, chunk.Size.y);
                // Voxel Importer mirrors the VOX X/Y axes inside each model before it
                // evaluates the scene graph. Include the per-model extent here so all
                // chunks end up in one shared global grid instead of being mirrored
                // around their own local origins.
                Vector3Int translation = new(
                    voxOffset.x + voxSize.x - 1 - Mathf.CeilToInt(voxSize.x * 0.5f),
                    voxOffset.y + voxSize.y - 1 - Mathf.CeilToInt(voxSize.y * 0.5f),
                    voxOffset.z + Mathf.FloorToInt(voxSize.z * 0.5f));
                string translationText = $"{translation.x} {translation.y} {translation.z}";
                string name = $"Chunk_{chunk.Coordinate.x}_{chunk.Coordinate.y}_{chunk.Coordinate.z}";

                output.Add(BuildChunk("nTRN", writer =>
                {
                    writer.Write(transformId);
                    WriteDictionary(writer, ("_name", name));
                    writer.Write(shapeId);
                    writer.Write(-1);
                    writer.Write(-1);
                    writer.Write(1);
                    WriteDictionary(writer, ("_t", translationText));
                }));
                output.Add(BuildChunk("nSHP", writer =>
                {
                    writer.Write(shapeId);
                    WriteDictionary(writer);
                    writer.Write(1);
                    writer.Write(modelId);
                    WriteDictionary(writer);
                }));
            }
        }

        private static byte[] BuildChunk(string id, Action<BinaryWriter> writeContent)
        {
            using var contentStream = new MemoryStream();
            using (var contentWriter = new BinaryWriter(contentStream, Encoding.UTF8, true))
                writeContent(contentWriter);
            byte[] content = contentStream.ToArray();
            using var resultStream = new MemoryStream();
            using (var resultWriter = new BinaryWriter(resultStream, Encoding.UTF8, true))
            {
                WriteChunkHeader(resultWriter, id, content.Length, 0);
                resultWriter.Write(content);
            }
            return resultStream.ToArray();
        }

        private static void WriteDictionary(BinaryWriter writer, params (string Key, string Value)[] entries)
        {
            writer.Write(entries.Length);
            foreach ((string key, string value) in entries)
            {
                WriteString(writer, key);
                WriteString(writer, value);
            }
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
            writer.Write(bytes.Length);
            writer.Write(bytes);
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

    internal static class VoxelVolumeReader
    {
        private sealed class Model
        {
            public Vector3Int Size;
            public readonly List<(byte X, byte Y, byte Z, byte Palette)> Voxels = new();
        }

        public static VoxelGrid Read(string path, VoxelBridgeMetadata metadata)
        {
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));
            if (metadata.unityGridSize.x <= 0 || metadata.unityGridSize.y <= 0 || metadata.unityGridSize.z <= 0)
                throw new InvalidDataException("Los metadatos no contienen una rejilla válida.");

            var models = new List<Model>();
            var palette = new Color32[256];
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (ReadId(reader) != "VOX ") throw new InvalidDataException("El archivo no es VOX.");
            reader.ReadInt32();
            if (ReadId(reader) != "MAIN") throw new InvalidDataException("Falta el chunk MAIN.");
            int mainContent = reader.ReadInt32();
            int mainChildren = reader.ReadInt32();
            stream.Position += mainContent;
            long mainEnd = stream.Position + mainChildren;
            Vector3Int pendingSize = Vector3Int.zero;
            while (stream.Position < mainEnd)
            {
                string id = ReadId(reader);
                int contentBytes = reader.ReadInt32();
                int childrenBytes = reader.ReadInt32();
                long contentStart = stream.Position;
                if (id == "SIZE")
                {
                    int x = reader.ReadInt32();
                    int voxY = reader.ReadInt32();
                    int voxZ = reader.ReadInt32();
                    pendingSize = new Vector3Int(x, voxZ, voxY);
                }
                else if (id == "XYZI")
                {
                    var model = new Model { Size = pendingSize };
                    int count = reader.ReadInt32();
                    for (int i = 0; i < count; i++)
                        model.Voxels.Add((reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte()));
                    models.Add(model);
                }
                else if (id == "RGBA")
                {
                    for (int i = 0; i < 256; i++)
                        palette[i] = new Color32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                }
                stream.Position = contentStart + contentBytes + childrenBytes;
            }
            if (models.Count == 0) throw new InvalidDataException("El archivo VOX no contiene modelos.");

            var grid = new VoxelGrid(metadata.unityGridSize, metadata.gridOrigin, metadata.voxelSize);
            VoxelChunkMetadata[] chunks = metadata.chunks;
            if (chunks == null || chunks.Length == 0)
            {
                chunks = new[]
                {
                    new VoxelChunkMetadata
                    {
                        modelIndex = 0,
                        gridOffset = Vector3Int.zero,
                        gridSize = metadata.unityGridSize
                    }
                };
            }
            foreach (VoxelChunkMetadata chunk in chunks)
            {
                if (chunk.modelIndex < 0 || chunk.modelIndex >= models.Count)
                    throw new InvalidDataException("Los metadatos de chunks no coinciden con el VOX.");
                Model model = models[chunk.modelIndex];
                foreach ((byte x, byte voxY, byte voxZ, byte paletteIndex) in model.Voxels)
                {
                    int gx = chunk.gridOffset.x + x;
                    int gy = chunk.gridOffset.y + voxZ;
                    int gz = chunk.gridOffset.z + voxY;
                    if (gx < 0 || gy < 0 || gz < 0 ||
                        gx >= grid.Size.x || gy >= grid.Size.y || gz >= grid.Size.z) continue;
                    int index = grid.Index(gx, gy, gz);
                    grid.Occupied[index] = true;
                    grid.Colors[index] = paletteIndex > 0 ? palette[paletteIndex - 1] : Color.white;
                }
            }
            return grid;
        }

        private static string ReadId(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(4));
    }

    internal static class VoxelGridDownsampler
    {
        public static VoxelGrid Downsample(VoxelGrid source, float targetVoxelSize, int padding, int chunkCellSize)
        {
            bool found = false;
            Vector3 min = default;
            Vector3 max = default;
            for (int i = 0; i < source.Occupied.Length; i++)
            {
                if (!source.Occupied[i]) continue;
                source.Coordinates(i, out int x, out int y, out int z);
                Vector3 cellMin = source.Origin + new Vector3(x, y, z) * source.VoxelSize;
                Vector3 cellMax = cellMin + Vector3.one * source.VoxelSize;
                if (!found) { min = cellMin; max = cellMax; found = true; }
                else { min = Vector3.Min(min, cellMin); max = Vector3.Max(max, cellMax); }
            }
            if (!found) throw new InvalidOperationException("El LOD anterior no contiene vóxeles.");

            Bounds bounds = new Bounds((min + max) * 0.5f, max - min);
            VoxelGridPlan plan = VoxelGridPlanner.Create(bounds, targetVoxelSize, padding, chunkCellSize);
            var result = new VoxelGrid(plan.Size, plan.Origin, targetVoxelSize);
            var counts = new int[result.Occupied.Length];
            var red = new long[result.Occupied.Length];
            var green = new long[result.Occupied.Length];
            var blue = new long[result.Occupied.Length];
            for (int i = 0; i < source.Occupied.Length; i++)
            {
                if (!source.Occupied[i]) continue;
                source.Coordinates(i, out int x, out int y, out int z);
                Vector3 center = source.Origin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * source.VoxelSize;
                Vector3 relative = (center - result.Origin) / targetVoxelSize;
                int tx = Mathf.Clamp(Mathf.FloorToInt(relative.x), 0, result.Size.x - 1);
                int ty = Mathf.Clamp(Mathf.FloorToInt(relative.y), 0, result.Size.y - 1);
                int tz = Mathf.Clamp(Mathf.FloorToInt(relative.z), 0, result.Size.z - 1);
                int target = result.Index(tx, ty, tz);
                Color32 color = source.Colors[i];
                counts[target]++;
                red[target] += color.r;
                green[target] += color.g;
                blue[target] += color.b;
            }
            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] == 0) continue;
                result.Occupied[i] = true;
                result.Colors[i] = new Color32(
                    (byte)(red[i] / counts[i]),
                    (byte)(green[i] / counts[i]),
                    (byte)(blue[i] / counts[i]), 255);
            }
            return result;
        }
    }
}
