using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    internal static class VoxelSemanticEncoding
    {
        public static ushort Pack(int colorId, int surfaceId)
        {
            if (colorId < 0 || colorId > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(colorId));
            if (surfaceId < 0 || surfaceId > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(surfaceId));
            return (ushort)(colorId | surfaceId << 8);
        }

        public static int ColorId(ushort semanticId) => semanticId & byte.MaxValue;
        public static int SurfaceId(ushort semanticId) => semanticId >> 8;
    }

    internal sealed class VoxelSemanticVoxDocument
    {
        private sealed class RawChunk
        {
            public string Id;
            public byte[] Bytes;
        }

        private readonly byte[] mainContent;
        private readonly List<RawChunk> children;

        public int VoxVersion { get; }
        public Color32[] Palette { get; }
        public byte[] UsedSlots { get; }
        public int[] SlotUsageCounts { get; }
        public bool HasUnsupportedIndexMap { get; }

        private VoxelSemanticVoxDocument(int voxVersion, byte[] mainContent,
            List<RawChunk> children, Color32[] palette, byte[] usedSlots,
            int[] slotUsageCounts, bool hasIndexMap)
        {
            VoxVersion = voxVersion;
            this.mainContent = mainContent;
            this.children = children;
            Palette = palette;
            UsedSlots = usedSlots;
            SlotUsageCounts = slotUsageCounts;
            HasUnsupportedIndexMap = hasIndexMap;
        }

        public static VoxelSemanticVoxDocument Read(string path) =>
            Parse(File.ReadAllBytes(path));

        public static VoxelSemanticVoxDocument Parse(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 20)
                throw new InvalidDataException("The VOX document is truncated.");

            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (ReadId(reader) != "VOX ")
                throw new InvalidDataException("The file has no VOX header.");
            int version = reader.ReadInt32();
            if (ReadId(reader) != "MAIN")
                throw new InvalidDataException("The VOX document has no MAIN chunk.");
            int mainContentBytes = ReadNonNegativeSize(reader, "MAIN");
            int mainChildrenBytes = ReadNonNegativeSize(reader, "MAIN");
            EnsureRemaining(stream, checked((long)mainContentBytes + mainChildrenBytes), "MAIN");
            byte[] mainContent = reader.ReadBytes(mainContentBytes);
            long mainEnd = stream.Position + mainChildrenBytes;

            var chunks = new List<RawChunk>();
            var usedSlots = new HashSet<byte>();
            var slotUsageCounts = new int[256];
            var palette = new Color32[256];
            bool hasPalette = false;
            bool hasNotes = false;
            bool hasIndexMap = false;

            while (stream.Position < mainEnd)
            {
                long chunkStart = stream.Position;
                EnsureRemaining(stream, 12, "chunk");
                string id = ReadId(reader);
                int contentBytes = ReadNonNegativeSize(reader, id);
                int childBytes = ReadNonNegativeSize(reader, id);
                long totalBytes = checked(12L + contentBytes + childBytes);
                if (chunkStart + totalBytes > mainEnd)
                    throw new InvalidDataException($"Chunk {id} exceeds the bounds of MAIN.");

                long contentStart = stream.Position;
                if (id == "RGBA")
                {
                    if (hasPalette)
                        throw new InvalidDataException("The VOX document contains more than one RGBA chunk.");
                    if (contentBytes != 1024)
                        throw new InvalidDataException("The RGBA chunk has an invalid size.");
                    for (int i = 0; i < palette.Length; i++)
                        palette[i] = new Color32(
                            reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                    hasPalette = true;
                }
                else if (id == "XYZI")
                {
                    if (contentBytes < 4)
                        throw new InvalidDataException("The XYZI chunk is truncated.");
                    int count = reader.ReadInt32();
                    if (count < 0 || checked(4L + count * 4L) > contentBytes)
                        throw new InvalidDataException("The XYZI voxel count is invalid.");
                    for (int i = 0; i < count; i++)
                    {
                        reader.ReadByte();
                        reader.ReadByte();
                        reader.ReadByte();
                        byte slot = reader.ReadByte();
                        if (slot == 0)
                            throw new InvalidDataException("XYZI contains reserved palette slot 0.");
                        usedSlots.Add(slot);
                        slotUsageCounts[slot] = checked(slotUsageCounts[slot] + 1);
                    }
                }
                else if (id == "NOTE")
                {
                    if (hasNotes)
                        throw new InvalidDataException("The VOX document contains more than one NOTE chunk.");
                    hasNotes = true;
                    ValidateNotes(reader, contentStart + contentBytes);
                }
                else if (id == "IMAP")
                {
                    hasIndexMap = true;
                }

                stream.Position = chunkStart;
                if (totalBytes > int.MaxValue)
                    throw new InvalidDataException($"Chunk {id} is too large.");
                chunks.Add(new RawChunk { Id = id, Bytes = reader.ReadBytes((int)totalBytes) });
            }

            if (stream.Position != mainEnd)
                throw new InvalidDataException("The size declared by MAIN does not match the file.");
            if (mainEnd != stream.Length)
                throw new InvalidDataException("The VOX document contains data outside MAIN.");
            if (!hasPalette)
                throw new InvalidDataException(
                    "The VOX document has no explicit RGBA palette and cannot be bound safely.");
            if (usedSlots.Count == 0)
                throw new InvalidDataException("The VOX document contains no occupied voxels.");

            byte[] orderedSlots = usedSlots.OrderBy(value => value).ToArray();
            return new VoxelSemanticVoxDocument(
                version, mainContent, chunks, palette, orderedSlots, slotUsageCounts,
                hasIndexMap);
        }

        public byte[] BuildSemanticBytes(IReadOnlyList<VoxelSemanticSlotMetadata> semanticSlots)
        {
            if (semanticSlots == null) throw new ArgumentNullException(nameof(semanticSlots));
            var palette = (Color32[])Palette.Clone();
            var seenSlots = new HashSet<int>();
            foreach (VoxelSemanticSlotMetadata entry in semanticSlots)
            {
                if (entry == null || entry.slot < 1 || entry.slot > byte.MaxValue)
                    throw new InvalidDataException("The semantic table contains a slot outside 1..255.");
                if (!seenSlots.Add(entry.slot))
                    throw new InvalidDataException($"Slot {entry.slot} is duplicated in the semantic table.");
                palette[entry.slot - 1] = entry.displayColor;
            }

            byte[] rgba = BuildChunk("RGBA", writer =>
            {
                foreach (Color32 color in palette)
                {
                    writer.Write(color.r);
                    writer.Write(color.g);
                    writer.Write(color.b);
                    writer.Write(color.a);
                }
            });
            var outputChildren = new List<byte[]>(children.Count);
            foreach (RawChunk child in children)
                outputChildren.Add(child.Id == "RGBA" ? rgba : child.Bytes);

            int childrenBytes = 0;
            foreach (byte[] child in outputChildren)
                childrenBytes = checked(childrenBytes + child.Length);

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            WriteId(writer, "VOX ");
            writer.Write(VoxVersion);
            WriteId(writer, "MAIN");
            writer.Write(mainContent.Length);
            writer.Write(childrenBytes);
            writer.Write(mainContent);
            foreach (byte[] child in outputChildren) writer.Write(child);
            return stream.ToArray();
        }

        private static void ValidateNotes(BinaryReader reader, long contentEnd)
        {
            if (contentEnd - reader.BaseStream.Position < 4)
                throw new InvalidDataException("The NOTE chunk is truncated.");
            int count = reader.ReadInt32();
            if (count < 0 || count > 256)
                throw new InvalidDataException("The NOTE chunk contains an invalid entry count.");
            for (int i = 0; i < count; i++)
            {
                if (contentEnd - reader.BaseStream.Position < 4)
                    throw new InvalidDataException("The NOTE chunk is truncated.");
                int length = reader.ReadInt32();
                if (length < 0 || length > contentEnd - reader.BaseStream.Position)
                    throw new InvalidDataException("A NOTE entry has an invalid length.");
                reader.BaseStream.Position += length;
            }
        }

        private static byte[] BuildChunk(string id, Action<BinaryWriter> writeContent)
        {
            using var content = new MemoryStream();
            using (var writer = new BinaryWriter(content, Encoding.UTF8, leaveOpen: true))
                writeContent(writer);
            byte[] contentBytes = content.ToArray();
            using var chunk = new MemoryStream();
            using (var writer = new BinaryWriter(chunk, Encoding.UTF8, leaveOpen: true))
            {
                WriteId(writer, id);
                writer.Write(contentBytes.Length);
                writer.Write(0);
                writer.Write(contentBytes);
            }
            return chunk.ToArray();
        }

        private static int ReadNonNegativeSize(BinaryReader reader, string chunk)
        {
            int value = reader.ReadInt32();
            if (value < 0) throw new InvalidDataException($"Chunk {chunk} contains a negative size.");
            return value;
        }

        private static void EnsureRemaining(Stream stream, long bytes, string chunk)
        {
            if (bytes < 0 || stream.Length - stream.Position < bytes)
                throw new InvalidDataException($"Chunk {chunk} is truncated.");
        }

        private static string ReadId(BinaryReader reader) =>
            Encoding.ASCII.GetString(reader.ReadBytes(4));

        private static void WriteId(BinaryWriter writer, string id)
        {
            if (id == null || id.Length != 4)
                throw new ArgumentException("A VOX chunk identifier must contain four characters.");
            writer.Write(Encoding.ASCII.GetBytes(id));
        }
    }

    internal static class VoxelSemanticTransport
    {
        public static bool TryCreateMetadata(IReadOnlyList<VoxelSemanticSlotMetadata> slots,
            VoxelColorPalette colorPalette, VoxelSurfacePalette surfacePalette,
            out VoxelSemanticMetadata metadata, out string error)
        {
            return TryCreateMetadata(slots, colorPalette, surfacePalette, null,
                out metadata, out error);
        }

        public static bool TryCreateMetadata(IReadOnlyList<VoxelSemanticSlotMetadata> slots,
            VoxelColorPalette colorPalette, VoxelSurfacePalette surfacePalette,
            VoxelColorMappingProfile mappingProfile,
            out VoxelSemanticMetadata metadata, out string error)
        {
            metadata = null;
            if (!TryValidateBindings(slots, colorPalette, surfacePalette, out error)) return false;
            if (mappingProfile != null)
            {
                if (!mappingProfile.TryValidate(out error)) return false;
                if (mappingProfile.ColorPalette != colorPalette)
                {
                    error = "The mapping profile uses a different color palette.";
                    return false;
                }
            }
            string colorPath = AssetDatabase.GetAssetPath(colorPalette);
            string surfacePath = AssetDatabase.GetAssetPath(surfacePalette);
            if (string.IsNullOrEmpty(colorPath) || string.IsNullOrEmpty(surfacePath))
            {
                error = "Global palettes must be saved as assets before binding a VOX document.";
                return false;
            }
            string colorGuid = AssetDatabase.AssetPathToGUID(colorPath);
            string surfaceGuid = AssetDatabase.AssetPathToGUID(surfacePath);
            if (string.IsNullOrEmpty(colorGuid) || string.IsNullOrEmpty(surfaceGuid))
            {
                error = "The global palettes do not have valid asset GUIDs.";
                return false;
            }
            if (!VoxelPaletteLutBuilder.TryBuildColorPixels(
                    colorPalette, out _, out string colorHash, out error) ||
                !VoxelPaletteLutBuilder.TryBuildSurfacePixels(
                    surfacePalette, out _, out string surfaceHash, out error))
                return false;

            VoxelSemanticSlotMetadata[] copy = slots
                .OrderBy(entry => entry.slot)
                .Select(CloneSlot)
                .ToArray();
            string profilePath = mappingProfile == null
                ? null
                : AssetDatabase.GetAssetPath(mappingProfile);
            metadata = new VoxelSemanticMetadata
            {
                formatVersion = 1,
                colorPaletteGuid = colorGuid,
                colorPaletteAssetPath = colorPath,
                colorPaletteHash = colorHash,
                colorMappingProfileGuid = string.IsNullOrEmpty(profilePath)
                    ? null
                    : AssetDatabase.AssetPathToGUID(profilePath),
                colorMappingProfileAssetPath = profilePath,
                surfacePaletteGuid = surfaceGuid,
                surfacePaletteAssetPath = surfacePath,
                surfacePaletteHash = surfaceHash,
                slotTableHash = ComputeSlotTableHash(copy),
                slots = copy
            };
            error = null;
            return true;
        }

        public static bool TryResolveSlotSemantics(VoxelSemanticVoxDocument document,
            VoxelSemanticMetadata metadata, VoxelColorPalette colorPalette,
            VoxelSurfacePalette surfacePalette, out ushort[] slotToSemantic,
            out string warning, out string error)
        {
            slotToSemantic = null;
            warning = null;
            if (document == null)
            {
                error = "The VOX document could not be read.";
                return false;
            }
            if (metadata == null || metadata.formatVersion != 1 || metadata.slots == null)
            {
                error = "The sidecar contains no compatible semantic metadata.";
                return false;
            }
            if (document.HasUnsupportedIndexMap)
            {
                error = "The file contains an IMAP. Its remapping direction has not been " +
                        "validated for semantic transport.";
                return false;
            }
            if (!TryValidateBindings(metadata.slots, colorPalette, surfacePalette, out error))
                return false;
            if (!string.Equals(metadata.slotTableHash, ComputeSlotTableHash(metadata.slots),
                    StringComparison.Ordinal))
            {
                error = "The semantic table hash does not match the sidecar.";
                return false;
            }

            var bySlot = new Dictionary<int, VoxelSemanticSlotMetadata>();
            foreach (VoxelSemanticSlotMetadata entry in metadata.slots)
                bySlot.Add(entry.slot, entry);

            slotToSemantic = new ushort[256];
            foreach (byte slot in document.UsedSlots)
            {
                if (!bySlot.TryGetValue(slot, out VoxelSemanticSlotMetadata entry))
                {
                    error = $"Used slot {slot} is missing from the sidecar semantic table.";
                    slotToSemantic = null;
                    return false;
                }
                Color32 actual = document.Palette[slot - 1];
                if (!entry.displayColor.Equals(actual))
                {
                    error = $"The color of slot {slot} changed since its last semantic binding.";
                    slotToSemantic = null;
                    return false;
                }
                slotToSemantic[slot] = VoxelSemanticEncoding.Pack(entry.colorId, entry.surfaceId);
            }

            var warnings = new List<string>();
            if (VoxelPaletteLutBuilder.TryBuildColorPixels(
                    colorPalette, out _, out string colorHash, out _) &&
                !string.Equals(colorHash, metadata.colorPaletteHash, StringComparison.Ordinal))
                warnings.Add("the global color palette changed");
            if (VoxelPaletteLutBuilder.TryBuildSurfacePixels(
                    surfacePalette, out _, out string surfaceHash, out _) &&
                !string.Equals(surfaceHash, metadata.surfacePaletteHash, StringComparison.Ordinal))
                warnings.Add("the global surface palette changed");
            bool ambiguousDuplicateColors = metadata.slots
                .GroupBy(entry => entry.displayColor)
                .Any(group => group.Select(entry =>
                        VoxelSemanticEncoding.Pack(entry.colorId, entry.surfaceId))
                    .Distinct().Count() > 1);
            if (ambiguousDuplicateColors)
                warnings.Add("identical RGB slots use different surfaces; " +
                             "slot reordering when saving in MagicaVoxel has not been validated");
            warning = warnings.Count > 0 ? string.Join("; ", warnings) : null;
            error = null;
            return true;
        }

        public static bool TryLoadPalettes(VoxelSemanticMetadata metadata,
            out VoxelColorPalette colorPalette, out VoxelSurfacePalette surfacePalette,
            out string error)
        {
            colorPalette = null;
            surfacePalette = null;
            if (metadata == null)
            {
                error = "The sidecar contains no semantic metadata.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(metadata.colorPaletteGuid) ||
                string.IsNullOrWhiteSpace(metadata.surfacePaletteGuid))
            {
                error = "The sidecar contains no valid global palette GUIDs. Bind the VOX document to its palettes again.";
                return false;
            }
            string colorPath = AssetDatabase.GUIDToAssetPath(metadata.colorPaletteGuid);
            string surfacePath = AssetDatabase.GUIDToAssetPath(metadata.surfacePaletteGuid);
            if (string.IsNullOrWhiteSpace(colorPath) || string.IsNullOrWhiteSpace(surfacePath))
            {
                error = "A global palette referenced by GUID is missing. Restore the palette or bind the VOX document again.";
                return false;
            }
            colorPalette = AssetDatabase.LoadAssetAtPath<VoxelColorPalette>(colorPath);
            surfacePalette = AssetDatabase.LoadAssetAtPath<VoxelSurfacePalette>(surfacePath);
            if (colorPalette == null || surfacePalette == null)
            {
                error = "The global palettes referenced by the sidecar could not be loaded.";
                return false;
            }
            error = null;
            return true;
        }

        public static string ComputeSlotTableHash(
            IReadOnlyList<VoxelSemanticSlotMetadata> slots)
        {
            var text = new StringBuilder("voxel-semantic-slots-v1");
            foreach (VoxelSemanticSlotMetadata entry in slots.OrderBy(value => value.slot))
            {
                Color32 color = entry.displayColor;
                text.Append('|').Append(entry.slot)
                    .Append(':').Append(entry.colorId)
                    .Append(':').Append(entry.surfaceId)
                    .Append(':').Append(color.r.ToString("X2"))
                    .Append(color.g.ToString("X2"))
                    .Append(color.b.ToString("X2"))
                    .Append(color.a.ToString("X2"));
            }
            return Hash128.Compute(text.ToString()).ToString();
        }

        private static bool TryValidateBindings(IReadOnlyList<VoxelSemanticSlotMetadata> slots,
            VoxelColorPalette colorPalette, VoxelSurfacePalette surfacePalette, out string error)
        {
            if (slots == null || slots.Count == 0 || slots.Count > byte.MaxValue)
            {
                error = "The semantic table must contain between 1 and 255 slots.";
                return false;
            }
            if (colorPalette == null)
            {
                error = "The global color palette is not assigned.";
                return false;
            }
            if (!colorPalette.TryValidate(out error)) return false;
            if (surfacePalette == null)
            {
                error = "The global surface palette is not assigned.";
                return false;
            }
            if (!surfacePalette.TryValidate(out error)) return false;

            var seen = new HashSet<int>();
            var semanticPairs = new HashSet<ushort>();
            foreach (VoxelSemanticSlotMetadata entry in slots)
            {
                if (entry == null || entry.slot < 1 || entry.slot > byte.MaxValue)
                {
                    error = "The semantic table contains a slot outside 1..255.";
                    return false;
                }
                if (!seen.Add(entry.slot))
                {
                    error = $"Slot {entry.slot} is duplicated in the semantic table.";
                    return false;
                }
                if (!colorPalette.TryGetColor(entry.colorId, out _))
                {
                    error = $"Slot {entry.slot} references an unknown ColorID: {entry.colorId}.";
                    return false;
                }
                if (!surfacePalette.TryGetSurface(entry.surfaceId, out _))
                {
                    error = $"Slot {entry.slot} references an unknown SurfaceID: {entry.surfaceId}.";
                    return false;
                }
                ushort semanticId = VoxelSemanticEncoding.Pack(entry.colorId, entry.surfaceId);
                if (!semanticPairs.Add(semanticId))
                {
                    error = $"ColorID {entry.colorId} + SurfaceID {entry.surfaceId} " +
                            "appears in more than one slot.";
                    return false;
                }
            }
            error = null;
            return true;
        }

        private static VoxelSemanticSlotMetadata CloneSlot(VoxelSemanticSlotMetadata source) => new()
        {
            slot = source.slot,
            colorId = source.colorId,
            surfaceId = source.surfaceId,
            displayColor = source.displayColor
        };
    }

    internal static class VoxelSemanticQuantizer
    {
        public static QuantizedVoxels Quantize(VoxelGrid grid,
            VoxelColorPalette colorPalette, VoxelSurfacePalette surfacePalette)
        {
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            if (!grid.IsSemantic)
                throw new ArgumentException("The grid contains no semantic IDs.", nameof(grid));
            if (colorPalette == null)
                throw new InvalidDataException("The color palette is not assigned.");
            if (!colorPalette.TryValidate(out string colorError))
                throw new InvalidDataException(colorError);
            if (surfacePalette == null)
                throw new InvalidDataException("The surface palette is not assigned.");
            if (!surfacePalette.TryValidate(out string surfaceError))
                throw new InvalidDataException(surfaceError);

            var frequencies = new Dictionary<ushort, long>();
            for (int i = 0; i < grid.Occupied.Length; i++)
            {
                if (!grid.Occupied[i]) continue;
                ushort semanticId = grid.SemanticIds[i];
                frequencies.TryGetValue(semanticId, out long count);
                frequencies[semanticId] = count + 1;
            }
            if (frequencies.Count == 0)
                throw new InvalidOperationException("There are no voxels to export.");
            if (frequencies.Count > byte.MaxValue)
                throw new InvalidDataException(
                    $"The volume uses {frequencies.Count} ColorID + SurfaceID pairs; a VOX document supports at most 255.");

            ushort[] ordered = frequencies.OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key)
                .Select(pair => pair.Key)
                .ToArray();
            var semanticToSlot = new Dictionary<ushort, byte>(ordered.Length);
            var palette = new Color32[ordered.Length];
            var slots = new VoxelSemanticSlotMetadata[ordered.Length];
            for (int i = 0; i < ordered.Length; i++)
            {
                ushort semanticId = ordered[i];
                int colorId = VoxelSemanticEncoding.ColorId(semanticId);
                int surfaceId = VoxelSemanticEncoding.SurfaceId(semanticId);
                if (!colorPalette.TryGetColor(colorId, out Color32 color))
                    throw new InvalidDataException($"The volume references an unknown ColorID: {colorId}.");
                if (!surfacePalette.TryGetSurface(surfaceId, out _))
                    throw new InvalidDataException($"The volume references an unknown SurfaceID: {surfaceId}.");
                byte slot = (byte)(i + 1);
                semanticToSlot.Add(semanticId, slot);
                palette[i] = color;
                slots[i] = new VoxelSemanticSlotMetadata
                {
                    slot = slot,
                    colorId = colorId,
                    surfaceId = surfaceId,
                    displayColor = color
                };
            }

            var indices = new byte[grid.Occupied.Length];
            for (int i = 0; i < indices.Length; i++)
            {
                if (grid.Occupied[i]) indices[i] = semanticToSlot[grid.SemanticIds[i]];
            }
            return new QuantizedVoxels(indices, palette, slots);
        }
    }

    internal static class VoxelSemanticBindingService
    {
        public static bool TryBind(string voxAssetPath,
            IReadOnlyList<VoxelSemanticSlotMetadata> bindings,
            VoxelColorPalette colorPalette, VoxelSurfacePalette surfacePalette,
            VoxelColorMappingProfile mappingProfile, out string message)
        {
            message = null;
            if (!VoxelImporterIntegration.TryLoadMetadata(
                    voxAssetPath, out VoxelBridgeMetadata metadata, out message))
                return false;

            string absoluteVoxPath = VoxelLodPipeline.AssetPathToAbsolute(voxAssetPath);
            string metadataAssetPath = VoxelImporterIntegration.GetMetadataAssetPath(voxAssetPath);
            string absoluteMetadataPath = VoxelLodPipeline.AssetPathToAbsolute(metadataAssetPath);
            byte[] originalVox = null;
            string originalMetadata = null;
            bool wroteFiles = false;
            try
            {
                originalVox = File.ReadAllBytes(absoluteVoxPath);
                originalMetadata = File.ReadAllText(absoluteMetadataPath);
                VoxelSemanticVoxDocument document = VoxelSemanticVoxDocument.Parse(originalVox);
                if (!CoversUsedSlots(document.UsedSlots, bindings, out message)) return false;
                if (!VoxelSemanticTransport.TryCreateMetadata(
                        bindings, colorPalette, surfacePalette, mappingProfile,
                        out VoxelSemanticMetadata semantic, out message))
                    return false;

                byte[] updatedVox = document.BuildSemanticBytes(semantic.slots);
                metadata.formatVersion = 4;
                metadata.semantic = semantic;
                string updatedMetadata = JsonUtility.ToJson(metadata, true);

                VoxelSemanticVoxDocument checkDocument = VoxelSemanticVoxDocument.Parse(updatedVox);
                if (!VoxelSemanticTransport.TryResolveSlotSemantics(
                        checkDocument, semantic, colorPalette, surfacePalette,
                        out _, out string warning, out string validationError))
                    throw new InvalidDataException(validationError);

                wroteFiles = true;
                WriteAtomic(absoluteVoxPath, updatedVox);
                WriteAtomic(absoluteMetadataPath, Encoding.UTF8.GetBytes(updatedMetadata));

                AssetDatabase.ImportAsset(metadataAssetPath, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(voxAssetPath, ImportAssetOptions.ForceSynchronousImport);
                string importerMessage = null;
                if (VoxelImporterIntegration.IsInstalled)
                    VoxelImporterIntegration.ApplyAndReimport(
                        voxAssetPath, out importerMessage, forceReimport: true);
                message = "Semantic binding saved.";
                if (!string.IsNullOrEmpty(warning)) message += " Warning: " + warning + ".";
                if (!string.IsNullOrEmpty(importerMessage)) message += " " + importerMessage;
                return true;
            }
            catch (Exception exception)
            {
                if (wroteFiles && originalVox != null && originalMetadata != null)
                {
                    try
                    {
                        WriteAtomic(absoluteVoxPath, originalVox);
                        WriteAtomic(absoluteMetadataPath, Encoding.UTF8.GetBytes(originalMetadata));
                        AssetDatabase.ImportAsset(metadataAssetPath, ImportAssetOptions.ForceSynchronousImport);
                        AssetDatabase.ImportAsset(voxAssetPath, ImportAssetOptions.ForceSynchronousImport);
                    }
                    catch (Exception rollbackException)
                    {
                        Debug.LogException(rollbackException);
                    }
                }
                message = "Could not save semantic binding: " + exception.Message;
                return false;
            }
        }

        private static bool CoversUsedSlots(IReadOnlyList<byte> usedSlots,
            IReadOnlyList<VoxelSemanticSlotMetadata> bindings, out string error)
        {
            var bound = new HashSet<int>(
                (bindings ?? Array.Empty<VoxelSemanticSlotMetadata>())
                .Where(entry => entry != null)
                .Select(entry => entry.slot));
            foreach (byte slot in usedSlots)
            {
                if (bound.Contains(slot)) continue;
                error = $"Used slot {slot} has no assigned ColorID and SurfaceID.";
                return false;
            }
            error = null;
            return true;
        }

        private static void WriteAtomic(string path, byte[] bytes)
        {
            string tempPath = path + ".voxelbridge.tmp";
            if (File.Exists(tempPath)) File.Delete(tempPath);
            File.WriteAllBytes(tempPath, bytes);
            try
            {
                if (File.Exists(path))
                    File.Replace(tempPath, path, null);
                else
                    File.Move(tempPath, path);
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }
    }
}
