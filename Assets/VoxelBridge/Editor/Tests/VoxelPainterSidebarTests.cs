using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelPainterSidebarTests
    {
        [Test]
        public void ColorGrid_SearchKeepsAllowedChoicesAndStableIdOrder()
        {
            var allowed = new[] { new VoxelColorDefinition(54, "Orange 07", Color.red),
                new VoxelColorDefinition(2, "Blue", Color.blue), new VoxelColorDefinition(120, "Orange 08", Color.yellow) };
            Assert.That(VoxelSurfacePainterWindow.FilterColorChoices(allowed, "").Select(c => c.Id), Is.EqualTo(new[] { 2, 54, 120 }));
            Assert.That(VoxelSurfacePainterWindow.FilterColorChoices(allowed, " ORANGE ").Select(c => c.Id), Is.EqualTo(new[] { 54, 120 }));
            Assert.That(VoxelSurfacePainterWindow.FilterColorChoices(allowed, "054").Select(c => c.Id), Is.EqualTo(new[] { 54 }));
            Assert.That(VoxelSurfacePainterWindow.FilterColorChoices(allowed, "255"), Is.Empty);
        }

        [TestCase(1, 1)]
        [TestCase(180, 7)]
        [TestCase(240, 10)]
        public void ColorGrid_ColumnsFitAvailableWidth(float width, int columns)
        {
            Assert.That(VoxelSurfacePainterWindow.ColorGridColumns(width), Is.EqualTo(columns));
        }

        [Test]
        public void Legend_UsesOccupiedSurfaceIdsIncludingHiddenCellsNotColorIds()
        {
            var grid = new VoxelGrid(new Vector3Int(4, 1, 1), Vector3.zero, 1, true);
            grid.Occupied[0] = grid.Occupied[1] = grid.Occupied[2] = true;
            grid.SemanticIds[0] = VoxelSemanticEncoding.Pack(120, 43);
            grid.SemanticIds[1] = VoxelSemanticEncoding.Pack(54, 0);
            grid.SemanticIds[2] = VoxelSemanticEncoding.Pack(22, 43);
            grid.SemanticIds[3] = VoxelSemanticEncoding.Pack(1, 7);
            Assert.That(VoxelSurfacePainterWindow.UsedSurfaceIds(grid), Is.EqualTo(new[] { 0, 43 }));
            grid.SemanticIds[2] = VoxelSemanticEncoding.Pack(22, 13);
            Assert.That(VoxelSurfacePainterWindow.UsedSurfaceIds(grid), Is.EqualTo(new[] { 0, 13, 43 }));
            Assert.Throws<ArgumentException>(() => VoxelSurfacePainterWindow.UsedSurfaceIds(null));
        }
    }
}
