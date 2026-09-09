using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelSourceOrientationTests
    {
        [Test]
        public void AxisMapping_IsExactAndRejectsUnknownModes()
        {
            Assert.That(VoxelSourceOrientation.ToUnity(VoxelSourceAxes.PreserveLocalAxes), Is.EqualTo(Matrix4x4.identity));
            var matrix = VoxelSourceOrientation.ToUnity(VoxelSourceAxes.ZUp);
            Assert.That(matrix.MultiplyPoint3x4(new Vector3(1, 2, 3)), Is.EqualTo(new Vector3(1, 3, -2)));
            Assert.That(matrix.determinant, Is.EqualTo(1));
            Assert.Throws<InvalidDataException>(() => VoxelSourceOrientation.ToUnity((VoxelSourceAxes)99));
            Assert.Throws<InvalidDataException>(() => VoxelSourceOrientation.ReadPlacementAxes(null));
        }

        [Test]
        public void OldMetadataAndManifest_DefaultToPreserveLocalAxes()
        {
            Assert.That(JsonUtility.FromJson<VoxelBridgeMetadata>("{}").sourceAxes, Is.EqualTo(VoxelSourceAxes.PreserveLocalAxes));
            Assert.That(JsonUtility.FromJson<VoxelLodSetManifest>("{}").sourceAxes, Is.EqualTo(VoxelSourceAxes.PreserveLocalAxes));
        }

        [TestCase(VoxelSourceAxes.PreserveLocalAxes, 1)]
        [TestCase(VoxelSourceAxes.ZUp, 1)]
        [TestCase(VoxelSourceAxes.ZUp, -1)]
        public void Placement_PreservesWorldTransformWithScaledParent(VoxelSourceAxes axes, int sign)
        {
            var parent = new GameObject("Orientation test parent");
            try
            {
                var source = new GameObject("Source").transform;
                var destination = new GameObject("Destination").transform;
                source.SetParent(parent.transform, false); destination.SetParent(parent.transform, false);
                parent.transform.SetPositionAndRotation(new Vector3(3, 4, 5), Quaternion.Euler(31, 47, 22));
                parent.transform.localScale = new Vector3(2, -3, .5f);
                source.localPosition = new Vector3(1, 2, 3);
                source.localRotation = Quaternion.Euler(270, 17, 29);
                source.localScale = new Vector3(-2, 3 * sign, 4);
                VoxelSourceOrientation.CopyPlacement(source, destination, axes);
                AssertEquivalent(source.localToWorldMatrix, destination.localToWorldMatrix * VoxelSourceOrientation.ToUnity(axes));
            }
            finally { Object.DestroyImmediate(parent); }
        }

        internal static void AssertEquivalent(Matrix4x4 expected, Matrix4x4 actual)
        {
            for (int i = 0; i < 16; i++) Assert.That(actual[i], Is.EqualTo(expected[i]).Within(.0001f), "Matrix element " + i);
        }

        internal static void AssertBounds(Bounds expected, Bounds actual)
        {
            Assert.That(Vector3.Distance(expected.center, actual.center), Is.LessThan(.00001f));
            Assert.That(Vector3.Distance(expected.size, actual.size), Is.LessThan(.00001f));
        }
    }
}
