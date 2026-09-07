using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelSelectionOverlayTests
    {
        [Test]
        public void FaceOverlay_PreservesAll900CellsAndOmitsSharedFaces()
        {
            var grid = new VoxelGrid(new Vector3Int(30, 30, 1), Vector3.zero, 1, true);
            Array.Fill(grid.Occupied, true);
            var selected = Enumerable.Range(0, 900).ToArray();
            var meshes = VoxelSelectionOverlay.Build(grid, selected);
            try
            {
                Assert.That(meshes.Sum(m => m.vertexCount), Is.EqualTo((900 * 2 + 120) * 4));
                var fronts = new HashSet<Vector2Int>();
                foreach (var mesh in meshes)
                {
                    var positions = mesh.vertices;
                    for (int i = 0; i < positions.Length; i += 4)
                    {
                        if (positions[i].z >= 0 || positions[i + 1].z >= 0 || positions[i + 2].z >= 0 || positions[i + 3].z >= 0) continue;
                        Vector3 center = (positions[i] + positions[i + 1] + positions[i + 2] + positions[i + 3]) * .25f;
                        fronts.Add(new Vector2Int(Mathf.FloorToInt(center.x), Mathf.FloorToInt(center.y)));
                    }
                }
                Assert.That(fronts.Count, Is.EqualTo(900), "Every selected front face must be represented, including selections above 512 cells.");
                Assert.That(grid.Occupied.All(value => value), Is.True);
                Assert.That(grid.SemanticIds.All(value => value == 0), Is.True);
            }
            finally { VoxelSelectionOverlay.Destroy(meshes); }
        }

        [Test]
        public void Overlay_ChunksLargeSelectionsWithoutDroppingFaces()
        {
            var grid = new VoxelGrid(new Vector3Int(6000, 1, 1), Vector3.zero, 1, true);
            var selected = Enumerable.Range(0, 3000).Select(i => i * 2).ToArray();
            foreach (int cell in selected) grid.Occupied[cell] = true;
            var meshes = VoxelSelectionOverlay.Build(grid, selected);
            try
            {
                Assert.That(meshes.Count, Is.EqualTo(2));
                Assert.That(meshes.Sum(m => m.vertexCount), Is.EqualTo(3000 * 6 * 4));
                Assert.That(meshes.All(m => m.vertexCount < 65536), Is.True);
            }
            finally { VoxelSelectionOverlay.Destroy(meshes); }
        }

        [Test]
        public void Overlay_RejectsUnoccupiedCellsAndAcceptsEmptySelection()
        {
            var grid = new VoxelGrid(Vector3Int.one, Vector3.zero, 1, true);
            Assert.Throws<ArgumentException>(() => VoxelSelectionOverlay.Build(grid, new[] { 0 }));
            Assert.Throws<ArgumentException>(() => VoxelSelectionOverlay.Build(grid, new[] { -1 }));
            Assert.That(VoxelSelectionOverlay.Build(grid, Array.Empty<int>()), Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void HdrpOverlay_ShowsCompleteFaceGridAndRespectsOccluders(bool occlude)
        {
            var grid = new VoxelGrid(new Vector3Int(30, 30, 1), Vector3.zero, 1, true);
            Array.Fill(grid.Occupied, true);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(VoxelSelectionOverlay.ShaderPath);
            Assert.That(shader, Is.Not.Null); Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
            var overlay = new Material(shader);
            overlay.SetColor("_Color", new Color(1, .8f, .05f, 1));
            var baseMaterial = new Material(Shader.Find("HDRP/Unlit"));
            baseMaterial.SetColor("_UnlitColor", new Color(.08f, .08f, .08f, 1));
            var meshes = VoxelSelectionOverlay.Build(grid, Enumerable.Range(0, 900).ToArray());
            Mesh model = VoxelSemanticMesher.Build(grid, false);
            var preview = new PreviewRenderUtility();
            Texture2D capture = null;
            try
            {
                var camera = preview.camera;
                camera.orthographic = true; camera.orthographicSize = 16; camera.aspect = 1;
                camera.nearClipPlane = .1f; camera.farClipPlane = 100;
                camera.transform.SetPositionAndRotation(new Vector3(15, 15, -30), Quaternion.identity);
                for (int frame = 0; frame < 2; frame++)
                {
                    if (capture != null) UnityEngine.Object.DestroyImmediate(capture);
                    preview.BeginStaticPreview(new Rect(0, 0, 640, 640));
                    preview.DrawMesh(model, Matrix4x4.identity, baseMaterial, 0);
                    if (occlude) preview.DrawMesh(model, Matrix4x4.Translate(new Vector3(0, 0, -2)), baseMaterial, 0);
                    foreach (var mesh in meshes) preview.DrawMesh(mesh, Matrix4x4.identity, overlay, 0);
                    preview.Render(true, false);
                    capture = preview.EndStaticPreview();
                }
                var pixels = capture.GetPixels32();
                int highlightedTiles = 0;
                for (int y = 0; y < 30; y++) for (int x = 0; x < 30; x++)
                {
                    bool found = false;
                    for (int py = 20 + y * 20; py < 40 + y * 20 && !found; py++)
                        for (int px = 20 + x * 20; px < 40 + x * 20; px++)
                        {
                            var p = pixels[py * 640 + px];
                            if (p.r > 150 && p.g > 100 && p.b < 100) { found = true; break; }
                        }
                    if (found) highlightedTiles++;
                }
                Assert.That(highlightedTiles, Is.EqualTo(occlude ? 0 : 900), "The overlay must show each visible face and no face behind an occluder.");
                Directory.CreateDirectory("Logs");
                File.WriteAllBytes(occlude ? "Logs/overlay-occluded.png" : "Logs/overlay-900-faces.png", capture.EncodeToPNG());
            }
            finally
            {
                preview.Cleanup(); VoxelSelectionOverlay.Destroy(meshes);
                if (capture != null) UnityEngine.Object.DestroyImmediate(capture);
                UnityEngine.Object.DestroyImmediate(model); UnityEngine.Object.DestroyImmediate(overlay); UnityEngine.Object.DestroyImmediate(baseMaterial);
            }
        }
    }
}
