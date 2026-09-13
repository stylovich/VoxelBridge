using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge.Tests
{
    public class VoxelLodDetailTransitionTests
    {
        private VoxelStyleProfile profile;

        [SetUp]
        public void SetUp()
        {
            profile = ScriptableObject.CreateInstance<VoxelStyleProfile>();
            var data = new SerializedObject(profile);
            data.FindProperty("lodTransitionMode").enumValueIndex = (int)VoxelLodTransitionMode.AdaptiveByModelSize;
            var multipliers = data.FindProperty("lodMultipliers");
            multipliers.arraySize = 5;
            for (int i = 0; i < 5; i++) multipliers.GetArrayElementAtIndex(i).intValue = 1 << i;
            SetArray(data, "lodScreenHeights", new[] { .3f, .18f, .1f, .04f, .02f });
            SetArray(data, "lodDetailScreenHeights", new[] { .175f, .0875f, .04375f, .024f, .0145f });
            data.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(profile.TryValidate(out var error), Is.True, error);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(profile);

        private static void SetArray(SerializedObject data, string name, float[] values)
        {
            var array = data.FindProperty(name);
            array.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++) array.GetArrayElementAtIndex(i).floatValue = values[i];
        }

        [TestCase(2f, .12f, .06f, .03f, .02f, .01f)]
        [TestCase(7f, .24f, .12f, .06f, .03f, .02f)]
        public void Calibration_ApproximatesArtistReferences(float size, float a, float b, float c, float d, float e)
        {
            var expected = new[] { a, b, c, d, e };
            for (int i = 0; i < 5; i++)
                Assert.That(profile.GetLodScreenHeight(i, size), Is.EqualTo(expected[i]).Within(.004f));
        }

        [TestCase(20f)]
        [TestCase(40f)]
        [TestCase(1000f)]
        public void LargeModels_KeepOriginalCurve(float size)
        {
            float scale = Mathf.Min(profile.GetLodTransitionScale(size), .99f / profile.GetLodScreenHeight(0));
            for (int i = 0; i < 5; i++)
                Assert.That(profile.GetLodScreenHeight(i, size), Is.EqualTo(profile.GetLodScreenHeight(i) * scale).Within(1e-6f));
        }

        [TestCase(7f)]
        [TestCase(20f)]
        public void BlendBoundaries_AreContinuous(float size)
        {
            for (int i = 0; i < 5; i++)
                Assert.That(profile.GetLodScreenHeight(i, size - .0001f),
                    Is.EqualTo(profile.GetLodScreenHeight(i, size + .0001f)).Within(.00002f));
        }

        [Test]
        public void SizeSweep_PreservesOrderingRetentionAndMinimumBound()
        {
            var previous = new float[5];
            for (int step = 0; step <= 500; step++)
            {
                float size = Mathf.Pow(10f, -3f + step * .012f);
                float scale = Mathf.Min(profile.GetLodTransitionScale(size), .99f / profile.GetLodScreenHeight(0));
                float last = 1f;
                for (int i = 0; i < 5; i++)
                {
                    float height = profile.GetLodScreenHeight(i, size);
                    Assert.That(float.IsFinite(height), Is.True);
                    Assert.That(height, Is.GreaterThan(0).And.LessThan(last));
                    Assert.That(height, Is.GreaterThanOrEqualTo(previous[i] - 1e-6f));
                    Assert.That(height, Is.GreaterThanOrEqualTo(profile.GetMinimumLodScreenHeight(i) - 1e-6f));
                    Assert.That(height, Is.LessThanOrEqualTo(profile.GetLodScreenHeight(i) * scale + 1e-6f));
                    previous[i] = height;
                    last = height;
                }
            }
        }

        [Test]
        public void CappedBaseCurve_DoesNotAdvanceOtherLevels()
        {
            var data = new SerializedObject(profile);
            data.FindProperty("lodScreenHeights").GetArrayElementAtIndex(0).floatValue = .6f;
            data.ApplyModifiedPropertiesWithoutUndo();
            SizeSweep_PreservesOrderingRetentionAndMinimumBound();
        }

        [Test]
        public void FixedMode_AndEmptyCalibrationKeepLegacyBehavior()
        {
            var data = new SerializedObject(profile);
            data.FindProperty("lodTransitionMode").enumValueIndex = (int)VoxelLodTransitionMode.FixedScreenHeight;
            data.ApplyModifiedPropertiesWithoutUndo();
            for (int i = 0; i < 5; i++)
            {
                Assert.That(profile.GetLodScreenHeight(i, 2), Is.EqualTo(profile.GetLodScreenHeight(i)));
                Assert.That(profile.GetMinimumLodScreenHeight(i), Is.EqualTo(profile.GetLodScreenHeight(i)));
            }
            data.FindProperty("lodTransitionMode").enumValueIndex = (int)VoxelLodTransitionMode.AdaptiveByModelSize;
            data.FindProperty("lodDetailScreenHeights").arraySize = 0;
            data.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(profile.GetLodScreenHeight(0, 2), Is.EqualTo(.3f * Mathf.Sqrt(.5f)).Within(1e-6f));
            Assert.That(profile.GetMinimumLodScreenHeight(0), Is.EqualTo(.3f * .35f).Within(1e-6f));
        }

        [TestCase(0f)]
        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        public void InvalidModelSize_KeepsLegacyFallback(float size)
        {
            Assert.That(profile.GetLodScreenHeight(0, size), Is.EqualTo(.3f));
        }

        [TestCase("missing")]
        [TestCase("unordered")]
        [TestCase("higher")]
        [TestCase("nan")]
        [TestCase("zero")]
        [TestCase("sizes")]
        [TestCase("infiniteSize")]
        public void InvalidCalibration_IsRejected(string scenario)
        {
            var data = new SerializedObject(profile);
            var heights = data.FindProperty("lodDetailScreenHeights");
            switch (scenario)
            {
                case "missing": heights.arraySize = 2; break;
                case "unordered": heights.GetArrayElementAtIndex(1).floatValue = .175f; break;
                case "higher": heights.GetArrayElementAtIndex(0).floatValue = .4f; break;
                case "nan": heights.GetArrayElementAtIndex(0).floatValue = float.NaN; break;
                case "zero": heights.GetArrayElementAtIndex(4).floatValue = 0; break;
                case "sizes": data.FindProperty("lodDetailBlendEndSize").floatValue = 7; break;
                case "infiniteSize": data.FindProperty("lodDetailFullSize").floatValue = float.PositiveInfinity; break;
            }
            data.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(profile.TryValidate(out _), Is.False);
        }

        [Test]
        public void InvalidIndices_PreservePublicContract()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => profile.GetLodScreenHeight(-1, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => profile.GetLodScreenHeight(5, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => profile.GetMinimumLodScreenHeight(5));
        }
    }
}
