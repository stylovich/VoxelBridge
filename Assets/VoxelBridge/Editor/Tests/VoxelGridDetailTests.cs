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
            float anchor = 0, Matrix4x4? objectToWorld = null, float? spanY = null,
            float distanceStart = 12, float distanceStep = 20, bool levelOnly = false,
            float profile = 0, float bevelWidth = .06f, float jointDepth = .025f, bool patternOnly = false,
            float pom = 0, float pomMaxDepth = .003f, bool pomTrace = false, Vector3? view = null,
            float variation = 0, Vector3? plane = null, Vector3? cellV = null)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/VoxelBridge/Editor/Tests/VoxelGridDetailProbe.shader");
            Assert.That(shader, Is.Not.Null);
            Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
            var material = new Material(shader);
            var rt = RenderTexture.GetTemporary(64, 64, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            var camera = Shader.GetGlobalVector("_WorldSpaceCameraPos");
            var ortho = Shader.GetGlobalVector("unity_OrthoParams");
            Texture2D readable = null;
            try
            {
                Shader.SetGlobalVector("_WorldSpaceCameraPos", Vector4.zero);
                Shader.SetGlobalVector("unity_OrthoParams", Vector4.zero);
                material.SetFloat("_TestEnabled", enabled); material.SetFloat("_TestDistance", distance);
                material.SetFloat("_TestOffset", offset); material.SetFloat("_TestSpan", span);
                material.SetFloat("_TestSpanY", spanY ?? span);
                material.SetFloat("_TestMultiscale", multiscale); material.SetFloat("_TestTargetPixels", targetPixels);
                material.SetFloat("_TestMaxScaleLevels", maxLevels);
                material.SetFloat("_TestAnchor", anchor);
                material.SetFloat("_TestDistanceStart", distanceStart); material.SetFloat("_TestDistanceStep", distanceStep);
                material.SetFloat("_TestLevelOnly", levelOnly ? 1 : 0);
                material.SetFloat("_TestProfile", profile); material.SetFloat("_TestBevelWidth", bevelWidth);
                material.SetFloat("_TestJointDepth", jointDepth); material.SetFloat("_TestPatternOnly", patternOnly ? 1 : 0);
                material.SetFloat("_TestPom", pom); material.SetFloat("_TestPomMaxDepth", pomMaxDepth);
                material.SetFloat("_TestPomTrace", pomTrace ? 1 : 0); material.SetVector("_TestView", view ?? Vector3.forward);
                material.SetFloat("_TestVariation", variation); material.SetVector("_TestPlane", plane ?? Vector3.zero);
                material.SetVector("_TestCellV", cellV ?? Vector3.up);
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
                Shader.SetGlobalVector("unity_OrthoParams", ortho);
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

        [TestCase(0f, 0f)]
        [TestCase(12f, 0f)]
        [TestCase(22f, .5f)]
        [TestCase(32f, 1f)]
        [TestCase(52f, 2f)]
        [TestCase(92f, 4f)]
        [TestCase(1000f, 4f)]
        public void DistanceMode_UsesMetresAndClampsAtSizeCap(float distance, float expectedLevel)
        {
            foreach (var pixel in Render(distance: distance, levelOnly: true))
                Assert.That(pixel.r, Is.EqualTo(expectedLevel).Within(.0001f));
        }

        [Test]
        public void DistanceMode_ClampsDegenerateAuthoringValues()
        {
            foreach (var pixel in Render(distance: 1, distanceStart: -3, distanceStep: 0, maxLevels: 20, levelOnly: true))
                Assert.That(pixel.r, Is.EqualTo(8));
            foreach (var pixel in Render(distance: 100, maxLevels: -1, levelOnly: true))
                Assert.That(pixel.r, Is.Zero);
        }

        [Test]
        public void DistanceMode_NearRangeIgnoresPixelTargetAndDoesNotGrow()
        {
            var expected = Render(distance: 1, span: .125f);
            var actual = Render(distance: 10, span: .125f, multiscale: 2, targetPixels: 64);
            for (int i = 0; i < expected.Length; i++)
                Assert.That(Vector4.Distance(expected[i], actual[i]), Is.LessThan(.001f));
        }

        [TestCase(0f)]
        [TestCase(80f)]
        [TestCase(90f)]
        public void DistanceMode_UsesSameScaleOnRotatedSurfaces(float angle)
        {
            var expected = Render(distance: 32, multiscale: 2, anchor: 1);
            var actual = Render(distance: 32, multiscale: 2, anchor: 1,
                objectToWorld: Matrix4x4.Rotate(Quaternion.Euler(angle, 0, 0)));
            for (int i = 0; i < expected.Length; i++)
                Assert.That(Vector4.Distance(expected[i], actual[i]), Is.LessThan(.002f));
        }

        [Test]
        public void DistanceMode_FiltersUnresolvedGridInsteadOfGrowingIt()
        {
            foreach (var pixel in Render(distance: 1, span: 2, multiscale: 2))
                Assert.That(Vector4.Distance(pixel, new Color(.5f, .5f, 1, .6f)), Is.LessThan(.001f));
            Assert.That(Render(distance: 1, span: 2, multiscale: 1).Any(p => p.a < .59f), Is.True);
        }

        [TestCase(12f)]
        [TestCase(32f)]
        [TestCase(52f)]
        [TestCase(92f)]
        public void DistanceMode_HasContinuousTransitions(float distance)
        {
            var a = Render(distance: distance - .0001f, multiscale: 2);
            var b = Render(distance: distance + .0001f, multiscale: 2);
            for (int i = 0; i < a.Length; i++)
                Assert.That(Vector4.Distance(a[i], b[i]), Is.LessThan(.002f));
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
                Assert.That(material.GetFloat("_GridDistanceStart"), Is.EqualTo(12));
                Assert.That(material.GetFloat("_GridDistanceStep"), Is.EqualTo(20));
                Assert.That(material.GetFloat("_GridProfile"), Is.Zero);
                Assert.That(material.GetFloat("_GridBevelWidth"), Is.EqualTo(.06f));
                Assert.That(material.GetFloat("_GridJointDepth"), Is.EqualTo(.025f));
                Assert.That(material.GetFloat("_GridPomEnabled"), Is.Zero);
                Assert.That(material.GetFloat("_GridPomMaxDepth"), Is.EqualTo(.003f));
                Assert.That(material.HasProperty("_PaletteColor"), Is.True);
                Assert.That(material.HasProperty("_PaletteSurface"), Is.True);
                Assert.That(material.HasProperty("_EmissionIntensity"), Is.True);
            }
            finally { Object.DestroyImmediate(material); }
        }

        [Test]
        public void Bevel_HasFlatFaceFlatRecessedJointAndRoundedCorners()
        {
            var p = Render(span: 1, patternOnly: true);
            Assert.That(Vector4.Distance(p[32 + 32 * 64], Color.clear), Is.LessThan(.001f), "Flat top.");
            var joint = p[32 * 64];
            Assert.That(joint.r, Is.Zero.Within(.001f)); Assert.That(joint.g, Is.Zero.Within(.001f));
            Assert.That(joint.a, Is.EqualTo(-.025f).Within(.001f));
            var edge = p[4 + 32 * 64];
            Assert.That(edge.r, Is.GreaterThan(.1f)); Assert.That(edge.g, Is.Zero.Within(.001f));
            var corner = p[5 + 5 * 64];
            Assert.That(corner.r, Is.GreaterThan(.1f)); Assert.That(corner.g, Is.EqualTo(corner.r).Within(.001f));
            Assert.That(corner.a, Is.LessThan(edge.a), "Rounded corner is lower than the straight edge at this sample.");
        }

        [Test]
        public void Bevel_DepthAndWidthAreIndependentControls()
        {
            var a = Render(span: 1, patternOnly: true);
            var deep = Render(span: 1, patternOnly: true, jointDepth: .05f);
            var wide = Render(span: 1, patternOnly: true, bevelWidth: .12f);
            for (int i = 0; i < a.Length; i++)
            {
                Assert.That(deep[i].r, Is.EqualTo(a[i].r * 2).Within(.001f));
                Assert.That(deep[i].a, Is.EqualTo(a[i].a * 2).Within(.001f));
            }
            Assert.That(a[7 + 32 * 64].r, Is.Zero.Within(.001f));
            Assert.That(wide[7 + 32 * 64].r, Is.GreaterThan(.1f));
        }

        [TestCase(0f, 1f, .25f)]
        [TestCase(1f, 0f, .25f)]
        [TestCase(1f, 1f, 2f)]
        public void Bevel_DisabledFlatOrUnresolvedPreservesBase(float enabled, float depth, float span)
        {
            foreach (var pixel in Render(enabled: enabled, profile: 1, jointDepth: depth * .025f, span: span))
                Assert.That(Vector4.Distance(pixel, new Color(.5f, .5f, 1, .6f)), Is.LessThan(.001f));
        }

        [TestCase(0f)]
        [TestCase(90f)]
        public void Bevel_FollowsFamilyFrameAndWholeCellOffsets(float angle)
        {
            var a = Render(profile: 1, anchor: 1);
            var b = Render(profile: 1, anchor: 1, offset: .03125f,
                objectToWorld: Matrix4x4.TRS(new Vector3(.7f, 0, 0), Quaternion.Euler(angle, angle, 0), Vector3.one));
            for (int i = 0; i < a.Length; i++) Assert.That(Vector4.Distance(a[i], b[i]), Is.LessThan(.003f));
        }

        [TestCase(12f)]
        [TestCase(32f)]
        public void Bevel_DistanceBlendIsContinuous(float distance)
        {
            var a = Render(profile: 1, multiscale: 2, distance: distance - .0001f);
            var b = Render(profile: 1, multiscale: 2, distance: distance + .0001f);
            for (int i = 0; i < a.Length; i++) Assert.That(Vector4.Distance(a[i], b[i]), Is.LessThan(.002f));
        }

        [Test]
        public void Bevel_ResolvedPatternIsContinuousDuringSmallMotion()
        {
            var previous = Render(profile: 1, span: .03125f);
            for (int frame = 1; frame <= 8; frame++)
            {
                var current = Render(profile: 1, span: .03125f, offset: frame * .000001f);
                Assert.That(previous.Zip(current, (a, b) => Vector4.Distance(a, b)).Max(), Is.LessThan(.003f));
                previous = current;
            }
        }

        [Test]
        public void Pom_FrontViewHasDepthWithoutLateralShiftAndFaceStaysFlat()
        {
            var pixels = Render(span: 1, pomTrace: true);
            foreach (var p in pixels)
            {
                Assert.That(p.r, Is.Zero.Within(.00001f)); Assert.That(p.g, Is.Zero.Within(.00001f));
                Assert.That(p.b, Is.InRange(0f, 1f));
            }
            Assert.That(pixels[32 + 32 * 64].b, Is.Zero);
            Assert.That(pixels[32 * 64].b, Is.GreaterThan(.99f));
        }

        [Test]
        public void Pom_ObliqueRayMovesIntoJointAndReversesWithView()
        {
            var left = Render(span: 1, pomTrace: true, view: new Vector3(1, 0, 1));
            var right = Render(span: 1, pomTrace: true, view: new Vector3(-1, 0, 1));
            Assert.That(left.Any(p => p.r < -.005f), Is.True);
            Assert.That(right.Any(p => p.r > .005f), Is.True);
            Assert.That(left.All(p => p.r <= .00001f && Mathf.Abs(p.g) < .00001f), Is.True);
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
                Assert.That(left[x + y * 64].r, Is.EqualTo(-right[63 - x + y * 64].r).Within(.0001f));
        }

        [Test]
        public void Pom_RayShiftIsBoundedAndDegenerateCasesStayFinite()
        {
            var pixels = Render(span: 1, pomTrace: true, jointDepth: .25f, view: new Vector3(1, 0, .13f));
            Assert.That(pixels.All(p => float.IsFinite(p.r) && Mathf.Abs(p.r) <= .2001f), Is.True);
            foreach (var p in Render(span: 1, pomTrace: true, jointDepth: 0)) Assert.That(p.b, Is.Zero);
            foreach (var p in Render(span: 1, pomTrace: true, view: Vector3.right)) Assert.That(p.b, Is.Zero);
        }

        [TestCase(0f, 1f, .003f)]
        [TestCase(1f, 1f, 0f)]
        [TestCase(1f, 10f, .003f)]
        public void Pom_DisabledCappedOrDistantPreservesBevel(float enabled, float distance, float cap)
        {
            var a = Render(profile: 1, distance: distance, multiscale: 2);
            var b = Render(profile: 1, distance: distance, multiscale: 2, pom: enabled, pomMaxDepth: cap);
            for (int i = 0; i < a.Length; i++) Assert.That(Vector4.Distance(a[i], b[i]), Is.LessThan(.001f));
        }

        [Test]
        public void CellVariation_HasFlatRandomTopsAndOneCommonJointFloor()
        {
            var pixels = Render(span: 4, offset: -.03125f, patternOnly: true, variation: .08f);
            var heights = new System.Collections.Generic.List<float>();
            for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++)
            {
                var top = pixels[(x * 16 + 8) + (y * 16 + 8) * 64];
                Assert.That(top.r, Is.Zero.Within(.001f)); Assert.That(top.g, Is.Zero.Within(.001f));
                Assert.That(top.a, Is.InRange(-.0801f, .0001f)); heights.Add(top.a);
                var joint = pixels[x * 16 + (y * 16 + 8) * 64];
                Assert.That(joint.a, Is.EqualTo(-.105f).Within(.001f));
            }
            Assert.That(heights.Distinct().Count(), Is.GreaterThan(10));
        }

        [Test]
        public void CellVariation_UsesSameCellIdentityAcrossFaceOrientations()
        {
            var front = Render(span: 4, patternOnly: true, variation: .08f);
            var top = Render(span: 4, patternOnly: true, variation: .08f, cellV: Vector3.forward);
            for (int x = 0; x < 4; x++)
                Assert.That(front[x * 16 + 8 + 8 * 64].a, Is.EqualTo(top[x * 16 + 8 + 8 * 64].a).Within(.001f));
        }

        [TestCase(0f)]
        [TestCase(90f)]
        public void CellVariation_FollowsFamilyAndIsStableOnNegativeCoordinates(float angle)
        {
            var a = Render(profile: 1, anchor: 1, variation: .08f, offset: -.25f);
            var b = Render(profile: 1, anchor: 1, variation: .08f, offset: -.25f,
                objectToWorld: Matrix4x4.TRS(new Vector3(.1f, .2f, .1f), Quaternion.Euler(angle, angle, 0), Vector3.one));
            for (int i = 0; i < a.Length; i++) Assert.That(Vector4.Distance(a[i], b[i]), Is.LessThan(.003f));
        }

        [Test]
        public void CellVariation_FadesOutWithoutReseedingCoarseGrid()
        {
            var a = Render(profile: 1, multiscale: 2, distance: 40);
            var b = Render(profile: 1, multiscale: 2, distance: 40, variation: .08f);
            for (int i = 0; i < a.Length; i++) Assert.That(Vector4.Distance(a[i], b[i]), Is.LessThan(.001f));
        }

        [Test]
        public void CellVariation_PomIntersectsTheVariedHeightField()
        {
            var shape = Render(span: 4, patternOnly: true, variation: .08f);
            var hits = Render(span: 4, pomTrace: true, variation: .08f);
            for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++)
            {
                int i = x * 16 + 8 + (y * 16 + 8) * 64;
                Assert.That(hits[i].b, Is.EqualTo(-shape[i].a / .105f).Within(.004f));
            }
        }
    }
}
