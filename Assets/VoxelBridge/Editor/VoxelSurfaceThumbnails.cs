using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace LocalModels.VoxelBridge
{
    internal sealed class VoxelSurfaceThumbnails : IDisposable
    {
        private readonly Dictionary<int, Texture2D> images = new();
        private PreviewRenderUtility preview;
        private Material material;
        private VoxelSurfacePalette palette;
        private int revision;

        internal Texture2D Get(VoxelSurfacePalette source, VoxelSurfaceDefinition surface)
        {
            int current = EditorUtility.GetDirtyCount(source);
            if (palette != source || revision != current) { Dispose(); palette = source; revision = current; }
            if (images.TryGetValue(surface.Id, out var image)) return image;
            bool warmup = preview == null;
            if (preview == null)
            {
                material = new Material(Shader.Find("HDRP/Lit")) { hideFlags = HideFlags.HideAndDontSave };
                preview = new PreviewRenderUtility();
                preview.camera.fieldOfView = 35; preview.camera.nearClipPlane = .1f; preview.camera.farClipPlane = 20;
                preview.camera.clearFlags = CameraClearFlags.SolidColor;
                preview.camera.backgroundColor = new Color(.12f, .13f, .15f);
                preview.camera.transform.SetPositionAndRotation(new Vector3(0, 0, -4), Quaternion.identity);
                preview.ambientColor = new Color(.25f, .25f, .25f);
                for (int i = 0; i < preview.lights.Length; i++)
                {
                    var light = preview.lights[i];
                    if (light.GetComponent<HDAdditionalLightData>() == null) light.gameObject.AddComponent<HDAdditionalLightData>();
                    light.lightUnit = LightUnit.Lux; light.intensity = i == 0 ? 4 : 2;
                    light.color = Color.white; light.useColorTemperature = false; light.shadows = LightShadows.None;
                    light.transform.rotation = Quaternion.Euler(i == 0 ? new Vector3(25, 25, 0) : new Vector3(335, 315, 0));
                }
            }
            VoxelSurfacePreviewWindow.ApplySurface(material, surface, new Color(.65f, .65f, .65f));
            var sphere = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
            preview.camera.aspect = 1;
            float distance = VoxelSurfacePreviewWindow.FramingDistance(sphere.bounds, true, 1);
            preview.camera.transform.position = sphere.bounds.center + Vector3.back * distance;
            Texture rendered = null;
            // The first HDRP preview cull initializes the preview scene and light data.
            for (int pass = 0; pass < (warmup ? 2 : 1); pass++)
            {
                preview.BeginPreview(new Rect(0, 0, 72, 72), GUIStyle.none);
                try { preview.DrawMesh(sphere, Matrix4x4.identity, material, 0); preview.Render(true, false); }
                finally { rendered = preview.EndPreview(); }
            }
            var previous = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(72, 72, 0, RenderTextureFormat.ARGB32);
            Texture2D result = null;
            try
            {
                Graphics.Blit(rendered, rt); RenderTexture.active = rt;
                result = new Texture2D(72, 72, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                result.ReadPixels(new Rect(0, 0, 72, 72), 0, 0); result.Apply();
                images.Add(surface.Id, result); return result;
            }
            catch { if (result != null) UnityEngine.Object.DestroyImmediate(result); throw; }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt); }
        }

        public void Dispose()
        {
            foreach (var image in images.Values) UnityEngine.Object.DestroyImmediate(image);
            images.Clear(); preview?.Cleanup(); preview = null;
            if (material != null) UnityEngine.Object.DestroyImmediate(material);
            material = null;
        }
    }
}
