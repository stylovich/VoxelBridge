using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    internal static class VoxelSurfaceEditStore
    {
        [Serializable]
        private sealed class Transaction
        {
            public int version = 1;
            public string sourceGuid;
            public string sourcePath;
            public string oldVoxHash;
            public string oldMetadataHash;
            public string newVoxHash;
            public string newMetadataHash;
        }

        private static string Folder(string path)
        {
            string guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid)) throw new InvalidOperationException("Import the VOX asset before saving surface edits.");
            return Path.Combine("Library", "VoxelBridgeSurfaceEdits", guid);
        }

        internal static bool HasPending(string path)
        {
            string guid = AssetDatabase.AssetPathToGUID(path);
            return !string.IsNullOrEmpty(guid) && File.Exists(Path.Combine("Library", "VoxelBridgeSurfaceEdits", guid, "transaction.json"));
        }

        // Tests can inject an interruption between the two file replacements.
        internal static string Save(string path, VoxelSurfaceEdit edit, Action afterFirstReplace = null)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Surface editing requires Edit Mode.");
            if (edit == null || edit.AssetPath != path) throw new InvalidOperationException("The edit buffer belongs to a different source.");
            if (HasPending(path)) throw new InvalidOperationException("Recover the interrupted surface save first.");
            edit.BuildOutput(out var vox, out var sidecar);
            if (edit.PendingCells == 0) return null;
            string source = VoxelLodPipeline.AssetPathToAbsolute(path);
            string metaPath = VoxelImporterIntegration.GetMetadataAssetPath(path);
            string meta = VoxelLodPipeline.AssetPathToAbsolute(metaPath);
            string folder = Folder(path);
            Directory.CreateDirectory(folder);
            byte[] oldVox = File.ReadAllBytes(source), oldMetadata = File.ReadAllBytes(meta);
            var transaction = new Transaction
            {
                sourceGuid = AssetDatabase.AssetPathToGUID(path), sourcePath = path,
                oldVoxHash = Hash(oldVox), oldMetadataHash = Hash(oldMetadata),
                newVoxHash = Hash(vox), newMetadataHash = Hash(sidecar)
            };
            AssetDatabase.DisallowAutoRefresh();
            AssetDatabase.StartAssetEditing();
            try
            {
                edit.ValidateUnchangedSources();
                WriteAtomic(Path.Combine(folder, "original.vox"), oldVox);
                WriteAtomic(Path.Combine(folder, "original.json"), oldMetadata);
                WriteAtomic(Path.Combine(folder, "transaction.json"), System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(transaction)));
                try
                {
                    edit.ValidateUnchangedSources();
                    WriteAtomic(source, vox);
                    afterFirstReplace?.Invoke();
                    WriteAtomic(meta, sidecar);
                    if (Hash(File.ReadAllBytes(source)) != transaction.newVoxHash || Hash(File.ReadAllBytes(meta)) != transaction.newMetadataHash)
                        throw new IOException("Saved surface data did not match the candidate files.");
                    File.Delete(Path.Combine(folder, "transaction.json"));
                    RemoveBackups(folder);
                }
                catch
                {
                    RecoverFiles(path);
                    throw;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.AllowAutoRefresh();
            }
            try { Import(path, metaPath); return null; }
            catch (Exception exception) { return "Surface files were saved, but Unity import failed. Reimport the source before rebuilding. " + exception.Message; }
        }

        internal static void Recover(string path)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Recovery requires Edit Mode.");
            AssetDatabase.DisallowAutoRefresh();
            AssetDatabase.StartAssetEditing();
            try { RecoverFiles(path); }
            finally { AssetDatabase.StopAssetEditing(); AssetDatabase.AllowAutoRefresh(); }
            Import(path, VoxelImporterIntegration.GetMetadataAssetPath(path));
        }

        private static void RecoverFiles(string path)
        {
            string folder = Folder(path), journal = Path.Combine(folder, "transaction.json");
            if (!File.Exists(journal)) return;
            var transaction = JsonUtility.FromJson<Transaction>(File.ReadAllText(journal));
            if (transaction == null || transaction.version != 1 || transaction.sourcePath != path ||
                transaction.sourceGuid != AssetDatabase.AssetPathToGUID(path))
                throw new InvalidDataException("Surface recovery journal does not match the source. Keep the recovery files for manual review.");
            byte[] originalVox = File.ReadAllBytes(Path.Combine(folder, "original.vox"));
            byte[] originalMetadata = File.ReadAllBytes(Path.Combine(folder, "original.json"));
            if (Hash(originalVox) != transaction.oldVoxHash || Hash(originalMetadata) != transaction.oldMetadataHash)
                throw new InvalidDataException("Surface recovery backups are damaged. No source was changed.");
            string source = VoxelLodPipeline.AssetPathToAbsolute(path);
            string meta = VoxelLodPipeline.AssetPathToAbsolute(VoxelImporterIntegration.GetMetadataAssetPath(path));
            string currentVox = Hash(File.ReadAllBytes(source)), currentMetadata = Hash(File.ReadAllBytes(meta));
            if ((currentVox != transaction.oldVoxHash && currentVox != transaction.newVoxHash) ||
                (currentMetadata != transaction.oldMetadataHash && currentMetadata != transaction.newMetadataHash))
                throw new InvalidOperationException("The source changed after the interrupted save. Recovery will not overwrite external edits.");
            WriteAtomic(source, originalVox);
            WriteAtomic(meta, originalMetadata);
            File.Delete(journal);
            RemoveBackups(folder);
        }

        private static void RemoveBackups(string folder)
        {
            // Only files owned by this operation; never recursively delete an asset directory.
            try
            {
                File.Delete(Path.Combine(folder, "original.vox"));
                File.Delete(Path.Combine(folder, "original.json"));
            }
            catch (IOException exception) { Debug.LogWarning("Surface data is consistent, but recovery backup cleanup failed: " + exception.Message); }
            catch (UnauthorizedAccessException exception) { Debug.LogWarning("Surface data is consistent, but recovery backup cleanup was denied: " + exception.Message); }
        }

        private static void Import(string path, string metadataPath)
        {
            AssetDatabase.ImportAsset(metadataPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        }

        private static void WriteAtomic(string path, byte[] bytes)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(bytes));
        }
    }
}
