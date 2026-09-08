using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelSurfacePreviewTests
    {
        [Test]
        public void PreviewReadsCurrentSurfaceValuesWithoutChangingPaletteOrRequiringLut()
        {
            var palette = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
            Material material = CreateMaterial();
            try
            {
                var surface = new VoxelSurfaceDefinition(5, "Test", VoxelSurfaceRenderClass.Opaque, .8f, .37f, .6f, .9f);
                palette.MutableEntries[5] = surface;
                string before = EditorJsonUtility.ToJson(palette);
                Color color = new(.3f, .5f, .7f, .2f);
                VoxelSurfacePreviewWindow.ApplySurface(material, surface, color);
                Assert.That(material.GetFloat("_Metallic"), Is.EqualTo(.8f));
                Assert.That(material.GetFloat("_Smoothness"), Is.EqualTo(.37f));
                Assert.That(material.GetFloat("_AORemapMin"), Is.EqualTo(.9f));
                Assert.That(material.GetFloat("_AORemapMax"), Is.EqualTo(.9f));
                Color baseColor = material.GetColor("_BaseColor");
                Assert.That(baseColor.r, Is.EqualTo(color.r).Within(.0001f));
                Assert.That(baseColor.a, Is.EqualTo(1));
                Assert.That(material.GetColor("_EmissiveColor").r, Is.EqualTo(color.linear.r * .6f).Within(.0001f));
                Assert.That(EditorJsonUtility.ToJson(palette), Is.EqualTo(before));
                Assert.That(palette.GeneratedLut, Is.Null);
            }
            finally { UnityEngine.Object.DestroyImmediate(material); UnityEngine.Object.DestroyImmediate(palette); }
        }

        [Test]
        public void PreviewRejectsInvalidInputsBeforeMutatingMaterial()
        {
            Material material = CreateMaterial();
            try
            {
                var invalid = new VoxelSurfaceDefinition(1, "Invalid", VoxelSurfaceRenderClass.Opaque, float.NaN, 0, 0, 1);
                var valid = new VoxelSurfaceDefinition(1, "Valid", VoxelSurfaceRenderClass.Opaque, 0, 0, 0, 1);
                string before = EditorJsonUtility.ToJson(material);
                Assert.Throws<ArgumentException>(() => VoxelSurfacePreviewWindow.ApplySurface(material, invalid, Color.white));
                Assert.Throws<ArgumentException>(() => VoxelSurfacePreviewWindow.ApplySurface(material, valid, new Color(2, 0, 0)));
                Assert.That(EditorJsonUtility.ToJson(material), Is.EqualTo(before));
                material.hideFlags = HideFlags.None;
                Assert.Throws<ArgumentException>(() => VoxelSurfacePreviewWindow.ApplySurface(material, valid, Color.white));
            }
            finally { UnityEngine.Object.DestroyImmediate(material); }
        }

        [Test]
        public void PreviewFraming_FitsSphereAndRotatedCubeWithMarginInNarrowWindows()
        {
            foreach (float aspect in new[] { .5f, 1f, 2f })
            {
                var bounds = new Bounds(Vector3.zero, Vector3.one * 2);
                float sphere = VoxelSurfacePreviewWindow.FramingDistance(bounds, true, aspect);
                float cube = VoxelSurfacePreviewWindow.FramingDistance(bounds, false, aspect);
                float halfAngle = Mathf.Atan(Mathf.Tan(17.5f * Mathf.Deg2Rad) * Mathf.Min(1, aspect));
                Assert.That(sphere * Mathf.Sin(halfAngle), Is.GreaterThan(1.1f));
                Assert.That(cube * Mathf.Sin(halfAngle), Is.GreaterThan(bounds.extents.magnitude * 1.1f));
            }
        }

        [Test]
        public void PreviewResourcesAreTemporaryAndReleasedWithoutDestroyingSharedPrimitives()
        {
            var window = ScriptableObject.CreateInstance<VoxelSurfacePreviewWindow>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Type type = typeof(VoxelSurfacePreviewWindow);
            try
            {
                type.GetMethod("EnsurePreview", flags).Invoke(window, null);
                var material = (Material)type.GetField("material", flags).GetValue(window);
                var mesh = (Mesh)type.GetField("sphere", flags).GetValue(window);
                var preview = (PreviewRenderUtility)type.GetField("preview", flags).GetValue(window);
                Assert.That(material.hideFlags, Is.EqualTo(HideFlags.HideAndDontSave));
                Assert.That(preview.lights[0].intensity, Is.EqualTo(4));
                Assert.That(preview.lights[1].intensity, Is.EqualTo(2));
                type.GetMethod("ReleasePreview", flags).Invoke(window, null);
                Assert.That(material == null, Is.True);
                Assert.That(mesh != null, Is.True);
                Assert.That(type.GetField("preview", flags).GetValue(window), Is.Null);
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        private static Material CreateMaterial()
        {
            Shader shader = Shader.Find("HDRP/Lit");
            Assert.That(shader, Is.Not.Null, "HDRP/Lit must be installed for material preview tests.");
            return new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }
    }
}
