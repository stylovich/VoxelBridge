using System;
using NUnit.Framework;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelProductionEditorTests
    {
        [Test]
        public void Status_HealthyFamilyHasNoBanner()
            => Assert.That(VoxelProductionEditor.FormatFamilyStatus(Array.Empty<int>(), Array.Empty<int>()), Is.Empty);

        [Test]
        public void Status_ListsEveryStaleLevelAndPreservesTheReviewWarning()
        {
            string text = VoxelProductionEditor.FormatFamilyStatus(new[] { 1, 2, 3 }, Array.Empty<int>());
            Assert.That(text, Does.Contain("LOD 1, 2, 3"));
            Assert.That(text, Does.Contain("Revisar retoques antes de regenerar"));
            Assert.That(text, Does.Not.Contain("Rebuild pendiente"));
        }

        [Test]
        public void Status_ListsEveryChangedMeshWithoutImplyingRegeneration()
        {
            string text = VoxelProductionEditor.FormatFamilyStatus(Array.Empty<int>(), new[] { 0, 2 });
            Assert.That(text, Does.Contain("Rebuild pendiente: LOD 0, 2"));
            Assert.That(text, Does.Contain("no regenera la fuente"));
            Assert.That(text, Does.Not.Contain("Origen cambiado"));
        }

        [Test]
        public void Status_PreservesBothKindsOfPendingWork()
        {
            string text = VoxelProductionEditor.FormatFamilyStatus(new[] { 1 }, new[] { 0 });
            Assert.That(text, Does.Contain("Origen cambiado: LOD 1"));
            Assert.That(text, Does.Contain("\nRebuild pendiente: LOD 0"));
        }

        [TestCase(0, 3)]
        [TestCase(1, 6)]
        [TestCase(7, 6)]
        public void LevelMenu_OffersRegenerationOnlyForDescendants(int level, int itemCount)
        {
            // Native menu counts include separators. Building a menu must not execute any action.
            var menu = VoxelProductionEditor.CreateLevelMenu("", level, "");
            Assert.That(menu.GetItemCount(), Is.EqualTo(itemCount));
        }

        [Test]
        public void FamilyMenu_KeepsCopyRebuildAndManifestActions()
            => Assert.That(VoxelProductionEditor.CreateFamilyMenu("", "").GetItemCount(), Is.EqualTo(5));

        [Test]
        public void CreateMenu_KeepsBothDerivationModesAndRemainingLevels()
            => Assert.That(VoxelProductionEditor.CreateNextLevelMenu("", 1, 4).GetItemCount(), Is.EqualTo(4));
    }
}
