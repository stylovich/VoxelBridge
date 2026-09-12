using System;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    internal sealed partial class VoxelSurfacePainterWindow
    {
        private VoxelPainterReductionPreview reductionPreview;
        private bool reductionActive, reductionOriginal, reductionLosses, reductionStale;
        private int reductionRevision, reductionProfileRevision;
        private VoxelStyleProfile reductionProfile;
        private string reductionDescription;
        private bool ReductionStale => reductionStale || reductionPreview != null &&
            (edit.SurfaceRevision != reductionRevision || reductionProfile == null ||
             EditorUtility.GetDirtyCount(reductionProfile) != reductionProfileRevision);
        private VoxelGrid ReductionViewGrid => reductionOriginal || ReductionStale ? edit.Grid : reductionPreview.Reduced;
        private Mesh ReductionViewMesh => ReductionStale ? mesh : reductionOriginal
            ? (reductionLosses ? reductionPreview.LossMesh : mesh) : reductionPreview.Mesh;

        private void OnProjectChange()
        {
            if (reductionPreview != null) { reductionStale = true; Repaint(); }
        }

        private void StartReductionPreview()
        {
            if (edit == null || selectionJob != null || appearancePreview != null ||
                EditorApplication.isPlayingOrWillChangePlaymode) return;
            edit.ValidateUnchangedSources();
            ResolveRebuildTarget(out _, out string manifestPath);
            if (string.IsNullOrEmpty(manifestPath))
                throw new InvalidOperationException("Next LOD preview requires a linked production LOD family. Open its prefab in Source.");
            var manifest = VoxelProductionFamily.Load(manifestPath);
            var profile = VoxelProductionFamily.Profile(manifest);
            int next = level + 1;
            float size = VoxelProductionFamily.ReductionVoxelSize(manifest, profile, next, edit.Grid.VoxelSize, out _);
            // Invalidate before work so cancellation never presents an old result as current.
            if (reductionPreview == null || ReductionStale || reductionProfile != profile || reductionPreview.Reduced.VoxelSize != size)
            {
                reductionStale = true;
                var candidate = new VoxelPainterReductionPreview(edit.Grid, size, profile.Padding, manifest.chunkCellSize,
                    edit.HideInternalCavities, VoxelProductionEditor.Progress, edit.Surfaces);
                reductionPreview?.Dispose(); reductionPreview = candidate;
                reductionRevision = edit.SurfaceRevision;
                reductionProfile = profile; reductionProfileRevision = EditorUtility.GetDirtyCount(profile);
                reductionStale = false;
            }
            reductionDescription = $"LOD {level} → {next} · {edit.Grid.VoxelSize:0.####} → {size:0.####} m · " +
                (next < manifest.lods.Length ? "Saved LOD unchanged" : "No saved LOD at target");
            // Isolation stays available after returning, but reduction always evaluates the whole draft.
            reductionActive = true; reductionOriginal = false; reductionLosses = false;
            hover = -1; overlayCount = -1;
            if (Event.current != null) GUI.FocusControl(null);
            Repaint();
        }

        private void ExitReductionPreview()
        {
            reductionActive = false; reductionLosses = false; hover = -1; overlayCount = overlayHover = -1;
            Repaint();
        }

        private void ReleaseReductionPreview()
        {
            reductionPreview?.Dispose(); reductionPreview = null; reductionProfile = null;
            reductionActive = reductionStale = reductionLosses = false;
        }

        private bool DrawReductionSession()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("NEXT LOD · REDUCE PREVIEW", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("Return to Paint", "Esc — Volver al borrador sin modificar fuentes, historial ni selección."),
                    EditorStyles.toolbarButton, GUILayout.Width(110))) ExitReductionPreview();
            }
            GUILayout.Label(reductionDescription, EditorStyles.miniLabel);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                using (new EditorGUI.DisabledScope(ReductionStale))
                {
                    if (GUILayout.Toggle(reductionOriginal, "Draft", EditorStyles.toolbarButton, GUILayout.Width(60)) && !reductionOriginal)
                    { reductionOriginal = true; reductionLosses = false; Repaint(); }
                    if (GUILayout.Toggle(!reductionOriginal, "If Regenerated: Reduce", EditorStyles.toolbarButton, GUILayout.Width(165)) && reductionOriginal)
                    { reductionOriginal = false; reductionLosses = false; Repaint(); }
                    var mode = (VoxelPainterViewMode)EditorGUILayout.Popup((int)viewMode,
                        new[] { "Lit", "Base Color", "SurfaceID", "Emission" }, GUILayout.Width(110));
                    if (mode != viewMode) Run(() => SetViewMode(mode));
                    using (new EditorGUI.DisabledScope(!reductionOriginal))
                    {
                        bool losses = GUILayout.Toggle(reductionLosses, new GUIContent("Changed Finish",
                            "Rojo: caras expuestas del borrador cuyo par ColorID/SurfaceID no gana en la celda gruesa compatible. No es un diagnóstico de silueta."),
                            EditorStyles.toolbarButton, GUILayout.Width(100));
                        if (losses != reductionLosses) Run(() =>
                        {
                            if (losses) reductionPreview.BuildLossMesh(VoxelProductionEditor.Progress);
                            reductionLosses = losses;
                        });
                    }
                }
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(!ReductionStale))
                    if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(65))) Run(StartReductionPreview);
            }
            GUILayout.Label(ReductionStale ? "Full draft · Previous reduction is stale · No files modified" :
                $"Full draft · {reductionPreview.ChangedCells:N0} cells with changed exposed contributions · No files modified", EditorStyles.miniLabel);
            if (ReductionStale) EditorGUILayout.HelpBox("Preview desactualizado: se muestra sólo el borrador. Refresh verifica fuentes y recalcula; ante conflicto externo, volver y recargar Source.", MessageType.Warning);
            else if (reductionLosses) GUILayout.Label("Rojo = cambia el acabado · Sin rojo no garantiza conservar silueta, huecos o relieve.", EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, statusType);
            return true;
        }

        private void HandleReductionKeys(Event e)
        {
            if (!reductionActive || e.type != EventType.KeyDown || EditorGUIUtility.editingTextField ||
                e.alt || e.control || e.command || GUIUtility.hotControl != 0) return;
            if (e.keyCode == KeyCode.Escape) ExitReductionPreview();
            else return;
            e.Use();
        }
    }
}
