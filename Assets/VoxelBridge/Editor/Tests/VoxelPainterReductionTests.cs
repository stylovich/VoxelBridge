using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelPainterReductionTests
    {
        private static readonly ushort Default = VoxelSemanticEncoding.Pack(5, 0);
        private static readonly ushort Ceramic = VoxelSemanticEncoding.Pack(5, 10);
        private static readonly ushort Led = VoxelSemanticEncoding.Pack(63, 12);

        private static VoxelGrid Filled()
        {
            var grid = new VoxelGrid(Vector3Int.one * 6, Vector3.zero, 1, true);
            for (int i = 0; i < grid.Occupied.Length; i++) { grid.Occupied[i] = true; grid.SemanticIds[i] = Default; }
            return grid;
        }

        [TestCase(false, 0)]
        [TestCase(true, 2)]
        public void Preview_MatchesRealReducerAndDoesNotMutateDraft(bool hide, int padding)
        {
            var source = Filled(); source.SemanticIds[source.Index(2, 2, 0)] = Led;
            var before = (ushort[])source.SemanticIds.Clone(); var occupied = (bool[])source.Occupied.Clone();
            using var preview = new VoxelPainterReductionPreview(source, 2, padding, 16, hide);
            var real = VoxelGridDownsampler.Downsample(source, 2, padding, 16, hide);
            Assert.That(preview.Reduced.Size, Is.EqualTo(real.Size));
            Assert.That(preview.Reduced.Origin, Is.EqualTo(real.Origin));
            CollectionAssert.AreEqual(real.Occupied, preview.Reduced.Occupied);
            CollectionAssert.AreEqual(real.SemanticIds, preview.Reduced.SemanticIds);
            CollectionAssert.AreEqual(before, source.SemanticIds);
            CollectionAssert.AreEqual(occupied, source.Occupied);
            Assert.That(preview.Mesh.hideFlags, Is.EqualTo(HideFlags.HideAndDontSave));
        }

        [Test]
        public void Diagnostic_ReportsTinyVisibleFinishButNotBuriedFill()
        {
            var source = Filled(); int visible = source.Index(2, 2, 0), buried = source.Index(2, 2, 1);
            source.SemanticIds[visible] = Led; source.SemanticIds[buried] = Ceramic;
            using var preview = new VoxelPainterReductionPreview(source, 2, 0, 16, true);
            Assert.That(preview.ChangedFaces[visible], Is.EqualTo(16), "Only the outward -Z face contributes.");
            Assert.That(preview.ChangedFaces[buried], Is.Zero);
            Assert.That(preview.ChangedCells, Is.EqualTo(1));
        }

        [Test]
        public void Diagnostic_DoesNotReportInteriorDefaultWhenPaintedSkinWins()
        {
            var source = Filled();
            for (int x = 0; x < 6; x++) for (int y = 0; y < 6; y++) source.SemanticIds[source.Index(x, y, 0)] = Ceramic;
            using var preview = new VoxelPainterReductionPreview(source, 2, 0, 16, true);
            Assert.That(preview.Reduced.SemanticIds[preview.Reduced.Index(1, 1, 0)], Is.EqualTo(Ceramic));
            Assert.That(preview.ChangedFaces[source.Index(2, 2, 0)], Is.Zero);
            Assert.That(preview.ChangedFaces[source.Index(2, 2, 1)], Is.Zero);
        }

        [Test]
        public void Diagnostic_IgnoresIncompatibleCavityFaces()
        {
            var source = Filled();
            for (int x = 0; x < 6; x++) for (int y = 0; y < 6; y++) source.SemanticIds[source.Index(x, y, 0)] = Ceramic;
            for (int x = 2; x < 4; x++) for (int y = 2; y < 4; y++) source.Occupied[source.Index(x, y, 2)] = false;
            using var preview = new VoxelPainterReductionPreview(source, 2, 0, 16, false);
            Assert.That(preview.ChangedFaces[source.Index(2, 2, 1)], Is.Zero, "+Z cavity is not a -Z coarse contribution.");
        }

        [Test]
        public void Diagnostic_DetectsColorOnlyChanges()
        {
            var source = Filled(); int visible = source.Index(2, 2, 0);
            source.SemanticIds[visible] = VoxelSemanticEncoding.Pack(63, 0);
            using var preview = new VoxelPainterReductionPreview(source, 2, 0, 16, true);
            Assert.That(preview.ChangedFaces[visible], Is.EqualTo(16));
        }

        [Test]
        public void LossMesh_FlagsOnlyRequestedOrientationAndRetainsIds()
        {
            var source = new VoxelGrid(Vector3Int.one, Vector3.zero, 1, true);
            source.Occupied[0] = true; source.SemanticIds[0] = Led;
            Mesh mesh = VoxelSemanticMesher.BuildReductionLossPreview(source, new byte[] { 16 }, false);
            try
            {
                Assert.That(mesh.uv2.Count(v => v.x == 1), Is.EqualTo(4));
                for (int i = 0; i < mesh.vertexCount; i++)
                    Assert.That(mesh.uv2[i].x > .5f, Is.EqualTo(mesh.normals[i] == Vector3.back));
                Assert.That(mesh.uv.All(v => v.x == 63), Is.True);
                Assert.That(mesh.uv4.All(v => v.x == 12), Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void Preview_ReusesLossMeshAndDisposesAllTemporaryMeshes()
        {
            var preview = new VoxelPainterReductionPreview(Filled(), 2, 0, 16, true);
            preview.BuildLossMesh(); Mesh loss = preview.LossMesh, mesh = preview.Mesh;
            preview.BuildLossMesh(_ => Assert.Fail("Cached diagnostic must not remesh."));
            Assert.That(preview.LossMesh, Is.SameAs(loss));
            preview.Dispose(); preview.Dispose();
            Assert.That(mesh == null && loss == null, Is.True);
        }

        [TestCase(.25f)]
        [TestCase(.6f)]
        [TestCase(.85f)]
        [TestCase(1f)]
        public void Cancellation_KeepsSourceAndReleasesTemporaryMeshes(float cancelAt)
        {
            var source = Filled(); var before = (ushort[])source.SemanticIds.Clone();
            var meshes = Resources.FindObjectsOfTypeAll<Mesh>().Select(m => m.GetInstanceID()).ToArray();
            Assert.Throws<OperationCanceledException>(() => new VoxelPainterReductionPreview(source, 2, 0, 16, true,
                v => { if (v >= cancelAt) throw new OperationCanceledException(); }));
            CollectionAssert.AreEqual(before, source.SemanticIds);
            CollectionAssert.AreEquivalent(meshes, Resources.FindObjectsOfTypeAll<Mesh>().Select(m => m.GetInstanceID()).ToArray());
        }

        [Test]
        public void Progress_IsMonotonicAndCompletes()
        {
            float previous = 0;
            using var preview = new VoxelPainterReductionPreview(Filled(), 2, 0, 16, true,
                v => { Assert.That(v, Is.GreaterThanOrEqualTo(previous)); previous = v; });
            Assert.That(previous, Is.EqualTo(1));
        }

        [Test]
        public void Target_UsesFamilyInitialMultiplierAndRejectsInvalidNextLevel()
        {
            var profile = ScriptableObject.CreateInstance<VoxelStyleProfile>();
            var family = new VoxelLodSetManifest { baseVoxelSize = .024f, initialVoxelMultiplier = 4, lods = new[] { new VoxelLodEntry() } };
            try
            {
                Assert.That(VoxelProductionFamily.ReductionVoxelSize(family, profile, 1, .096f, out int multiplier), Is.EqualTo(.192f));
                Assert.That(multiplier, Is.EqualTo(8));
                Assert.Throws<ArgumentException>(() => VoxelProductionFamily.ReductionVoxelSize(family, profile, 0, .096f, out _));
                Assert.Throws<ArgumentException>(() => VoxelProductionFamily.ReductionVoxelSize(family, profile, 2, .096f, out _));
                Assert.Throws<InvalidOperationException>(() => VoxelProductionFamily.ReductionVoxelSize(family, profile, 1, .192f, out _));
            }
            finally { UnityEngine.Object.DestroyImmediate(profile); }
        }

        [Test]
        public void Preview_RejectsRgbAndNonCoarserTargets()
        {
            Assert.Throws<ArgumentException>(() => new VoxelPainterReductionPreview(null, 2, 0, 16, false));
            Assert.Throws<ArgumentException>(() => new VoxelPainterReductionPreview(new VoxelGrid(Vector3Int.one, Vector3.zero, 1), 2, 0, 16, false));
            foreach (float size in new[] { 0, 1, float.NaN, float.PositiveInfinity })
                Assert.Throws<ArgumentException>(() => new VoxelPainterReductionPreview(Filled(), size, 0, 16, false));
        }
    }
}
