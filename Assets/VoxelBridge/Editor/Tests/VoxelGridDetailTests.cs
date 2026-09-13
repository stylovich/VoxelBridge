using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge.Tests
{
    public class VoxelGridDetailTests
    {
        private static Color[] Render(float enabled = 1, float distance = 1, float offset = 0, float span = .25f,
            float multiscale = 0, float targetPixels = 12, float maxLevels = 4,
            float anchor = 0, Matrix4x4? objectToWorld = null, float? spanY = null)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/VoxelBridge/Editor/Tests/VoxelGridDetailProbe.shader");
            Assert.That(shader, Is.Not.Null);
            Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
            var material = new Material(shader);
            var rt = RenderTexture.GetTemporary(64, 64, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            var camera = Shader.GetGlobalVector("_WorldSpaceCameraPos");
            Texture2D readable = null;
            try
            {
                Shader.SetGlobalVector("_WorldSpaceCameraPos", Vector4.zero);
                material.SetFloat("_TestEnabled", enabled); material.SetFloat("_TestDistance", distance);
                material.SetFloat("_TestOffset", offset); material.SetFloat("_TestSpan", span);
                material.SetFloat("_TestSpanY", spanY ?? span);
                material.SetFloat("_TestMultiscale", multiscale); material.SetFloat("_TestTargetPixels", targetPixels);
                material.SetFloat("_TestMaxScaleLevels", maxLevels);
                material.SetFloat("_TestAnchor", anchor);
                material.SetMatrix("_TestObjectToWorld", objectToWorld ?? Matrix4x4.identity);
                material.SetMatrix("_TestWorldToObject", (objectToWorld ?? Matrix4x4.identity).inverse);
                Graphics.Blit(Texture2D.whiteTexture, rt, material);
                RenderTexture.active = rt;
                readable = new Texture2D(64, 64, TextureFormat.RGBAFloat, false, true);
                readable.ReadPixels(new Rect(0, 0, 64, 64), 0, 0); readable.Apply();
                return readable.GetPixels();
            }
            finally
            {
                Shader.SetGlobalVector("_WorldSpaceCameraPos", camera);
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                Object.DestroyImmediate(material);
                if (readable != null) Object.DestroyImmediate(readable);
            }
        }

        [TestCase(0, 1, .25f)]
        [TestCase(1, 9, .25f)]
        [TestCase(1, 1, 2f)]
        public void DisabledDistantOrUnresolvedGrid_PreservesBase(float enabled, float distance, float span)
        {
            foreach (var c in Render(enabled, distance, 0, span))
            {
                Assert.That(c.r, Is.EqualTo(.5f).Within(.001f));
                Assert.That(c.g, Is.EqualTo(.5f).Within(.001f));
                Assert.That(c.b, Is.EqualTo(1).Within(.001f));
                Assert.That(c.a, Is.EqualTo(.6f).Within(.001f));
            }
        }

        [Test]
        public void CloseGrid_ChangesNormalAndRoughness()
        {
            var pixels = Render();
            Assert.That(pixels.Any(c => Mathf.Abs(c.r - .5f) > .01f), Is.True);
            Assert.That(pixels.Any(c => c.a < .59f), Is.True);
            Assert.That(pixels.All(c => float.IsFinite(c.r) && float.IsFinite(c.g) && float.IsFinite(c.b) && float.IsFinite(c.a)), Is.True);
        }

        [Test]
        public void WholeCellOffset_PreservesGridPhase()
        {
            var a = Render(); var b = Render(offset: .03125f);
            for (int i = 0; i < a.Length; i++)
                Assert.That(Vector4.Distance(a[i], b[i]), Is.LessThan(.001f));
        }

        [Test]
        public void Multiscale_KeepsDistantGridWhereFixedIsInvisible()
        {
            Assert.That(Render(distance: 30, span: 2).All(c => Mathf.Abs(c.a - .6f) < .001f), Is.True);
            var pixels = Render(distance: 30, span: 2, multiscale: 1);
            Assert.That(pixels.Any(c => c.a < .59f), Is.True);
            Assert.That(pixels.Any(c => Mathf.Abs(c.r - .5f) > .01f), Is.True);
        }

        [Test]
        public void Multiscale_ApproachesFixedWhenFineGridIsResolved()
        {
            var a = Render(span: .125f);
            var b = Render(span: .125f, multiscale: 1);
            for (int i = 0; i < a.Length; i++) Assert.That(Vector4.Distance(a[i], b[i]), Is.LessThan(.001f));
        }

        [TestCase(1f)]
        [TestCase(2f)]
        [TestCase(4f)]
        public void Multiscale_HasContinuousBinaryTransitions(float octave)
        {
            // Base footprint is .125 cells/pixel. Changing target size crosses an exact octave
            // without changing the sampled positions, isolating continuity of the blend.
            var a = Render(multiscale: 1, targetPixels: 8 * octave * .9999f);
            var b = Render(multiscale: 1, targetPixels: 8 * octave * 1.0001f);
            for (int i = 0; i < a.Length; i++) Assert.That(Vector4.Distance(a[i], b[i]), Is.LessThan(.002f));
        }

        [Test]
        public void Multiscale_CoarsestCellTranslationPreservesPhase()
        {
            var a = Render(span: 2, multiscale: 1);
            var b = Render(span: 2, multiscale: 1, offset: .5f);
            for (int i = 0; i < a.Length; i++) Assert.That(Vector4.Distance(a[i], b[i]), Is.LessThan(.001f));
        }

        [TestCase(0, 2, 4)]
        [TestCase(1, 64, 4)]
        [TestCase(1, 2, 0)]
        public void Multiscale_DisabledOrBeyondCapRemainsFlat(float enabled, float span, float maxLevels)
        {
            foreach (var c in Render(enabled: enabled, span: span, multiscale: 1, maxLevels: maxLevels))
                Assert.That(Vector4.Distance(c, new Color(.5f, .5f, 1, .6f)), Is.LessThan(.001f));
        }

        [TestCase(0f)]
        [TestCase(37f)]
        [TestCase(90f)]
        public void FamilyAnchor_FollowsTranslationAndRotation(float angle)
        {
            var a = Render(anchor: 1, multiscale: 1);
            var transform = Matrix4x4.TRS(new Vector3(8.017f, 3.009f, -2.12f), Quaternion.Euler(angle, angle, angle), Vector3.one);
            var b = Render(anchor: 1, multiscale: 1, objectToWorld: transform);
            for (int i = 0; i < a.Length; i++) Assert.That(Vector4.Distance(a[i], b[i]), Is.LessThan(.002f));
        }

        [Test]
        public void FamilyAnchor_KeepsPhysicalCellSizeUnderScaling()
        {
            var a = Render(anchor: 1, multiscale: 1);
            var b = Render(anchor: 1, multiscale: 1, span: .125f, objectToWorld: Matrix4x4.Scale(Vector3.one * 2));
            for (int i = 0; i < a.Length; i++) Assert.That(Vector4.Distance(a[i], b[i]), Is.LessThan(.001f));
        }

        [Test]
        public void FamilyAnchor_KeepsPhysicalCellSizeUnderNonuniformScaleAndRotation()
        {
            var a = Render(anchor: 1, multiscale: 1);
            var transform = Matrix4x4.TRS(new Vector3(3.017f, 2, 4), Quaternion.Euler(23, 32, 17), new Vector3(2, 3, 1));
            var b = Render(anchor: 1, multiscale: 1, span: .125f, spanY: .25f / 3, objectToWorld: transform);
            for (int i = 0; i < a.Length; i++) Assert.That(Vector4.Distance(a[i], b[i]), Is.LessThan(.002f));
        }

        [Test]
        public void WorldAnchor_DoesNotFollowTranslatedObject()
        {
            var a = Render(multiscale: 1);
            var b = Render(multiscale: 1, objectToWorld: Matrix4x4.Translate(new Vector3(.017f, 0, 0)));
            Assert.That(a.Zip(b, (x, y) => Vector4.Distance(x, y)).Max(), Is.GreaterThan(.02f));
        }

        [Test]
        public void FamilyAnchorValidation_RejectsMovedChunksAndStaticBatching()
        {
            var root = new GameObject("Family frame test");
            try
            {
                var group = root.AddComponent<LODGroup>();
                var chunk = new GameObject("Chunk"); chunk.transform.SetParent(root.transform, false);
                var renderer = chunk.AddComponent<MeshRenderer>();
                group.SetLODs(new[] { new LOD(.5f, new Renderer[] { renderer }) });
                Assert.That(VoxelGridAnchorValidation.Validate(group), Is.Null);
                chunk.transform.localPosition = Vector3.right;
                Assert.That(VoxelGridAnchorValidation.Validate(group), Does.Contain("coordenadas"));
                chunk.transform.localPosition = Vector3.zero;
                GameObjectUtility.SetStaticEditorFlags(chunk, StaticEditorFlags.BatchingStatic);
                Assert.That(VoxelGridAnchorValidation.Validate(group), Does.Contain("Batching Static"));
                GameObjectUtility.SetStaticEditorFlags(chunk, 0);
                root.transform.localScale = new Vector3(-1, 1, 1);
                Assert.That(VoxelGridAnchorValidation.Validate(group), Does.Contain("reflejo"));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [TestCase("VoxelGridPrototype", "Forward", false)]
        [TestCase("VoxelGridPrototype", "Forward", true)]
        [TestCase("VoxelGridPrototype", "GBuffer", false)]
        [TestCase("VoxelGridPrototype", "GBuffer", true)]
        [TestCase("VoxelWorldOpaque", "Forward", false)]
        [TestCase("VoxelWorldOpaque", "Forward", true)]
        [TestCase("VoxelWorldOpaque", "GBuffer", false)]
        [TestCase("VoxelWorldOpaque", "GBuffer", true)]
        public void GridShaders_CompileRasterVariants(string shaderName, string passName, bool dots)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>($"Assets/VoxelBridge/Shaders/{shaderName}.shadergraph");
            Assert.That(shader, Is.Not.Null);
            var sub = ShaderUtil.GetShaderData(shader).GetSubshader(0);
            var pass = Enumerable.Range(0, sub.PassCount).Select(sub.GetPass).First(p => p.Name == passName);
            var keywords = new[] { "PUNCTUAL_SHADOW_MEDIUM", "DIRECTIONAL_SHADOW_MEDIUM", "AREA_SHADOW_MEDIUM" };
            if (dots) keywords = keywords.Concat(new[] { "DOTS_INSTANCING_ON" }).ToArray();
            var result = pass.CompileVariant(UnityEditor.Rendering.ShaderType.Fragment, keywords,
                UnityEditor.Rendering.ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64);
            Assert.That(result.Success, Is.True, string.Join("\n", result.Messages.Select(m => m.message)));
        }

        [Test]
        public void Production_DefaultsPreserveExistingMaterialsAndOfferLocalMultiscaleGrid()
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(VoxelProductionExporter.ShaderPath);
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            try
            {
                Assert.That(material.GetFloat("_GridEnabled"), Is.Zero);
                Assert.That(material.GetFloat("_GridCellSize"), Is.EqualTo(.0625f));
                Assert.That(material.GetFloat("_GridAnchor"), Is.EqualTo(1));
                Assert.That(material.GetFloat("_GridMultiscale"), Is.EqualTo(1));
                Assert.That(material.GetFloat("_GridTargetPixels"), Is.EqualTo(12));
                Assert.That(material.GetFloat("_GridMaxScaleLevels"), Is.EqualTo(4));
                Assert.That(material.HasProperty("_PaletteColor"), Is.True);
                Assert.That(material.HasProperty("_PaletteSurface"), Is.True);
                Assert.That(material.HasProperty("_EmissionIntensity"), Is.True);
            }
            finally { Object.DestroyImmediate(material); }
        }
    }
}
