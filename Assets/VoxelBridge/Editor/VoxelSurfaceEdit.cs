using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;
using UnityEditor;

namespace LocalModels.VoxelBridge
{
    // A local edit buffer. Produces validated candidates; never writes source files or meshes.
    internal sealed class VoxelSurfaceEdit
    {
        internal const int MaximumSelection = 131072;
        internal const int MaximumHistoryCells = 1048576;
        internal const int MaximumHistorySteps = 64;

        private sealed class Change
        {
            internal bool IsSelection;
            internal int[] Cells;
            internal ushort[] Before;
            internal ushort[] After;
        }

        private readonly string sourcePath;
        private readonly string metadataPath;
        private readonly byte[] sourceBytes;
        private readonly byte[] metadataBytes;
        private readonly VoxelBridgeMetadata metadata;
        private readonly int[] recordOffsets;
        private readonly ushort[] originalPairs;
        private readonly List<Change> history = new();
        private readonly VoxelColorPalette colors;
        private readonly VoxelSurfacePalette surfaces;
        private readonly string colorHash;
        private readonly string surfaceHash;
        private readonly string sourceHash;
        private readonly string metadataHash;
        private int cursor;
        private int historyCells;
        private readonly HashSet<int> selection;
        private readonly Dictionary<int, int> colorEdits = new();

        internal VoxelGrid Grid { get; }
        internal string AssetPath { get; }
        internal bool CanUndo => cursor > 0;
        internal bool CanRedo => cursor < history.Count;
        internal int HistorySteps => history.Count;
        internal int PendingCells { get; private set; }
        internal int SurfaceRevision { get; private set; }
        internal VoxelSurfacePalette Surfaces => surfaces;
        internal VoxelColorPalette Colors => colors;
        internal string Fingerprint => sourceHash + metadataHash + colorHash + surfaceHash;
        internal bool HideInternalCavities => metadata.hideInternalCavities;
        internal int LodIndex => metadata.lodIndex;

        internal VoxelColorDefinition[] AllowedColors()
        {
            string guid = metadata.semantic.colorMappingProfileGuid;
            string path = string.IsNullOrEmpty(guid) ? metadata.semantic.colorMappingProfileAssetPath : AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(guid) && string.IsNullOrEmpty(path))
                return colors.Entries.Where(c => c != null).OrderBy(c => c.Id).ToArray();
            var profile = AssetDatabase.LoadAssetAtPath<VoxelColorMappingProfile>(path ?? "");
            if (profile == null || profile.ColorPalette != colors)
                throw new InvalidDataException("The source color mapping profile is missing or uses another palette. Restore it before editing colors.");
            if (!profile.TryGetAllowedColors(out var allowed, out string error)) throw new InvalidDataException(error);
            return allowed.OrderBy(c => c.Id).ToArray();
        }

        internal void ValidateColor(int id)
        {
            ValidatePaletteState();
            if (!colors.TryGetColor(id, out _) || !AllowedColors().Any(c => c.Id == id))
                throw new InvalidDataException($"ColorID {id} is unknown or excluded by the source color mapping profile.");
        }

        internal void GetChanges(out int[] cells, out int[] surfaceIds)
        {
            var changed = new List<int>();
            for (int i = 0; i < originalPairs.Length; i++)
                if (Grid.Occupied[i] && Grid.SemanticIds[i] != originalPairs[i]) changed.Add(i);
            cells = changed.ToArray();
            surfaceIds = cells.Select(i => VoxelSemanticEncoding.SurfaceId(Grid.SemanticIds[i])).ToArray();
        }

        internal void ClearHistory() { history.Clear(); cursor = 0; historyCells = 0; }

        internal VoxelSurfaceEdit(string voxAssetPath, HashSet<int> selection = null)
        {
            // The painter shares this set; selection changes belong to the same local history.
            this.selection = selection ?? new HashSet<int>();
            if (string.IsNullOrWhiteSpace(voxAssetPath) || !voxAssetPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                !voxAssetPath.EndsWith(".vox", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose a VOX source under Assets.", nameof(voxAssetPath));
            AssetPath = voxAssetPath;
            sourcePath = VoxelLodPipeline.AssetPathToAbsolute(voxAssetPath);
            metadataPath = VoxelLodPipeline.AssetPathToAbsolute(
                VoxelImporterIntegration.GetMetadataAssetPath(voxAssetPath));
            if ((File.Exists(sourcePath) && new FileInfo(sourcePath).Length > 64L * 1024 * 1024) ||
                (File.Exists(metadataPath) && new FileInfo(metadataPath).Length > 4L * 1024 * 1024))
                throw new InvalidDataException("The surface editing source exceeds the file size limit.");
            if (!VoxelImporterIntegration.TryLoadMetadata(voxAssetPath, out metadata, out string error))
                throw new InvalidDataException(error);
            if (metadata.formatVersion != 4)
                throw new InvalidDataException("Surface editing requires a semantic v4 sidecar. Bind the source first.");
            VoxelSemanticMesher.ValidateGrid(metadata.unityGridSize, metadata.gridOrigin, metadata.voxelSize);
            sourceBytes = File.ReadAllBytes(sourcePath);
            metadataBytes = File.ReadAllBytes(metadataPath);
            if (JsonUtility.ToJson(metadata) != JsonUtility.ToJson(JsonUtility.FromJson<VoxelBridgeMetadata>(
                    System.Text.Encoding.UTF8.GetString(metadataBytes))))
                throw new InvalidOperationException("The sidecar changed while loading. Reload the source.");
            sourceHash = Hash(sourceBytes);
            metadataHash = Hash(metadataBytes);
            if (!VoxelSemanticTransport.TryLoadPalettes(metadata.semantic, out colors, out surfaces, out error))
                throw new InvalidDataException(error);
            ReadPaletteHashes(out colorHash, out surfaceHash);
            recordOffsets = new int[checked(metadata.unityGridSize.x * metadata.unityGridSize.y * metadata.unityGridSize.z)];
            Grid = VoxelVolumeReader.Read(sourcePath, metadata, (cell, offset) => recordOffsets[cell] = offset);
            originalPairs = (ushort[])Grid.SemanticIds.Clone();
            ValidateUnchangedSources();
            ValidateGridAndPairs();
            if (metadata.semantic.slots.GroupBy(entry => entry.colorId)
                .Any(group => group.Select(entry => entry.displayColor).Distinct().Count() != 1))
                throw new InvalidDataException("The same ColorID has conflicting local display colors. Rebind the source before editing surfaces.");
        }

        internal int Apply(IReadOnlyCollection<int> selection, int surfaceId) => ApplyAttribute(selection, surfaceId, false);
        internal int ApplyColor(IReadOnlyCollection<int> selection, int colorId) => ApplyAttribute(selection, colorId, true);

        private int ApplyAttribute(IReadOnlyCollection<int> selection, int id, bool color)
        {
            if (selection == null) throw new ArgumentNullException(nameof(selection));
            if (selection.Count > MaximumSelection)
                throw new InvalidOperationException($"Select at most {MaximumSelection:N0} cells per operation.");
            ValidatePaletteState();
            if (color) ValidateColor(id); else ValidateSurface(id);
            int[] cells = selection.Distinct().OrderBy(cell => cell).ToArray();
            foreach (int cell in cells)
            {
                if (cell < 0 || cell >= Grid.Occupied.Length || !Grid.Occupied[cell] || recordOffsets[cell] == 0)
                    throw new InvalidDataException("The selection contains an empty or invalid cell.");
                int expected = colorEdits.TryGetValue(cell, out int authorized) ? authorized : VoxelSemanticEncoding.ColorId(originalPairs[cell]);
                if (VoxelSemanticEncoding.ColorId(Grid.SemanticIds[cell]) != expected)
                    throw new InvalidDataException("ColorID changed outside the validated color editing operation.");
            }
            cells = cells.Where(cell => (color ? VoxelSemanticEncoding.ColorId(Grid.SemanticIds[cell]) : VoxelSemanticEncoding.SurfaceId(Grid.SemanticIds[cell])) != id).ToArray();
            if (cells.Length == 0) return 0;
            var change = new Change { Cells = cells, Before = new ushort[cells.Length], After = new ushort[cells.Length] };
            for (int i = 0; i < cells.Length; i++)
            {
                change.Before[i] = Grid.SemanticIds[cells[i]];
                change.After[i] = color ? VoxelSemanticEncoding.Pack(id, VoxelSemanticEncoding.SurfaceId(change.Before[i])) :
                    VoxelSemanticEncoding.Pack(VoxelSemanticEncoding.ColorId(change.Before[i]), id);
            }
            int pending = PendingCells;
            for (int i = 0; i < cells.Length; i++)
            {
                if (change.Before[i] != originalPairs[cells[i]]) pending--;
                if (change.After[i] != originalPairs[cells[i]]) pending++;
            }
            if (pending > MaximumHistoryCells)
                throw new InvalidOperationException("The pending edit limit was reached. Save or discard before editing more cells.");
            Record(change);
            Set(change, change.After);
            return cells.Length;
        }

        internal void Select(IReadOnlyCollection<int> cells)
        {
            if (cells == null) throw new ArgumentNullException(nameof(cells));
            if (cells.Count > MaximumSelection)
                throw new InvalidOperationException($"Select at most {MaximumSelection:N0} cells per operation.");
            var next = new HashSet<int>(cells);
            foreach (int cell in next)
                if (cell < 0 || cell >= Grid.Occupied.Length || !Grid.Occupied[cell] || recordOffsets[cell] == 0)
                    throw new InvalidDataException("The selection contains an empty or invalid cell.");
            var delta = new HashSet<int>(selection);
            delta.SymmetricExceptWith(next);
            if (delta.Count == 0) return;
            int[] changed = delta.ToArray();
            var change = new Change { IsSelection = true, Cells = changed,
                Before = new ushort[changed.Length], After = new ushort[changed.Length] };
            for (int i = 0; i < changed.Length; i++)
            {
                change.Before[i] = (ushort)(selection.Contains(changed[i]) ? 1 : 0);
                change.After[i] = (ushort)(next.Contains(changed[i]) ? 1 : 0);
            }
            Record(change);
            Set(change, change.After);
        }

        private void Record(Change change)
        {
            // Validate the whole operation before changing state or truncating redo history.
            while (history.Count > cursor)
            {
                historyCells -= history[history.Count - 1].Cells.Length;
                history.RemoveAt(history.Count - 1);
            }
            while (history.Count >= MaximumHistorySteps || historyCells + change.Cells.Length > MaximumHistoryCells)
            {
                historyCells -= history[0].Cells.Length;
                history.RemoveAt(0);
                cursor--;
            }
            history.Add(change);
            historyCells += change.Cells.Length;
            cursor++;
        }

        internal void Undo()
        {
            if (!CanUndo) return;
            Change change = history[--cursor];
            Set(change, change.Before);
        }

        internal void Redo()
        {
            if (!CanRedo) return;
            Change change = history[cursor++];
            Set(change, change.After);
        }

        private void Set(Change change, ushort[] pairs)
        {
            if (change.IsSelection)
            {
                for (int i = 0; i < change.Cells.Length; i++)
                    if (pairs[i] != 0) selection.Add(change.Cells[i]);
                    else selection.Remove(change.Cells[i]);
                return;
            }
            SurfaceRevision++;
            for (int i = 0; i < change.Cells.Length; i++)
            {
                int cell = change.Cells[i];
                if (Grid.SemanticIds[cell] != originalPairs[cell]) PendingCells--;
                Grid.SemanticIds[cell] = pairs[i];
                int color = VoxelSemanticEncoding.ColorId(pairs[i]);
                if (color != VoxelSemanticEncoding.ColorId(originalPairs[cell])) colorEdits[cell] = color;
                else colorEdits.Remove(cell);
                if (Grid.SemanticIds[cell] != originalPairs[cell]) PendingCells++;
            }
        }

        internal void ValidateUnchangedSources()
        {
            if (!File.Exists(sourcePath) || !File.Exists(metadataPath) ||
                new FileInfo(sourcePath).Length != sourceBytes.Length ||
                new FileInfo(metadataPath).Length != metadataBytes.Length ||
                Hash(File.ReadAllBytes(sourcePath)) != sourceHash || Hash(File.ReadAllBytes(metadataPath)) != metadataHash)
                throw new InvalidOperationException("The VOX or sidecar changed externally. Reload before saving surface edits.");
            ValidatePaletteState();
        }

        internal void BuildOutput(out byte[] voxBytes, out byte[] sidecarBytes)
        {
            voxBytes = null;
            sidecarBytes = null;
            ValidateUnchangedSources();
            HashSet<ushort> usedPairs = ValidateGridAndPairs();
            if (usedPairs.Count > byte.MaxValue)
                throw new InvalidDataException($"Surface edits use {usedPairs.Count} ColorID + SurfaceID pairs; VOX supports at most 255. No files were changed.");
            bool changed = false;
            for (int cell = 0; cell < originalPairs.Length; cell++)
                if (Grid.Occupied[cell] && Grid.SemanticIds[cell] != originalPairs[cell]) { changed = true; break; }
            if (!changed)
            {
                voxBytes = (byte[])sourceBytes.Clone();
                sidecarBytes = (byte[])metadataBytes.Clone();
                return;
            }

            var pairSlots = new Dictionary<ushort, VoxelSemanticSlotMetadata>();
            var occupiedSlots = new bool[256];
            // Preserve surviving pairs in their original slots, independent of frequency.
            foreach (var entry in metadata.semantic.slots)
            {
                ushort pair = VoxelSemanticEncoding.Pack(entry.colorId, entry.surfaceId);
                if (!usedPairs.Contains(pair)) continue;
                pairSlots.Add(pair, new VoxelSemanticSlotMetadata
                {
                    slot = entry.slot, colorId = entry.colorId, surfaceId = entry.surfaceId, displayColor = entry.displayColor
                });
                occupiedSlots[entry.slot] = true;
            }
            foreach (ushort pair in usedPairs.OrderBy(pair => pair))
            {
                if (pairSlots.ContainsKey(pair)) continue;
                int slot = Array.FindIndex(occupiedSlots, 1, value => !value);
                if (slot < 1) throw new InvalidDataException("No local semantic slot is available.");
                occupiedSlots[slot] = true;
                int colorId = VoxelSemanticEncoding.ColorId(pair);
                var originalColor = metadata.semantic.slots.FirstOrDefault(entry => entry.colorId == colorId);
                Color32 displayColor;
                if (originalColor != null) displayColor = originalColor.displayColor;
                else if (!colors.TryGetColor(colorId, out displayColor)) throw new InvalidDataException("Unknown ColorID.");
                pairSlots.Add(pair, new VoxelSemanticSlotMetadata
                {
                    slot = slot, colorId = colorId, surfaceId = VoxelSemanticEncoding.SurfaceId(pair), displayColor = displayColor
                });
            }
            byte[] updated = (byte[])sourceBytes.Clone();
            for (int cell = 0; cell < Grid.Occupied.Length; cell++)
                if (Grid.Occupied[cell]) updated[recordOffsets[cell]] = (byte)pairSlots[Grid.SemanticIds[cell]].slot;
            updated = VoxelSemanticVoxDocument.Parse(updated).BuildSemanticBytes(pairSlots.Values.ToArray(), out var slots);
            if (updated.Length != sourceBytes.Length)
                throw new InvalidDataException("Surface editing does not support palette chunks with nested content.");
            var revised = JsonUtility.FromJson<VoxelBridgeMetadata>(System.Text.Encoding.UTF8.GetString(metadataBytes));
            revised.semantic.slots = slots;
            revised.semantic.slotTableHash = VoxelSemanticTransport.ComputeSlotTableHash(slots);
            revised.paletteColorCount = slots.Length;
            if (!VoxelSemanticTransport.TryResolveSlotSemantics(VoxelSemanticVoxDocument.Parse(updated),
                    revised.semantic, colors, surfaces, out var resolved, out _, out string error))
                throw new InvalidDataException(error);
            // Surface edits preserve chunk lengths, so original record offsets remain valid.
            for (int cell = 0; cell < Grid.Occupied.Length; cell++)
                if (Grid.Occupied[cell] && resolved[updated[recordOffsets[cell]]] != Grid.SemanticIds[cell])
                    throw new InvalidDataException("The candidate VOX does not preserve the edited cell semantics.");
            voxBytes = updated;
            sidecarBytes = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(revised, true));
        }

        private HashSet<ushort> ValidateGridAndPairs()
        {
            var pairs = new HashSet<ushort>();
            for (int cell = 0; cell < Grid.Occupied.Length; cell++)
            {
                if (Grid.Occupied[cell] != (recordOffsets[cell] != 0))
                    throw new InvalidDataException("Surface editing cannot change voxel occupancy.");
                if (!Grid.Occupied[cell]) continue;
                ushort pair = Grid.SemanticIds[cell];
                int expectedColor = colorEdits.TryGetValue(cell, out int authorized) ? authorized : VoxelSemanticEncoding.ColorId(originalPairs[cell]);
                if (VoxelSemanticEncoding.ColorId(pair) != expectedColor)
                    throw new InvalidDataException("ColorID changed outside the validated color editing operation.");
                pairs.Add(pair);
            }
            if (colorEdits.Count > 0)
            {
                var allowed = new HashSet<int>(AllowedColors().Select(c => c.Id));
                if (colorEdits.Values.Any(id => !allowed.Contains(id)))
                    throw new InvalidDataException("The color mapping profile excludes a pending ColorID. Restore the profile or undo the color edit.");
            }
            foreach (ushort pair in pairs)
            {
                if (!colors.TryGetColor(VoxelSemanticEncoding.ColorId(pair), out _))
                    throw new InvalidDataException("The volume references an unknown ColorID.");
                ValidateSurface(VoxelSemanticEncoding.SurfaceId(pair));
            }
            return pairs;
        }

        private void ValidateSurface(int id)
        {
            if (!surfaces.TryGetSurface(id, out var entry) || entry.RenderClass != VoxelSurfaceRenderClass.Opaque)
                throw new InvalidDataException($"SurfaceID {id} is unknown or not opaque.");
        }

        private void ValidatePaletteState()
        {
            ReadPaletteHashes(out string currentColors, out string currentSurfaces);
            if (currentColors != colorHash || currentSurfaces != surfaceHash)
                throw new InvalidOperationException("A global palette changed during editing. Reload before saving surface edits.");
        }

        private void ReadPaletteHashes(out string currentColors, out string currentSurfaces)
        {
            if (!VoxelPaletteLutBuilder.TryBuildColorPixels(colors, out _, out currentColors, out string error))
                throw new InvalidDataException(error);
            if (!VoxelPaletteLutBuilder.TryBuildSurfacePixels(surfaces, out _, out currentSurfaces, out error))
                throw new InvalidDataException(error);
        }

        private static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(bytes));
        }

        internal static bool Pick(VoxelGrid grid, Ray ray, out int cell)
        {
            cell = -1;
            Vector3 origin = (ray.origin - grid.Origin) / grid.VoxelSize;
            Vector3 direction = ray.direction;
            float enter = 0, exit = float.PositiveInfinity;
            if (direction.sqrMagnitude < 1e-12f) return false;
            for (int axis = 0; axis < 3; axis++)
            {
                if (!float.IsFinite(origin[axis]) || !float.IsFinite(direction[axis])) return false;
                if (Mathf.Abs(direction[axis]) < 1e-10f)
                { if (origin[axis] < 0 || origin[axis] >= grid.Size[axis]) return false; continue; }
                float a = -origin[axis] / direction[axis], b = (grid.Size[axis] - origin[axis]) / direction[axis];
                enter = Mathf.Max(enter, Mathf.Min(a, b)); exit = Mathf.Min(exit, Mathf.Max(a, b));
            }
            if (enter >= exit) return false;
            Vector3 p = origin + direction * (enter + 0.0001f);
            var coordinate = new Vector3Int(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y), Mathf.FloorToInt(p.z));
            Vector3 next = Vector3.zero, delta = Vector3.zero;
            var step = new Vector3Int();
            for (int axis = 0; axis < 3; axis++)
            {
                step[axis] = direction[axis] > 0 ? 1 : direction[axis] < 0 ? -1 : 0;
                delta[axis] = step[axis] == 0 ? float.PositiveInfinity : Mathf.Abs(1 / direction[axis]);
                next[axis] = step[axis] == 0 ? float.PositiveInfinity :
                    (coordinate[axis] + (step[axis] > 0 ? 1 : 0) - origin[axis]) / direction[axis];
            }
            int limit = grid.Size.x + grid.Size.y + grid.Size.z + 3;
            for (int iteration = 0; iteration < limit; iteration++)
            {
                if (coordinate.x < 0 || coordinate.y < 0 || coordinate.z < 0 ||
                    coordinate.x >= grid.Size.x || coordinate.y >= grid.Size.y || coordinate.z >= grid.Size.z) return false;
                int index = grid.Index(coordinate.x, coordinate.y, coordinate.z);
                if (grid.Occupied[index]) { cell = index; return true; }
                float nearest = Mathf.Min(next.x, Mathf.Min(next.y, next.z));
                if (nearest >= exit) return false;
                for (int axis = 0; axis < 3; axis++)
                    if (next[axis] <= nearest)
                    { coordinate[axis] += step[axis]; next[axis] += delta[axis]; }
            }
            return false;
        }
    }
}
