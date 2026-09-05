using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    // Reads the static, axis-aligned authoring subset. Unsupported transforms fail closed.
    internal sealed class VoxelSceneGraph
    {
        private sealed class Node
        {
            public int[] Children = Array.Empty<int>();
            public int Model = -1;
            public int Layer = -1;
            public bool Hidden;
            public Vector3Int Translation;
        }

        private readonly Dictionary<int, Node> nodes = new();
        private readonly HashSet<int> hiddenLayers = new();
        private readonly HashSet<int> layers = new();

        internal bool ReadChunk(string kind, BinaryReader reader, long end)
        {
            if (kind != "nTRN" && kind != "nGRP" && kind != "nSHP" && kind != "LAYR") return false;
            int id = Integer(reader, end);
            if (id < 0) throw new InvalidDataException("Negative scene graph ID.");
            Dictionary<string, string> attributes = Dictionary(reader, end);
            var node = new Node { Hidden = IsHidden(attributes) };
            if (kind == "LAYR")
            {
                if (!layers.Add(id) || Integer(reader, end) != -1)
                    throw new InvalidDataException("Invalid or duplicate VOX layer.");
                if (node.Hidden) hiddenLayers.Add(id);
            }
            else
            {
                if (nodes.Count >= 100000 || !nodes.TryAdd(id, node))
                    throw new InvalidDataException("Duplicate scene node or scene graph limit exceeded.");
                if (kind == "nTRN")
                {
                    node.Children = new[] { Integer(reader, end) };
                    if (Integer(reader, end) != -1) throw new InvalidDataException("Invalid transform reserved ID.");
                    node.Layer = Integer(reader, end);
                    if (Integer(reader, end) != 1) throw new InvalidDataException("Animated VOX transforms are not supported.");
                    var frame = Dictionary(reader, end);
                    ValidateFrame(frame);
                    if (frame.TryGetValue("_r", out string rotation) && rotation != "4")
                        throw new InvalidDataException("Rotated or mirrored VOX chunks are not supported. Keep the exported chunk orientation.");
                    if (frame.TryGetValue("_t", out string translation))
                    {
                        string[] parts = translation.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length != 3) throw new InvalidDataException("Invalid VOX translation.");
                        node.Translation = new Vector3Int(Parse(parts[0]), Parse(parts[1]), Parse(parts[2]));
                    }
                }
                else if (kind == "nGRP")
                {
                    int count = Integer(reader, end);
                    if (count < 0 || count > 100000 || count > (end - reader.BaseStream.Position) / 4)
                        throw new InvalidDataException("Invalid scene child count.");
                    node.Children = new int[count];
                    for (int i = 0; i < count; i++) node.Children[i] = Integer(reader, end);
                }
                else
                {
                    if (Integer(reader, end) != 1) throw new InvalidDataException("Animated VOX shapes are not supported.");
                    node.Model = Integer(reader, end);
                    if (node.Model < 0) throw new InvalidDataException("Negative VOX model ID.");
                    ValidateFrame(Dictionary(reader, end));
                }
            }
            if (reader.BaseStream.Position != end) throw new InvalidDataException($"Invalid {kind} content length.");
            return true;
        }

        internal int[] ResolveModels(VoxelBridgeMetadata metadata, VoxelChunkMetadata[] chunks,
            Vector3Int[] sizes, int[] counts)
        {
            if (nodes.Count == 0)
            {
                if (chunks.Length != sizes.Length) throw new InvalidDataException("The VOX model count changed without a scene graph to locate the chunks.");
                var direct = chunks.Select(c => c.modelIndex).ToArray();
                if (direct.Distinct().Count() != direct.Length || direct.Any(i => i < 0 || i >= sizes.Length))
                    throw new InvalidDataException("Invalid chunk model mapping.");
                for (int i = 0; i < chunks.Length; i++)
                    if (chunks[i].gridSize != sizes[direct[i]])
                        throw new InvalidDataException("A source chunk was resized. Restore its saved box before rebuilding.");
                return direct;
            }
            var children = new HashSet<int>();
            foreach (Node node in nodes.Values)
                foreach (int child in node.Children)
                    if (!nodes.ContainsKey(child) || !children.Add(child))
                        throw new InvalidDataException("Scene graph has missing or multiply-parented nodes.");
            int[] roots = nodes.Keys.Where(id => !children.Contains(id)).ToArray();
            if (roots.Length != 1) throw new InvalidDataException("The VOX scene graph must have one root.");
            var visited = new HashSet<int>();
            var modelSeen = new HashSet<int>();
            var mapping = Enumerable.Repeat(-1, chunks.Length).ToArray();
            var stack = new Stack<(int Id, Vector3Int Translation, bool Hidden)>();
            stack.Push((roots[0], Vector3Int.zero, false));
            bool single = !VoxelImporterIntegration.UsesSceneGraph(metadata) && chunks.Length == 1;
            var placements = new Dictionary<(Vector3Int Size, Vector3Int Translation), int>();
            for (int i = 0; i < chunks.Length; i++)
            {
                VoxelChunkMetadata chunk = chunks[i];
                Vector3Int expected = single ? new Vector3Int(0, 0, chunk.gridSize.y / 2)
                    : Translation(chunk.gridOffset, chunk.gridSize);
                if (!placements.TryAdd((chunk.gridSize, expected), i))
                    throw new InvalidDataException("Ambiguous chunk placement in sidecar.");
            }
            while (stack.Count > 0)
            {
                var entry = stack.Pop();
                if (!visited.Add(entry.Id)) throw new InvalidDataException("Cyclic VOX scene graph.");
                Node node = nodes[entry.Id];
                Vector3Int t = new(checked(entry.Translation.x + node.Translation.x),
                    checked(entry.Translation.y + node.Translation.y), checked(entry.Translation.z + node.Translation.z));
                bool hidden = entry.Hidden || node.Hidden || hiddenLayers.Contains(node.Layer);
                foreach (int child in node.Children) stack.Push((child, t, hidden));
                if (node.Model < 0) continue;
                int model = node.Model;
                if (model >= sizes.Length) throw new InvalidDataException("Scene shape references a missing model.");
                if (!modelSeen.Add(model)) throw new InvalidDataException("Instanced VOX models are not supported in a chunked source.");
                Vector3Int size = sizes[model];
                if (!placements.TryGetValue((size, t), out int match))
                {
                    if (counts[model] == 0) continue;
                    throw new InvalidDataException($"Model {model} does not match a saved chunk size and position. Restore the chunk boxes; only voxel edits inside them are supported.");
                }
                if (mapping[match] >= 0) throw new InvalidDataException("Ambiguous VOX chunk placement.");
                if (hidden && counts[model] > 0) throw new InvalidDataException("A populated source chunk is hidden. Show it before rebuilding.");
                mapping[match] = model;
            }
            if (visited.Count != nodes.Count) throw new InvalidDataException("Disconnected or cyclic VOX scene graph.");
            if (mapping.Any(i => i < 0)) throw new InvalidDataException("An exported chunk is missing or its box was resized/moved. Restore its saved size and position before rebuilding.");
            for (int i = 0; i < sizes.Length; i++)
                if (!modelSeen.Contains(i) && counts[i] > 0) throw new InvalidDataException("Unreferenced populated model in VOX document.");
            return mapping;
        }

        internal static Vector3Int Translation(Vector3Int offset, Vector3Int size) => new(
            checked(offset.x + size.x - 1 - (size.x + 1) / 2),
            checked(offset.z + size.z - 1 - (size.z + 1) / 2),
            checked(offset.y + size.y / 2));

        private static int Parse(string value) => int.TryParse(value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int result) ? result : throw new InvalidDataException("Invalid scene integer.");
        private static bool IsHidden(Dictionary<string, string> attributes) =>
            attributes.TryGetValue("_hidden", out string hidden) && hidden == "1";
        private static void ValidateFrame(Dictionary<string, string> frame)
        {
            if (frame.TryGetValue("_f", out string index) && index != "0")
                throw new InvalidDataException("Only static VOX frame 0 is supported.");
        }
        private static int Integer(BinaryReader reader, long end)
        {
            if (end - reader.BaseStream.Position < 4) throw new InvalidDataException("Truncated scene graph integer.");
            return reader.ReadInt32();
        }
        private static Dictionary<string, string> Dictionary(BinaryReader reader, long end)
        {
            int count = Integer(reader, end);
            if (count < 0 || count > 4096) throw new InvalidDataException("Invalid scene dictionary count.");
            var result = new Dictionary<string, string>();
            for (int i = 0; i < count; i++)
                if (!result.TryAdd(Text(reader, end), Text(reader, end))) throw new InvalidDataException("Duplicate scene attribute.");
            return result;
        }
        private static string Text(BinaryReader reader, long end)
        {
            int count = Integer(reader, end);
            if (count < 0 || count > 65536 || count > end - reader.BaseStream.Position)
                throw new InvalidDataException("Invalid scene string length.");
            return Encoding.UTF8.GetString(reader.ReadBytes(count));
        }
    }
}
