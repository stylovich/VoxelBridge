using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelSemanticTransportTests
    {
        [TestCase(0, 0)]
        [TestCase(255, 255)]
        [TestCase(12, 197)]
        public void SemanticEncoding_RoundTripsBothStableIds(int colorId, int surfaceId)
        {
            ushort packed = VoxelSemanticEncoding.Pack(colorId, surfaceId);

            Assert.That(VoxelSemanticEncoding.ColorId(packed), Is.EqualTo(colorId));
            Assert.That(VoxelSemanticEncoding.SurfaceId(packed), Is.EqualTo(surfaceId));
        }

        [Test]
        public void Quantizer_KeepsSameColorWithDifferentSurfacesInSeparateSlots()
        {
            VoxelColorPalette colors = CreateColors(1);
            VoxelSurfacePalette surfaces = CreateSurfaces(2);
            try
            {
                var grid = new VoxelGrid(new Vector3Int(2, 1, 1), Vector3.zero, 1f, true);
                grid.Occupied[0] = grid.Occupied[1] = true;
                grid.SemanticIds[0] = VoxelSemanticEncoding.Pack(1, 1);
                grid.SemanticIds[1] = VoxelSemanticEncoding.Pack(1, 2);

                QuantizedVoxels result = VoxelSemanticQuantizer.Quantize(grid, colors, surfaces);

                Assert.That(result.SemanticSlots, Has.Length.EqualTo(2));
                Assert.That(result.Indices[0], Is.Not.EqualTo(result.Indices[1]));
                Assert.That(result.Palette[0], Is.EqualTo(result.Palette[1]));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(colors);
                UnityEngine.Object.DestroyImmediate(surfaces);
            }
        }

        [Test]
        public void Quantizer_IsDeterministicWhenVoxelTraversalChanges()
        {
            VoxelColorPalette colors = CreateColors(2);
            VoxelSurfacePalette surfaces = CreateSurfaces(2);
            try
            {
                var first = new VoxelGrid(new Vector3Int(4, 1, 1), Vector3.zero, 1f, true);
                var second = new VoxelGrid(new Vector3Int(4, 1, 1), Vector3.zero, 1f, true);
                ushort a = VoxelSemanticEncoding.Pack(1, 2);
                ushort b = VoxelSemanticEncoding.Pack(2, 1);
                ushort[] firstValues = { a, b, a, b };
                ushort[] secondValues = { b, a, b, a };
                for (int i = 0; i < 4; i++)
                {
                    first.Occupied[i] = second.Occupied[i] = true;
                    first.SemanticIds[i] = firstValues[i];
                    second.SemanticIds[i] = secondValues[i];
                }

                QuantizedVoxels firstResult = VoxelSemanticQuantizer.Quantize(first, colors, surfaces);
                QuantizedVoxels secondResult = VoxelSemanticQuantizer.Quantize(second, colors, surfaces);

                CollectionAssert.AreEqual(
                    firstResult.SemanticSlots.Select(SemanticPair).ToArray(),
                    secondResult.SemanticSlots.Select(SemanticPair).ToArray());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(colors);
                UnityEngine.Object.DestroyImmediate(surfaces);
            }
        }

        [Test]
        public void Quantizer_Accepts255PairsAndRejects256()
        {
            VoxelColorPalette colors = CreateColors(0);
            VoxelSurfacePalette surfaces = CreateSurfaces(255);
            try
            {
                VoxelGrid valid = CreateSurfaceIdGrid(255);
                Assert.That(VoxelSemanticQuantizer.Quantize(valid, colors, surfaces)
                    .SemanticSlots, Has.Length.EqualTo(255));

                VoxelGrid invalid = CreateSurfaceIdGrid(256);
                InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
                    VoxelSemanticQuantizer.Quantize(invalid, colors, surfaces));
                StringAssert.Contains("at most 255", exception.Message);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(colors);
                UnityEngine.Object.DestroyImmediate(surfaces);
            }
        }

        [Test]
        public void Resolver_RejectsPaletteSnapshotChangesAndNonIdentityImap()
        {
            VoxelColorPalette colors = CreateColors(1);
            VoxelSurfacePalette surfaces = CreateSurfaces(1);
            string path = Path.Combine(Path.GetTempPath(), "VoxelSemanticValidation.vox");
            try
            {
                VoxelGrid grid = OneSemanticVoxel(VoxelSemanticEncoding.Pack(1, 1));
                QuantizedVoxels quantized = VoxelSemanticQuantizer.Quantize(grid, colors, surfaces);
                VoxelChunkedVoxWriter.Write(path, grid, quantized, 16);
                VoxelSemanticMetadata metadata = CreateMetadata(
                    quantized.SemanticSlots, colors, surfaces);
                byte[] original = File.ReadAllBytes(path);
                VoxelSemanticVoxDocument document = VoxelSemanticVoxDocument.Parse(original);

                metadata.slots[0].displayColor = Color.red;
                metadata.slotTableHash = VoxelSemanticTransport.ComputeSlotTableHash(metadata.slots);
                Assert.That(VoxelSemanticTransport.TryResolveSlotSemantics(
                    document, metadata, colors, surfaces, out _, out _, out string colorError), Is.False);
                StringAssert.Contains("color of slot", colorError);

                metadata = CreateMetadata(quantized.SemanticSlots, colors, surfaces);
                byte[] imap = Enumerable.Range(0, 256).Select(index => (byte)index).ToArray();
                (imap[1], imap[2]) = (imap[2], imap[1]);
                VoxelSemanticVoxDocument remapped = VoxelSemanticVoxDocument.Parse(
                    AppendChunk(original, "IMAP", imap));
                Assert.That(VoxelSemanticTransport.TryResolveSlotSemantics(
                    remapped, metadata, colors, surfaces, out _, out _, out string imapError), Is.False);
                StringAssert.Contains("IMAP", imapError);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                UnityEngine.Object.DestroyImmediate(colors);
                UnityEngine.Object.DestroyImmediate(surfaces);
            }
        }

        [Test]
        public void SemanticRewrite_PreservesExistingNoteChunkExactly()
        {
            VoxelColorPalette colors = CreateColors(1);
            VoxelSurfacePalette surfaces = CreateSurfaces(1);
            string path = Path.Combine(Path.GetTempPath(), "VoxelSemanticNote.vox");
            try
            {
                VoxelGrid grid = OneSemanticVoxel(VoxelSemanticEncoding.Pack(1, 1));
                QuantizedVoxels quantized = VoxelSemanticQuantizer.Quantize(grid, colors, surfaces);
                VoxelChunkedVoxWriter.Write(path, grid, quantized, 16);
                byte[] noteContent = BuildNotes("palette row 0", "palette row 1");
                byte[] bytesWithNote = AppendChunk(File.ReadAllBytes(path), "NOTE", noteContent);
                byte[] noteChunk = BuildChunk("NOTE", noteContent);

                byte[] rewritten = VoxelSemanticVoxDocument.Parse(bytesWithNote)
                    .BuildSemanticBytes(quantized.SemanticSlots, out _);

                Assert.That(ContainsSequence(rewritten, noteChunk), Is.True);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                UnityEngine.Object.DestroyImmediate(colors);
                UnityEngine.Object.DestroyImmediate(surfaces);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Binding_ConsolidatesPairsAcrossChunksAndPreservesGeometry(bool separateSurface)
        {
            string folderName = "VoxelSemanticMerge_" + Guid.NewGuid().ToString("N");
            string folder = "Assets/" + folderName;
            AssetDatabase.CreateFolder("Assets", folderName);
            VoxelColorPalette colors = CreateColors(1);
            VoxelSurfacePalette surfaces = CreateSurfaces(1);
            AssetDatabase.CreateAsset(colors, folder + "/Colors.asset");
            AssetDatabase.CreateAsset(surfaces, folder + "/Surfaces.asset");
            string voxPath = folder + "/Model.vox";
            string absolute = VoxelLodPipeline.AssetPathToAbsolute(voxPath);
            try
            {
                var grid = new VoxelGrid(new Vector3Int(33, 1, 1), Vector3.zero, 0.032f);
                int[] positions = { 0, 16, 32 };
                Color32[] sourceColors = { Color.red, Color.green, Color.blue };
                for (int i = 0; i < positions.Length; i++)
                {
                    grid.Occupied[positions[i]] = true;
                    grid.Colors[positions[i]] = sourceColors[i];
                }
                QuantizedVoxels quantized = VoxelColorQuantizer.Quantize(grid);
                VoxWriteResult write = VoxelChunkedVoxWriter.Write(absolute, grid, quantized, 16);
                Assert.That(write.Chunks, Has.Length.EqualTo(3));
                byte[] note = BuildNotes("Test palette row");
                byte[] material = { 2, 0, 0, 0, 0, 0, 0, 0 };
                byte[] original = AppendChunk(AppendChunk(File.ReadAllBytes(absolute),
                    "NOTE", note), "MATL", material);
                File.WriteAllBytes(absolute, original);
                var metadata = new VoxelBridgeMetadata
                {
                    formatVersion = 3, voxelSize = grid.VoxelSize, unityGridSize = grid.Size,
                    chunks = write.Chunks, voxelCount = 3, paletteColorCount = 3
                };
                string sidecar = VoxelImporterIntegration.GetMetadataAssetPath(voxPath);
                File.WriteAllText(VoxelLodPipeline.AssetPathToAbsolute(sidecar), JsonUtility.ToJson(metadata));
                colors.TryGetColor(1, out Color32 displayColor);
                VoxelSemanticVoxDocument document = VoxelSemanticVoxDocument.Parse(original);
                VoxelSemanticSlotMetadata[] bindings = document.UsedSlots.Select(slot =>
                    new VoxelSemanticSlotMetadata
                    {
                        slot = slot, colorId = 1,
                        surfaceId = separateSurface && slot == document.UsedSlots.Last() ? 1 : 0,
                        displayColor = displayColor
                    }).ToArray();
                byte[] expected = document.BuildSemanticBytes(bindings, out var canonical);
                CollectionAssert.AreEqual(expected,
                    document.BuildSemanticBytes(bindings.Reverse().ToArray(), out _));
                Assert.That(canonical.Length, Is.EqualTo(separateSurface ? 2 : 1));
                Assert.That(canonical[0].slot, Is.EqualTo(document.UsedSlots[0]));
                CollectionAssert.AreEqual(original, File.ReadAllBytes(absolute));
                Assert.Throws<InvalidDataException>(() => document.BuildSemanticBytes(
                    bindings.Concat(new[] { bindings[0] }).ToArray(), out _));
                Assert.Throws<InvalidDataException>(() => document.BuildSemanticBytes(
                    bindings.Skip(1).ToArray(), out _));
                Assert.Throws<InvalidDataException>(() => VoxelSemanticVoxDocument.Parse(
                    AppendChunk(original, "IMAP", new byte[256])).BuildSemanticBytes(bindings, out _));

                int originalSurface = bindings[0].surfaceId;
                bindings[0].surfaceId = 99;
                Assert.That(VoxelSemanticBindingService.TryBind(voxPath, bindings,
                    colors, surfaces, null, out string rejected), Is.False);
                StringAssert.Contains("unknown SurfaceID", rejected);
                CollectionAssert.AreEqual(original, File.ReadAllBytes(absolute));
                Assert.That(JsonUtility.FromJson<VoxelBridgeMetadata>(File.ReadAllText(
                    VoxelLodPipeline.AssetPathToAbsolute(sidecar))).formatVersion, Is.EqualTo(3));
                bindings[0].surfaceId = originalSurface;

                Assert.That(VoxelSemanticBindingService.TryBind(voxPath, bindings,
                    colors, surfaces, null, out string error), Is.True, error);
                Assert.That(VoxelImporterIntegration.TryLoadMetadata(voxPath,
                    out VoxelBridgeMetadata saved, out error), Is.True, error);
                Assert.That(saved.semantic.slots.Length, Is.EqualTo(canonical.Length));
                Assert.That(saved.paletteColorCount, Is.EqualTo(canonical.Length));
                byte[] result = File.ReadAllBytes(absolute);
                CollectionAssert.AreEqual(expected, result);
                Assert.That(ContainsSequence(result, BuildChunk("NOTE", note)), Is.True);
                Assert.That(ContainsSequence(result, BuildChunk("MATL", material)), Is.True);
                VoxelSemanticVoxDocument rewritten = VoxelSemanticVoxDocument.Parse(result);
                Assert.That(rewritten.SlotUsageCounts.Sum(), Is.EqualTo(3));
                Assert.That(rewritten.UsedSlots.Length, Is.EqualTo(canonical.Length));
                VoxelGrid restored = VoxelVolumeReader.Read(absolute, saved);
                CollectionAssert.AreEqual(grid.Occupied, restored.Occupied);
                foreach (int position in positions)
                {
                    var binding = bindings.Single(entry => entry.slot == quantized.Indices[position]);
                    Assert.That(restored.SemanticIds[position],
                        Is.EqualTo(VoxelSemanticEncoding.Pack(binding.colorId, binding.surfaceId)));
                }
                Assert.That(VoxelSemanticBindingService.TryBind(voxPath, saved.semantic.slots,
                    colors, surfaces, null, out error), Is.True, error);
                CollectionAssert.AreEqual(result, File.ReadAllBytes(absolute));
            }
            finally
            {
                AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void SemanticChunkedWriterAndReader_RoundTripPairs()
        {
            string folderName = "VoxelSemanticRoundTrip_" + Guid.NewGuid().ToString("N");
            string assetFolder = "Assets/" + folderName;
            AssetDatabase.CreateFolder("Assets", folderName);
            VoxelColorPalette colors = CreateColors(2);
            VoxelSurfacePalette surfaces = CreateSurfaces(2);
            AssetDatabase.CreateAsset(colors, assetFolder + "/Colors.asset");
            AssetDatabase.CreateAsset(surfaces, assetFolder + "/Surfaces.asset");
            AssetDatabase.SaveAssets();
            string path = Path.Combine(Path.GetTempPath(), "VoxelSemanticChunkRoundTrip.vox");
            try
            {
                var grid = new VoxelGrid(
                    new Vector3Int(35, 3, 2), new Vector3(-1f, 2f, 3f), 0.1f, true);
                int[] indices = { grid.Index(0, 0, 0), grid.Index(17, 1, 0), grid.Index(34, 2, 1) };
                ushort[] semantics =
                {
                    VoxelSemanticEncoding.Pack(1, 1),
                    VoxelSemanticEncoding.Pack(2, 2),
                    VoxelSemanticEncoding.Pack(1, 2)
                };
                for (int i = 0; i < indices.Length; i++)
                {
                    grid.Occupied[indices[i]] = true;
                    grid.SemanticIds[indices[i]] = semantics[i];
                }
                QuantizedVoxels quantized = VoxelSemanticQuantizer.Quantize(grid, colors, surfaces);
                VoxWriteResult write = VoxelChunkedVoxWriter.Write(path, grid, quantized, 16);
                Assert.That(VoxelSemanticTransport.TryCreateMetadata(
                    quantized.SemanticSlots, colors, surfaces,
                    out VoxelSemanticMetadata semanticMetadata, out string semanticError),
                    Is.True, semanticError);
                Assert.That(semanticMetadata.colorPaletteGuid, Is.Not.Empty);
                Assert.That(semanticMetadata.surfacePaletteGuid, Is.Not.Empty);
                var metadata = new VoxelBridgeMetadata
                {
                    formatVersion = 4,
                    voxelSize = grid.VoxelSize,
                    gridOrigin = grid.Origin,
                    unityGridSize = grid.Size,
                    chunks = write.Chunks,
                    semantic = semanticMetadata
                };

                VoxelGrid restored = VoxelVolumeReader.Read(path, metadata);

                Assert.That(restored.IsSemantic, Is.True);
                for (int i = 0; i < indices.Length; i++)
                    Assert.That(restored.SemanticIds[indices[i]], Is.EqualTo(semantics[i]));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                AssetDatabase.DeleteAsset(assetFolder);
            }
        }

        [Test]
        public void VolumeReader_UsesFormatVersionToDistinguishRgbAndSemanticData()
        {
            var grid = new VoxelGrid(Vector3Int.one, Vector3.zero, 1f);
            grid.Occupied[0] = true;
            grid.Colors[0] = new Color32(12, 34, 56, 255);
            string path = Path.Combine(Path.GetTempPath(), "VoxelSemanticVersionGate.vox");
            try
            {
                QuantizedVoxels quantized = VoxelColorQuantizer.Quantize(grid);
                VoxWriteResult write = VoxelChunkedVoxWriter.Write(path, grid, quantized, 16);
                var rgbMetadata = new VoxelBridgeMetadata
                {
                    formatVersion = 3,
                    voxelSize = 1f,
                    unityGridSize = Vector3Int.one,
                    chunks = write.Chunks,
                    semantic = new VoxelSemanticMetadata()
                };

                VoxelGrid restored = VoxelVolumeReader.Read(path, rgbMetadata);
                Assert.That(restored.IsSemantic, Is.False);
                Assert.That(restored.Colors[0], Is.EqualTo(grid.Colors[0]));

                rgbMetadata.formatVersion = 4;
                rgbMetadata.semantic = null;
                InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
                    VoxelVolumeReader.Read(path, rgbMetadata));
                StringAssert.Contains("v4", exception.Message);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void VolumeReader_RejectsVoxelsOutsideSavedBounds()
        {
            var grid = new VoxelGrid(new Vector3Int(2, 1, 1), Vector3.zero, 1f);
            grid.Occupied[1] = true;
            grid.Colors[1] = Color.red;
            string path = Path.Combine(Path.GetTempPath(),
                "VoxelBounds_" + Guid.NewGuid().ToString("N") + ".vox");
            try
            {
                VoxWriteResult written = VoxelChunkedVoxWriter.Write(
                    path, grid, VoxelColorQuantizer.Quantize(grid), 16);
                var metadata = new VoxelBridgeMetadata
                {
                    voxelSize = 1f,
                    unityGridSize = Vector3Int.one,
                    chunks = written.Chunks
                };
                Assert.Throws<InvalidDataException>(() => VoxelVolumeReader.Read(path, metadata));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void VolumeReader_RejectsCorruptChunkLengths(bool negativeLength)
        {
            var grid = new VoxelGrid(Vector3Int.one, Vector3.zero, 1f);
            grid.Occupied[0] = true;
            grid.Colors[0] = Color.red;
            string path = Path.Combine(Path.GetTempPath(),
                "VoxelChunkBounds_" + Guid.NewGuid().ToString("N") + ".vox");
            try
            {
                VoxWriteResult written = VoxelChunkedVoxWriter.Write(
                    path, grid, VoxelColorQuantizer.Quantize(grid), 16);
                byte[] bytes = File.ReadAllBytes(path);
                if (negativeLength)
                {
                    // MAIN child byte count follows VOX and MAIN headers.
                    Array.Copy(BitConverter.GetBytes(-1), 0, bytes, 16, sizeof(int));
                }
                else
                {
                    Array.Resize(ref bytes, bytes.Length - 1);
                }
                File.WriteAllBytes(path, bytes);
                var metadata = new VoxelBridgeMetadata
                {
                    voxelSize = 1f,
                    unityGridSize = Vector3Int.one,
                    chunks = written.Chunks
                };
                Assert.Throws<InvalidDataException>(() => VoxelVolumeReader.Read(path, metadata));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void SemanticDownsampler_UsesExposedAreaAndLowerPairForTies()
        {
            var majority = new VoxelGrid(new Vector3Int(4, 1, 1), Vector3.zero, 0.1f, true);
            ushort low = VoxelSemanticEncoding.Pack(1, 1);
            ushort high = VoxelSemanticEncoding.Pack(2, 2);
            for (int i = 0; i < 4; i++) majority.Occupied[i] = true;
            majority.SemanticIds[0] = high;
            majority.SemanticIds[1] = low;
            majority.SemanticIds[2] = high;
            majority.SemanticIds[3] = high;

            VoxelGrid majorityResult = VoxelGridDownsampler.Downsample(majority, 0.4f, 0, 16);
            Assert.That(majorityResult.SemanticIds.Single(), Is.EqualTo(high));

            // Each pair covers one end cap and two side segments: equal exposed area.
            majority.SemanticIds[1] = high;
            majority.SemanticIds[2] = low;
            majority.SemanticIds[3] = low;
            VoxelGrid tieResult = VoxelGridDownsampler.Downsample(majority, 0.4f, 0, 16);
            Assert.That(tieResult.SemanticIds.Single(), Is.EqualTo(low));
        }

        private static VoxelGrid OneSemanticVoxel(ushort semantic)
        {
            var grid = new VoxelGrid(Vector3Int.one, Vector3.zero, 1f, true);
            grid.Occupied[0] = true;
            grid.SemanticIds[0] = semantic;
            return grid;
        }

        private static VoxelGrid CreateSurfaceIdGrid(int count)
        {
            var grid = new VoxelGrid(new Vector3Int(count, 1, 1), Vector3.zero, 1f, true);
            for (int i = 0; i < count; i++)
            {
                grid.Occupied[i] = true;
                grid.SemanticIds[i] = VoxelSemanticEncoding.Pack(0, i);
            }
            return grid;
        }

        private static VoxelColorPalette CreateColors(int maximumId)
        {
            VoxelColorPalette palette = ScriptableObject.CreateInstance<VoxelColorPalette>();
            palette.MutableEntries.Clear();
            for (int id = 0; id <= maximumId; id++)
                palette.MutableEntries.Add(new VoxelColorDefinition(
                    id, "Color " + id, new Color32((byte)(20 + id), 80, 160, 255)));
            return palette;
        }

        private static VoxelSurfacePalette CreateSurfaces(int maximumId)
        {
            VoxelSurfacePalette palette = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
            palette.MutableEntries.Clear();
            for (int id = 0; id <= maximumId; id++)
                palette.MutableEntries.Add(new VoxelSurfaceDefinition(
                    id, "Surface " + id, VoxelSurfaceRenderClass.Opaque,
                    0f, 0.25f, 0f, 1f));
            return palette;
        }

        private static VoxelSemanticMetadata CreateMetadata(
            VoxelSemanticSlotMetadata[] slots,
            VoxelColorPalette colors, VoxelSurfacePalette surfaces)
        {
            Assert.That(VoxelPaletteLutBuilder.TryBuildColorPixels(
                colors, out _, out string colorHash, out string colorError), Is.True, colorError);
            Assert.That(VoxelPaletteLutBuilder.TryBuildSurfacePixels(
                surfaces, out _, out string surfaceHash, out string surfaceError), Is.True, surfaceError);
            return new VoxelSemanticMetadata
            {
                formatVersion = 1,
                colorPaletteAssetPath = AssetDatabase.GetAssetPath(colors),
                colorPaletteGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(colors)),
                colorPaletteHash = colorHash,
                surfacePaletteAssetPath = AssetDatabase.GetAssetPath(surfaces),
                surfacePaletteGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(surfaces)),
                surfacePaletteHash = surfaceHash,
                slotTableHash = VoxelSemanticTransport.ComputeSlotTableHash(slots),
                slots = slots.Select(entry => new VoxelSemanticSlotMetadata
                {
                    slot = entry.slot,
                    colorId = entry.colorId,
                    surfaceId = entry.surfaceId,
                    displayColor = entry.displayColor
                }).ToArray()
            };
        }

        private static int SemanticPair(VoxelSemanticSlotMetadata entry) =>
            VoxelSemanticEncoding.Pack(entry.colorId, entry.surfaceId);

        private static byte[] BuildNotes(params string[] notes)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(notes.Length);
            foreach (string note in notes)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(note);
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
            return stream.ToArray();
        }

        private static byte[] AppendChunk(byte[] vox, string id, byte[] content)
        {
            byte[] chunk = BuildChunk(id, content);
            byte[] output = new byte[vox.Length + chunk.Length];
            Buffer.BlockCopy(vox, 0, output, 0, vox.Length);
            Buffer.BlockCopy(chunk, 0, output, vox.Length, chunk.Length);
            int childrenBytes = BitConverter.ToInt32(output, 16);
            Buffer.BlockCopy(BitConverter.GetBytes(checked(childrenBytes + chunk.Length)),
                0, output, 16, 4);
            return output;
        }

        private static byte[] BuildChunk(string id, byte[] content)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(Encoding.ASCII.GetBytes(id));
            writer.Write(content.Length);
            writer.Write(0);
            writer.Write(content);
            return stream.ToArray();
        }

        private static bool ContainsSequence(byte[] source, byte[] sequence)
        {
            for (int i = 0; i <= source.Length - sequence.Length; i++)
            {
                int j = 0;
                while (j < sequence.Length && source[i + j] == sequence[j]) j++;
                if (j == sequence.Length) return true;
            }
            return false;
        }
    }
}
