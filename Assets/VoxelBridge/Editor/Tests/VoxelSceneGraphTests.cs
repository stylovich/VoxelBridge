using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelSceneGraphTests
    {
        private static VoxelBridgeMetadata Metadata() => new()
        {
            formatVersion = 3, voxelSize = 0.032f, unityGridSize = new Vector3Int(5, 2, 2),
            gridOrigin = new Vector3(-1, 2, 3),
            chunks = new[]
            {
                new VoxelChunkMetadata { modelIndex = 0, gridSize = new Vector3Int(3, 2, 2) },
                new VoxelChunkMetadata { modelIndex = 1, gridOffset = new Vector3Int(3, 0, 0), gridSize = new Vector3Int(2, 2, 2) }
            }
        };

        private static readonly Vector3Int[] Sizes =
        { new(40, 40, 40), new(40, 40, 40), new(3, 2, 2), new(2, 2, 2) };

        [Test]
        public void SavedDocument_EmptyModelsAndReorderedIdsPreserveChunkCoordinatesAndColors()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vox");
            try
            {
                using (var children = new MemoryStream())
                using (var output = new BinaryWriter(children, Encoding.UTF8, true))
                {
                    for (int i = 0; i < Sizes.Length; i++)
                    {
                        Vector3Int size = Sizes[i];
                        WriteChunk(output, "SIZE", w => { w.Write(size.x); w.Write(size.z); w.Write(size.y); });
                        int model = i;
                        WriteChunk(output, "XYZI", w =>
                        {
                            w.Write(model < 2 ? 0 : 1);
                            if (model < 2) return;
                            byte p = (byte)(model - 2);
                            w.Write(p); w.Write(p); w.Write(p); w.Write((byte)(p + 1));
                        });
                    }
                    WriteGraph((kind, bytes) => WriteChunk(output, kind, w => w.Write(bytes)));
                    WriteChunk(output, "RGBA", w =>
                    {
                        for (int i = 0; i < 256; i++)
                        { w.Write((byte)(i == 0 ? 255 : 0)); w.Write((byte)(i == 1 ? 255 : 0)); w.Write((byte)0); w.Write((byte)255); }
                    });
                    using var file = new BinaryWriter(File.Create(path));
                    file.Write(Encoding.ASCII.GetBytes("VOX ")); file.Write(200);
                    file.Write(Encoding.ASCII.GetBytes("MAIN")); file.Write(0); file.Write((int)children.Length);
                    file.Write(children.ToArray());
                }
                VoxelBridgeMetadata metadata = Metadata();
                VoxelGrid grid = VoxelVolumeReader.Read(path, metadata);
                Assert.That(grid.CountOccupied(), Is.EqualTo(2));
                Assert.That(grid.Colors[grid.Index(0, 0, 0)], Is.EqualTo(new Color32(255, 0, 0, 255)));
                Assert.That(grid.Colors[grid.Index(4, 1, 1)], Is.EqualTo(new Color32(0, 255, 0, 255)));
                Assert.That(grid.Origin, Is.EqualTo(metadata.gridOrigin));
                Assert.That(metadata.chunks[0].modelIndex, Is.Zero, "Reading must not rewrite the sidecar.");
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void SceneGraph_UsesPlacementInsteadOfShapeOrder()
        {
            VoxelSceneGraph graph = Graph();
            var metadata = Metadata();
            CollectionAssert.AreEqual(new[] { 2, 3 }, graph.ResolveModels(metadata, metadata.chunks, Sizes, new[] { 0, 0, 1, 1 }));
        }

        [Test]
        public void UnexpectedPopulatedModel_IsNotSilentlyDiscarded()
        {
            var metadata = Metadata();
            Assert.Throws<InvalidDataException>(() => Graph().ResolveModels(metadata, metadata.chunks, Sizes, new[] { 1, 0, 1, 1 }));
        }

        [Test]
        public void MovedChunk_IsRejected()
        {
            var metadata = Metadata();
            Assert.Throws<InvalidDataException>(() => Graph(moved: true).ResolveModels(metadata, metadata.chunks, Sizes, new[] { 0, 0, 1, 1 }));
        }

        [Test]
        public void ResizedChunk_IsRejected()
        {
            var metadata = Metadata();
            var sizes = (Vector3Int[])Sizes.Clone(); sizes[2] = Vector3Int.one;
            Assert.Throws<InvalidDataException>(() => Graph().ResolveModels(metadata, metadata.chunks, sizes, new[] { 0, 0, 1, 1 }));
        }

        [TestCase("_r", "17")]
        [TestCase("_f", "1")]
        public void UnsupportedRotationOrAnimation_IsRejected(string key, string value)
        {
            var graph = new VoxelSceneGraph();
            Assert.Throws<InvalidDataException>(() => Read(graph, "nTRN", Data(w =>
            {
                w.Write(0); w.Write(0); w.Write(1); w.Write(-1); w.Write(-1); w.Write(1);
                w.Write(1); Text(w, key); Text(w, value);
            })));
        }

        [Test]
        public void TruncatedDictionary_IsRejected()
        {
            var graph = new VoxelSceneGraph();
            Assert.Throws<InvalidDataException>(() => Read(graph, "nGRP", Data(w => { w.Write(0); w.Write(1); w.Write(1000); })));
        }

        private static VoxelSceneGraph Graph(bool moved = false)
        {
            var graph = new VoxelSceneGraph();
            WriteGraph((kind, data) => Read(graph, kind, data), moved);
            return graph;
        }

        private static void Read(VoxelSceneGraph graph, string kind, byte[] data)
        {
            using var stream = new MemoryStream(data);
            using var reader = new BinaryReader(stream);
            Assert.That(graph.ReadChunk(kind, reader, data.Length), Is.True);
        }

        private static void WriteGraph(Action<string, byte[]> chunk, bool moved = false)
        {
            chunk("nGRP", Data(w => { w.Write(0); w.Write(0); w.Write(4); w.Write(7); w.Write(5); w.Write(3); w.Write(1); }));
            for (int model = 0; model < 4; model++)
            {
                int id = 1 + model * 2;
                // Same placement as a MagicaVoxel save: two empty workspace models, then the original chunks.
                string translation = model < 2 ? "0 0 20" : model == 2 ? (moved ? "1 0 1" : "0 0 1") : "3 0 1";
                chunk("nTRN", Data(w =>
                {
                    w.Write(id); w.Write(0); w.Write(id + 1); w.Write(-1); w.Write(-1); w.Write(1);
                    w.Write(1); Text(w, "_t"); Text(w, translation);
                }));
                chunk("nSHP", Data(w => { w.Write(id + 1); w.Write(0); w.Write(1); w.Write(model); w.Write(0); }));
            }
        }

        private static byte[] Data(Action<BinaryWriter> write)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) write(writer);
            return stream.ToArray();
        }
        private static void WriteChunk(BinaryWriter writer, string kind, Action<BinaryWriter> write)
        {
            byte[] bytes = Data(write);
            writer.Write(Encoding.ASCII.GetBytes(kind)); writer.Write(bytes.Length); writer.Write(0); writer.Write(bytes);
        }
        private static void Text(BinaryWriter writer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes);
        }
    }
}
