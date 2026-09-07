using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            if (byCoordinate.Count == 0) throw new InvalidOperationException("There are no voxels to export.");

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
            public List<int> SlotOffsets;
        }

        public static VoxelGrid Read(string path, VoxelBridgeMetadata metadata,
            Action<int, int> visitRecord = null)
        {
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));
            if (metadata.formatVersion != 3 && metadata.formatVersion != 4)
                throw new InvalidDataException($"Unsupported Voxel Bridge metadata version: {metadata.formatVersion}.");
            if (metadata.unityGridSize.x <= 0 || metadata.unityGridSize.y <= 0 || metadata.unityGridSize.z <= 0)
                throw new InvalidDataException("The metadata does not contain a valid grid.");

            bool isSemantic = metadata.formatVersion >= 4;
            ushort[] slotToSemantic = null;
            if (isSemantic)
            {
                if (metadata.semantic == null)
                    throw new InvalidDataException(
                        "The v4 sidecar contains no semantic metadata.");
                VoxelSemanticVoxDocument semanticDocument = VoxelSemanticVoxDocument.Read(path);
                if (!VoxelSemanticTransport.TryLoadPalettes(metadata.semantic,
                        out VoxelColorPalette colorPalette,
                        out VoxelSurfacePalette surfacePalette, out string paletteError))
                    throw new InvalidDataException(paletteError);
                if (!VoxelSemanticTransport.TryResolveSlotSemantics(
                        semanticDocument, metadata.semantic, colorPalette, surfacePalette,
                        out slotToSemantic, out string warning, out string semanticError))
                    throw new InvalidDataException(semanticError);
                if (!string.IsNullOrEmpty(warning))
                    Debug.LogWarning($"Voxel Bridge: {warning} ({path})");
            }

            var models = new List<Model>();
            var sceneGraph = new VoxelSceneGraph();
            var palette = new Color32[256];
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            EnsureAvailable(stream, stream.Length, 20, "VOX header");
            if (ReadId(reader) != "VOX ") throw new InvalidDataException("The file is not a VOX document.");
            reader.ReadInt32();
            if (ReadId(reader) != "MAIN") throw new InvalidDataException("The MAIN chunk is missing.");
            int mainContent = reader.ReadInt32();
            int mainChildren = reader.ReadInt32();
            if (mainContent < 0 || mainChildren < 0)
                throw new InvalidDataException("The MAIN chunk contains a negative size.");
            EnsureAvailable(stream, stream.Length, (long)mainContent + mainChildren, "MAIN");
            stream.Position += mainContent;
            long mainEnd = stream.Position + mainChildren;
            if (mainEnd != stream.Length)
                throw new InvalidDataException("The VOX document contains data outside MAIN.");
            Vector3Int pendingSize = Vector3Int.zero;
            bool hasPalette = false;
            while (stream.Position < mainEnd)
            {
                EnsureAvailable(stream, mainEnd, 12, "chunk header");
                string id = ReadId(reader);
                int contentBytes = reader.ReadInt32();
                int childrenBytes = reader.ReadInt32();
                if (contentBytes < 0 || childrenBytes < 0)
                    throw new InvalidDataException($"Chunk {id} contains a negative size.");
                EnsureAvailable(stream, mainEnd, (long)contentBytes + childrenBytes, id);
                long contentStart = stream.Position;
                if (id == "SIZE")
                {
                    if (contentBytes != 12 || pendingSize != Vector3Int.zero)
                        throw new InvalidDataException("The SIZE chunk is invalid or has no matching XYZI chunk.");
                    int x = reader.ReadInt32();
                    int voxY = reader.ReadInt32();
                    int voxZ = reader.ReadInt32();
                    if (x < 1 || x > 256 || voxY < 1 || voxY > 256 || voxZ < 1 || voxZ > 256)
                        throw new InvalidDataException("SIZE dimensions must be between 1 and 256.");
                    pendingSize = new Vector3Int(x, voxZ, voxY);
                }
                else if (id == "XYZI")
                {
                    if (contentBytes < 4 || pendingSize == Vector3Int.zero)
                        throw new InvalidDataException("The XYZI chunk is truncated or has no preceding SIZE chunk.");
                    var model = new Model { Size = pendingSize, SlotOffsets = visitRecord == null ? null : new List<int>() };
                    int count = reader.ReadInt32();
                    if (count < 0 || 4L + count * 4L != contentBytes)
                        throw new InvalidDataException("The XYZI voxel count does not match its chunk size.");
                    for (int i = 0; i < count; i++)
                    {
                        model.SlotOffsets?.Add(checked((int)stream.Position + 3));
                        model.Voxels.Add((reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte()));
                    }
                    models.Add(model);
                    pendingSize = Vector3Int.zero;
                }
                else if (id == "RGBA")
                {
                    if (hasPalette || contentBytes != 1024)
                        throw new InvalidDataException("The RGBA chunk is duplicated or has an invalid size.");
                    for (int i = 0; i < 256; i++)
                        palette[i] = new Color32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                    hasPalette = true;
                }
                else if (sceneGraph.ReadChunk(id, reader, contentStart + contentBytes) && childrenBytes != 0)
                    throw new InvalidDataException("Nested scene node chunks are not supported.");
                stream.Position = contentStart + contentBytes + childrenBytes;
            }
            if (pendingSize != Vector3Int.zero)
                throw new InvalidDataException("A SIZE chunk has no matching XYZI chunk.");
            if (models.Count == 0) throw new InvalidDataException("The VOX document contains no models.");
            if (!hasPalette)
                throw new InvalidDataException("The VOX document has no explicit RGBA palette. Re-export it with its palette before generating LODs.");

            var grid = new VoxelGrid(
                metadata.unityGridSize, metadata.gridOrigin, metadata.voxelSize, isSemantic);
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
            if (chunks.Any(c => c == null)) throw new InvalidDataException("Null chunk metadata.");
            int[] modelMapping = sceneGraph.ResolveModels(metadata, chunks,
                models.Select(m => m.Size).ToArray(), models.Select(m => m.Voxels.Count).ToArray());
            for (int chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
            {
                VoxelChunkMetadata chunk = chunks[chunkIndex];
                Model model = models[modelMapping[chunkIndex]];
                for (int record = 0; record < model.Voxels.Count; record++)
                {
                    (byte x, byte voxY, byte voxZ, byte paletteIndex) = model.Voxels[record];
                    if (x >= model.Size.x || voxZ >= model.Size.y || voxY >= model.Size.z)
                        throw new InvalidDataException($"Model {chunk.modelIndex} contains a voxel outside its SIZE bounds.");
                    if (paletteIndex == 0)
                        throw new InvalidDataException("XYZI uses reserved palette slot 0.");
                    long gx = (long)chunk.gridOffset.x + x;
                    long gy = (long)chunk.gridOffset.y + voxZ;
                    long gz = (long)chunk.gridOffset.z + voxY;
                    if (gx < 0 || gy < 0 || gz < 0 ||
                        gx >= grid.Size.x || gy >= grid.Size.y || gz >= grid.Size.z)
                        throw new InvalidDataException(
                            $"Model {chunk.modelIndex} contains a voxel outside the saved grid at ({gx}, {gy}, {gz}). " +
                            "Re-export the model and its sidecar with matching bounds before generating LODs.");
                    int index = grid.Index((int)gx, (int)gy, (int)gz);
                    if (visitRecord != null && grid.Occupied[index])
                        throw new InvalidDataException("Multiple VOX records occupy the same cell; per-cell editing is ambiguous.");
                    grid.Occupied[index] = true;
                    if (isSemantic)
                    {
                        grid.SemanticIds[index] = slotToSemantic[paletteIndex];
                    }
                    else
                    {
                        grid.Colors[index] = palette[paletteIndex - 1];
                    }
                    visitRecord?.Invoke(index, model.SlotOffsets[record]);
                }
            }
            return grid;
        }

        private static string ReadId(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(4));

        private static void EnsureAvailable(Stream stream, long end, long bytes, string chunk)
        {
            if (bytes < 0 || bytes > end - stream.Position)
                throw new InvalidDataException($"Chunk {chunk} is truncated or exceeds its parent bounds.");
        }
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
            if (!found) throw new InvalidOperationException("The previous LOD contains no voxels.");

            Bounds bounds = new Bounds((min + max) * 0.5f, max - min);
            VoxelGridPlan plan = VoxelGridPlanner.Create(bounds, targetVoxelSize, padding, chunkCellSize);
            var result = new VoxelGrid(plan.Size, plan.Origin, targetVoxelSize, source.IsSemantic);
            if (source.IsSemantic)
            {
                DownsampleSemantics(source, result, targetVoxelSize);
                return result;
            }
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

        private static void DownsampleSemantics(
            VoxelGrid source, VoxelGrid result, float targetVoxelSize)
        {
            var votes = new ulong[source.CountOccupied()];
            int voteIndex = 0;
            for (int i = 0; i < source.Occupied.Length; i++)
            {
                if (!source.Occupied[i]) continue;
                int target = MapTargetIndex(source, result, targetVoxelSize, i);
                votes[voteIndex++] = ((ulong)(uint)target << 16) | source.SemanticIds[i];
            }
            Array.Sort(votes);

            int cursor = 0;
            while (cursor < votes.Length)
            {
                int target = (int)(votes[cursor] >> 16);
                ushort bestSemantic = 0;
                int bestCount = -1;
                while (cursor < votes.Length && (int)(votes[cursor] >> 16) == target)
                {
                    ushort semantic = (ushort)votes[cursor];
                    int count = 0;
                    do
                    {
                        count++;
                        cursor++;
                    }
                    while (cursor < votes.Length &&
                           (int)(votes[cursor] >> 16) == target &&
                           (ushort)votes[cursor] == semantic);

                    if (count > bestCount || count == bestCount && semantic < bestSemantic)
                    {
                        bestSemantic = semantic;
                        bestCount = count;
                    }
                }
                result.Occupied[target] = true;
                result.SemanticIds[target] = bestSemantic;
            }
        }

        private static int MapTargetIndex(
            VoxelGrid source, VoxelGrid result, float targetVoxelSize, int sourceIndex)
        {
            source.Coordinates(sourceIndex, out int x, out int y, out int z);
            Vector3 center = source.Origin +
                             new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * source.VoxelSize;
            Vector3 relative = (center - result.Origin) / targetVoxelSize;
            int tx = Mathf.Clamp(Mathf.FloorToInt(relative.x), 0, result.Size.x - 1);
            int ty = Mathf.Clamp(Mathf.FloorToInt(relative.y), 0, result.Size.y - 1);
            int tz = Mathf.Clamp(Mathf.FloorToInt(relative.z), 0, result.Size.z - 1);
            return result.Index(tx, ty, tz);
        }
    }
}
