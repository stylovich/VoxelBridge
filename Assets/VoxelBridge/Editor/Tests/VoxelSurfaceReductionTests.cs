using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelSurfaceReductionTests
    {
        private static readonly ushort Default = VoxelSemanticEncoding.Pack(5, 0);
        private static readonly ushort Ceramic = VoxelSemanticEncoding.Pack(5, 10);
        private static readonly ushort Led = VoxelSemanticEncoding.Pack(63, 12);

        [TestCase(2, false)]
        [TestCase(2, true)]
        [TestCase(4, true)]
        public void PaintedSkin_WinsOverInteriorFill(int factor, bool hideCavities)
        {
            var source = Filled(factor * 3, Default);
            PaintFront(source, Ceramic);
            var result = VoxelGridDownsampler.Downsample(source, factor, 0, 16, hideCavities);
            Assert.That(result.SemanticIds[result.Index(1, 1, 0)], Is.EqualTo(Ceramic));
            Assert.That(result.SemanticIds[result.Index(1, 1, 1)], Is.EqualTo(Default));
            Assert.That(result.CountOccupied(), Is.EqualTo(27));
        }

        [Test]
        public void ExposedDefault_IsValidAndNotReplacedByInteriorMaterial()
        {
            var source = Filled(6, Ceramic);
            PaintFront(source, Default);
            var result = VoxelGridDownsampler.Downsample(source, 2, 0, 16);
            Assert.That(result.SemanticIds[result.Index(1, 1, 0)], Is.EqualTo(Default));
            Assert.That(result.SemanticIds[result.Index(1, 1, 1)], Is.EqualTo(Ceramic));
        }

        [Test]
        public void OppositeFacingCavity_DoesNotOutvoteTheVisibleSkin()
        {
            var source = Filled(6, Default);
            PaintFront(source, Ceramic);
            // Back faces of the z=1 fill face this pocket, not the coarse cell's outer front face.
            for (int x = 2; x < 4; x++)
            for (int y = 2; y < 4; y++) source.Occupied[source.Index(x, y, 2)] = false;
            var result = VoxelGridDownsampler.Downsample(source, 2, 0, 16, false);
            Assert.That(result.SemanticIds[result.Index(1, 1, 0)], Is.EqualTo(Ceramic));
        }

        [Test]
        public void InteriorCells_KeepVolumeMajorityAndStableTieBreak()
        {
            var source = Filled(6, Default);
            for (int x = 2; x < 4; x++)
            for (int y = 2; y < 4; y++)
            for (int z = 2; z < 4; z++) source.SemanticIds[source.Index(x, y, z)] = Ceramic;
            source.SemanticIds[source.Index(2, 2, 2)] = Default;
            var majority = VoxelGridDownsampler.Downsample(source, 2, 0, 16);
            Assert.That(majority.SemanticIds[majority.Index(1, 1, 1)], Is.EqualTo(Ceramic));
            for (int x = 2; x < 4; x++)
            for (int y = 2; y < 4; y++) source.SemanticIds[source.Index(x, y, 2)] = Default;
            var tie = VoxelGridDownsampler.Downsample(source, 2, 0, 16);
            Assert.That(tie.SemanticIds[tie.Index(1, 1, 1)], Is.EqualTo(Default));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void EnclosedCavities_RespectVisibilityPolicy(bool hideCavities)
        {
            var source = Filled(10, Ceramic);
            for (int z = 0; z < 10; z++)
            for (int y = 0; y < 10; y++)
            for (int x = 0; x < 10; x++)
            {
                int index = source.Index(x, y, z);
                if (x == 0 || y == 0 || z == 0 || x == 9 || y == 9 || z == 9) source.SemanticIds[index] = Default;
                else if ((x + y + z) % 2 == 0) source.Occupied[index] = false;
            }
            var result = VoxelGridDownsampler.Downsample(source, 10, 0, 16, hideCavities);
            Assert.That(result.CountOccupied(), Is.EqualTo(1));
            Assert.That(result.SemanticIds.Single(), Is.EqualTo(hideCavities ? Default : Ceramic));
        }

        [Test]
        public void LedPatch_RetainsItsColorSurfacePairWithoutPaintingOtherCells()
        {
            var source = Filled(6, Default);
            for (int x = 2; x < 4; x++)
            for (int y = 2; y < 4; y++) source.SemanticIds[source.Index(x, y, 0)] = Led;
            var result = VoxelGridDownsampler.Downsample(source, 2, 0, 16, true);
            Assert.That(result.SemanticIds[result.Index(1, 1, 0)], Is.EqualTo(Led));
            Assert.That(result.SemanticIds.Count(v => v == Led), Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TinyOrHiddenLed_HasNoUnconditionalPriority(bool hidden)
        {
            var source = Filled(6, Default);
            source.SemanticIds[source.Index(2, 2, hidden ? 1 : 0)] = Led;
            var result = VoxelGridDownsampler.Downsample(source, 2, 0, 16, true);
            Assert.That(result.SemanticIds.All(v => v != Led), Is.True);
        }

        [Test]
        public void ExposedArea_NotVolume_DecidesDifferentCoverage()
        {
            var source = new VoxelGrid(new Vector3Int(4, 1, 1), Vector3.zero, 1, true);
            for (int i = 0; i < 4; i++) source.Occupied[i] = true;
            source.SemanticIds[0] = Ceramic; source.SemanticIds[3] = Ceramic;
            source.SemanticIds[1] = Default; source.SemanticIds[2] = Default;
            var result = VoxelGridDownsampler.Downsample(source, 4, 0, 16);
            Assert.That(result.SemanticIds.Single(), Is.EqualTo(Ceramic), "The end caps contribute extra exposed area.");
        }

        [Test]
        public void GeometryMappingAndPairMembership_ArePreservedAcrossPaddingAndChunks()
        {
            var source = new VoxelGrid(new Vector3Int(40, 6, 6), new Vector3(-6, -4, -2), 1, true);
            var rgb = new VoxelGrid(source.Size, source.Origin, source.VoxelSize);
            var random = new System.Random(382);
            for (int i = 0; i < source.Occupied.Length; i++)
            {
                source.Occupied[i] = rgb.Occupied[i] = random.Next(5) != 0;
                source.SemanticIds[i] = VoxelSemanticEncoding.Pack(random.Next(4), random.Next(3));
                rgb.Colors[i] = new Color32(40, 80, 120, 255);
            }
            var baseline = VoxelGridDownsampler.Downsample(rgb, 2, 0, 16);
            var result = VoxelGridDownsampler.Downsample(source, 2, 0, 16, true);
            var padded = VoxelGridDownsampler.Downsample(source, 2, 1, 32, true);
            Assert.That(result.Size, Is.EqualTo(baseline.Size));
            Assert.That(result.Origin, Is.EqualTo(baseline.Origin));
            Assert.That(result.Occupied, Is.EqualTo(baseline.Occupied));
            var members = new Dictionary<int, HashSet<ushort>>();
            for (int i = 0; i < source.Occupied.Length; i++)
            {
                if (!source.Occupied[i]) continue;
                int target = CellAt(result, Center(source, i));
                if (!members.TryGetValue(target, out var pairs)) members[target] = pairs = new HashSet<ushort>();
                pairs.Add(source.SemanticIds[i]);
            }
            foreach (var pair in members)
            {
                Assert.That(pair.Value.Contains(result.SemanticIds[pair.Key]), Is.True);
                int paddedCell = CellAt(padded, Center(result, pair.Key));
                Assert.That(padded.Occupied[paddedCell], Is.True);
                Assert.That(padded.SemanticIds[paddedCell], Is.EqualTo(result.SemanticIds[pair.Key]));
            }
            Assert.That(padded.CountOccupied(), Is.EqualTo(result.CountOccupied()));
        }

        [TestCase(.3f)]
        [TestCase(.6f)]
        [TestCase(.8f)]
        public void Cancellation_LeavesSourceUnchanged(float cancelAt)
        {
            var source = Filled(48, Default); PaintFront(source, Led);
            var occupied = source.Occupied.ToArray(); var ids = source.SemanticIds.ToArray();
            Assert.Throws<OperationCanceledException>(() => VoxelGridDownsampler.Downsample(source, 2, 0, 16, true,
                value => { if (value >= cancelAt) throw new OperationCanceledException(); }));
            Assert.That(source.Occupied, Is.EqualTo(occupied));
            Assert.That(source.SemanticIds, Is.EqualTo(ids));
        }

        [Test]
        public void RepeatedReduction_IsDeterministicAndReportsMonotonicProgress()
        {
            var source = Filled(6, Default); PaintFront(source, Led);
            var progress = new List<float>();
            var first = VoxelGridDownsampler.Downsample(source, 2, 0, 16, true, progress.Add);
            var second = VoxelGridDownsampler.Downsample(source, 2, 0, 16, true);
            Assert.That(first.SemanticIds, Is.EqualTo(second.SemanticIds));
            Assert.That(progress.Last(), Is.EqualTo(1));
            Assert.That(progress, Is.Ordered);
        }

        [Test]
        public void RgbReduction_KeepsTheExistingAverage()
        {
            var source = new VoxelGrid(new Vector3Int(4, 1, 1), Vector3.zero, 1);
            for (int i = 0; i < 4; i++) { source.Occupied[i] = true; source.Colors[i] = new Color32((byte)(10 + 20 * i), 80, 120, 255); }
            var result = VoxelGridDownsampler.Downsample(source, 4, 0, 16);
            Assert.That(result.Colors.Single(), Is.EqualTo(new Color32(40, 80, 120, 255)));
        }

        [Test]
        public void ProductionDerivation_WritesExposedPairsAndKeepsParentUnchanged()
        {
            string folder = "Assets/SurfaceReductionTest_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            string materialPath = null;
            try
            {
                var colors = ScriptableObject.CreateInstance<VoxelColorPalette>();
                var surfaces = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
                var profile = ScriptableObject.CreateInstance<VoxelStyleProfile>();
                AssetDatabase.CreateAsset(colors, folder + "/Colors.asset");
                AssetDatabase.CreateAsset(surfaces, folder + "/Surfaces.asset");
                AssetDatabase.CreateAsset(profile, folder + "/Profile.asset");
                Assert.That(VoxelPaletteLutGenerator.TryRebuild(colors, out _, out string error), Is.True, error);
                Assert.That(VoxelPaletteLutGenerator.TryRebuild(surfaces, out _, out error), Is.True, error);
                var grid = Filled(8, Default); PaintFront(grid, Ceramic);
                for (int x = 2; x < 4; x++)
                for (int y = 2; y < 4; y++) grid.SemanticIds[grid.Index(x, y, 0)] = Led;
                string source = folder + "/Source.vox", sidecar = VoxelImporterIntegration.GetMetadataAssetPath(source);
                var quantized = VoxelSemanticQuantizer.Quantize(grid, colors, surfaces);
                var write = VoxelChunkedVoxWriter.Write(VoxelLodPipeline.AssetPathToAbsolute(source), grid, quantized, 128);
                Assert.That(VoxelSemanticTransport.TryCreateMetadata(quantized.SemanticSlots, colors, surfaces,
                    out var semantic, out error), Is.True, error);
                var metadata = new VoxelBridgeMetadata { formatVersion = 4, sourceName = "Source", voxelSize = 1,
                    baseVoxelSize = 1, gridOrigin = grid.Origin, unityGridSize = grid.Size, chunks = write.Chunks,
                    sourceBoundsMax = Vector3.one * 8, chunkCellSize = 128, semantic = semantic, hideInternalCavities = true };
                File.WriteAllText(sidecar, JsonUtility.ToJson(metadata, true));
                AssetDatabase.ImportAsset(sidecar, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(source, ImportAssetOptions.ForceSynchronousImport);
                byte[] original = File.ReadAllBytes(source), originalMetadata = File.ReadAllBytes(sidecar);
                string manifestPath = VoxelProductionFamily.Create(source, profile, folder);
                var family = VoxelProductionFamily.Load(manifestPath);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(family.prefabAssetPath);
                materialPath = AssetDatabase.GetAssetPath(prefab.GetComponentInChildren<MeshRenderer>().sharedMaterial);
                VoxelProductionFamily.DeriveLevel(manifestPath, 1, VoxelLodGenerationMode.ReduceParent);
                family = VoxelProductionFamily.Load(manifestPath);
                var reduced = VoxelProductionExporter.ReadGrid(VoxelProductionFamily.SourcePath(family.lods[1]),
                    out var childMetadata, out _, out _, out _);
                Assert.That(reduced.SemanticIds[CellAt(reduced, new Vector3(3, 3, 1))], Is.EqualTo(Led));
                Assert.That(reduced.SemanticIds[CellAt(reduced, new Vector3(5, 5, 1))], Is.EqualTo(Ceramic));
                Assert.That(childMetadata.hideInternalCavities, Is.True);
                Assert.That(family.lods[1].builtSourceHash, Is.EqualTo(VoxelProductionFamily.SourceHash(family.lods[1])));
                Assert.That(VoxelProductionFamily.IsDerivedStale(family, 1), Is.False);
                Assert.That(File.ReadAllBytes(source), Is.EqualTo(original));
                Assert.That(File.ReadAllBytes(sidecar), Is.EqualTo(originalMetadata));
            }
            finally
            {
                AssetDatabase.DeleteAsset(folder);
                if (materialPath != null) AssetDatabase.DeleteAsset(materialPath);
            }
        }

        private static VoxelGrid Filled(int size, ushort semantic)
        {
            var grid = new VoxelGrid(Vector3Int.one * size, Vector3.zero, 1, true);
            for (int i = 0; i < grid.Occupied.Length; i++) { grid.Occupied[i] = true; grid.SemanticIds[i] = semantic; }
            return grid;
        }

        private static void PaintFront(VoxelGrid grid, ushort semantic)
        {
            for (int y = 0; y < grid.Size.y; y++)
            for (int x = 0; x < grid.Size.x; x++) grid.SemanticIds[grid.Index(x, y, 0)] = semantic;
        }

        private static Vector3 Center(VoxelGrid grid, int index)
        {
            grid.Coordinates(index, out int x, out int y, out int z);
            return grid.Origin + new Vector3(x + .5f, y + .5f, z + .5f) * grid.VoxelSize;
        }

        private static int CellAt(VoxelGrid grid, Vector3 point)
        {
            Vector3 cell = (point - grid.Origin) / grid.VoxelSize;
            return grid.Index(Mathf.FloorToInt(cell.x), Mathf.FloorToInt(cell.y), Mathf.FloorToInt(cell.z));
        }
    }
}
