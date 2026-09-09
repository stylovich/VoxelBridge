using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    internal sealed partial class VoxelSurfacePainterWindow
    {
        private VoxelPainterAppearancePreview appearancePreview;
        private VoxelSurfaceThumbnails thumbnails;
        private bool previewOutline, compareOriginalAppearance;

        private void DrawColorControls()
        {
            try
            {
                var options = edit.AllowedColors();
                if (options.Length == 0) throw new InvalidOperationException("The source profile has no allowed colors.");
                int index = Array.FindIndex(options, c => c.Id == colorId);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Color", GUILayout.Width(45));
                    GUILayout.Label(index < 0 ? $"{colorId:000} · Not allowed by profile" : $"{colorId:000} · {options[index].DisplayName}", GUILayout.MaxWidth(350));
                    Rect swatch = GUILayoutUtility.GetRect(30, 18, GUILayout.Width(30));
                    edit.Colors.TryGetColor(colorId, out var rgb); EditorGUI.DrawRect(swatch, rgb);
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(selected.Count == 0 || appearancePreview != null || index < 0))
                        if (GUILayout.Button("Apply Color", GUILayout.Width(108))) Run(() => Change(() => edit.ApplyColor(selected, colorId)));
                }
            }
            catch (Exception exception) { EditorGUILayout.HelpBox(exception.Message, MessageType.Warning); }
        }

        private void DrawSurfaceBrowser()
        {
            var options = edit.Surfaces.Entries.Where(s => s != null && s.RenderClass == VoxelSurfaceRenderClass.Opaque).OrderBy(s => s.Id).ToArray();
            int index = Array.FindIndex(options, s => s.Id == surfaceId);
            if (options.Length == 0) return;
            int count = Mathf.Min(5, options.Length);
            int start = Mathf.Clamp(Mathf.Max(0, index) - count / 2, 0, options.Length - count);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("‹", "Previous surface"), GUILayout.Width(25), GUILayout.Height(78))) StepAppearance(-1);
                for (int i = start; i < start + count; i++)
                {
                    var surface = options[i];
                    Rect rect = GUILayoutUtility.GetRect(100, 90, GUILayout.Width(100));
                    string tooltip = $"{surface.Id:000} · {surface.DisplayName}\nMetallic {surface.Metallic:0.##} · Smoothness {surface.Smoothness:0.##} · Emission {surface.Emission:0.##}\nReferencia con color neutro, sin bloom ni exposición de escena.";
                    if (Event.current.type == EventType.Repaint)
                    {
                        EditorGUI.DrawRect(rect, surface.Id == surfaceId ? new Color(.3f, .65f, .8f) : new Color(.12f, .13f, .15f));
                        try
                        {
                            thumbnails ??= new VoxelSurfaceThumbnails();
                            var texture = thumbnails.Get(edit.Surfaces, surface);
                            GUI.DrawTexture(new Rect(rect.x + 3, rect.y + 3, rect.width - 6, 62), texture, ScaleMode.ScaleToFit, false);
                        }
                        catch (Exception exception) { GUI.Label(rect, new GUIContent("Preview unavailable", exception.Message), EditorStyles.miniLabel); }
                    }
                    GUI.Label(new Rect(rect.x + 3, rect.y + 65, rect.width - 6, 22), new GUIContent($"{surface.Id:000} {surface.DisplayName}", tooltip), EditorStyles.miniLabel);
                    if (GUI.Button(rect, new GUIContent("", tooltip), GUIStyle.none))
                    { surfaceId = surface.Id; Run(UpdateAppearanceCandidate); }
                }
                if (GUILayout.Button(new GUIContent("›", "Next surface"), GUILayout.Width(25), GUILayout.Height(78))) StepAppearance(1);
                GUILayout.FlexibleSpace();
            }
        }

        private void StartAppearancePreview()
        {
            if (edit == null || selectionJob != null || EditorApplication.isPlayingOrWillChangePlaymode || selected.Count == 0) return;
            edit.ValidateUnchangedSources();
            if (editColor) edit.ValidateColor(colorId);
            else ValidatePreviewSurface();
            CancelAppearancePreview();
            var candidate = new VoxelPainterAppearancePreview(ViewGrid, selected, edit.HideInternalCavities, VoxelProductionEditor.Progress);
            appearancePreview = candidate;
            compareOriginalAppearance = false;
            try { UpdateAppearanceCandidate(); }
            catch { CancelAppearancePreview(); throw; }
            overlayCount = -1; hover = -1;
            if (Event.current != null) GUI.FocusControl(null);
        }

        private void ValidatePreviewSurface()
        {
            if (!edit.Surfaces.TryGetSurface(surfaceId, out var surface) || surface.RenderClass != VoxelSurfaceRenderClass.Opaque)
                throw new InvalidOperationException("Choose an opaque surface from the current palette.");
        }

        private void UpdateAppearanceCandidate()
        {
            if (appearancePreview == null) { Repaint(); return; }
            if (editColor) { edit.ValidateColor(colorId); appearancePreview.SetColor(colorId); }
            else { ValidatePreviewSurface(); appearancePreview.SetSurface(surfaceId); }
            Repaint();
        }

        private void StepAppearance(int direction)
        {
            Run(() =>
            {
                int[] ids = editColor ? FilterColorChoices(edit.AllowedColors(), colorSearch).Select(c => c.Id).ToArray() :
                    edit.Surfaces.Entries.Where(s => s != null && s.RenderClass == VoxelSurfaceRenderClass.Opaque).OrderBy(s => s.Id).Select(s => s.Id).ToArray();
                if (ids.Length == 0) return;
                int index = Array.IndexOf(ids, editColor ? colorId : surfaceId);
                int next = ids[editColor ? Mathf.Clamp(Mathf.Max(0, index) + direction, 0, ids.Length - 1) :
                    (Mathf.Max(0, index) + direction + ids.Length) % ids.Length];
                if (editColor) colorId = next; else surfaceId = next;
                UpdateAppearanceCandidate();
            });
        }

        private bool DrawAppearanceSession()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label(editColor ? "COLOR PREVIEW · Lit" : "SURFACE PREVIEW · Lit", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                compareOriginalAppearance = GUILayout.Toggle(compareOriginalAppearance, new GUIContent("Original", "Comparar con el borrador anterior al preview. Confirm aplica siempre el candidato elegido."), EditorStyles.toolbarButton, GUILayout.Width(65));
                previewOutline = GUILayout.Toggle(previewOutline, new GUIContent("Outline", "Mostrar el contorno de selección durante la comparación."), EditorStyles.toolbarButton, GUILayout.Width(65));
                if (GUILayout.Button(new GUIContent("Confirm", "Enter — Confirma una sola operación en el historial."), EditorStyles.toolbarButton, GUILayout.Width(75))) Run(ConfirmAppearancePreview);
                if (GUILayout.Button(new GUIContent("Cancel", "Esc — Descarta la previsualización, conservando borrador y selección."), EditorStyles.toolbarButton, GUILayout.Width(65))) CancelAppearancePreview();
            }
            GUILayout.Label(editColor ? "Cuadrícula lateral / flechas eligen color · Enter confirma · Esc cancela" :
                "← / → cambia candidato · Enter confirma · Esc cancela · Cámara: controles habituales", EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(status) && statusType != MessageType.Info) EditorGUILayout.HelpBox(status, statusType);
            if (editColor) DrawColorControls(); else
            {
                if (edit.Surfaces.TryGetSurface(surfaceId, out var surface))
                    GUILayout.Label($"{surface.Id:000} · {surface.DisplayName}   M {surface.Metallic:0.##}   S {surface.Smoothness:0.##}   E {surface.Emission:0.##}", EditorStyles.miniLabel);
                DrawSurfaceBrowser();
            }
            return true;
        }

        private void ConfirmAppearancePreview()
        {
            if (appearancePreview == null) return;
            edit.ValidateUnchangedSources();
            int[] cells = appearancePreview.Cells;
            if (editColor) edit.ValidateColor(colorId); else ValidatePreviewSurface();
            Change(() => { if (editColor) edit.ApplyColor(cells, colorId); else edit.Apply(cells, surfaceId); });
        }

        private void CancelAppearancePreview()
        {
            appearancePreview?.Dispose(); appearancePreview = null; compareOriginalAppearance = false; overlayCount = -1;
        }

        private void HandleAppearanceKeys(Event e)
        {
            if (appearancePreview == null || e.type != EventType.KeyDown || EditorGUIUtility.editingTextField ||
                e.alt || e.control || e.command || e.shift || GUIUtility.hotControl != 0) return;
            if (e.keyCode == KeyCode.LeftArrow || e.keyCode == KeyCode.RightArrow) StepAppearance(e.keyCode == KeyCode.LeftArrow ? -1 : 1);
            else if (editColor && (e.keyCode == KeyCode.UpArrow || e.keyCode == KeyCode.DownArrow))
                StepAppearance((e.keyCode == KeyCode.UpArrow ? -1 : 1) * ColorGridColumns(SidebarWidth - 28));
            else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) Run(ConfirmAppearancePreview);
            else if (e.keyCode == KeyCode.Escape) { CancelAppearancePreview(); Repaint(); }
            else return;
            e.Use();
        }
    }
}
