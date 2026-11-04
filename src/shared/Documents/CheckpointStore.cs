using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RvtMcp.Plugin.Documents
{
    public sealed class CheckpointListItem
    {
        public CheckpointEntry Entry { get; set; }
        public bool Exists { get; set; }
        public long? Bytes { get; set; }
    }

    public sealed class CheckpointListResult
    {
        public List<CheckpointListItem> Items { get; set; }
        public List<string> UntrackedFiles { get; set; }
        public string IndexStatus { get; set; }
    }

    public sealed class CheckpointPruneResult
    {
        public string Error { get; set; }
        public List<string> DeletedIds { get; set; } = new List<string>();
        public List<string> MissingIds { get; set; } = new List<string>();
        public List<string> FailedIds { get; set; } = new List<string>();
    }

    public sealed class CheckpointRestoreResult
    {
        public string Error { get; set; }
        public string Path { get; set; }
    }

    public sealed class CheckpointStore
    {
        private readonly string _modelPath;
        private readonly string _modelFile;
        private readonly string _folder;
        private readonly string _index;

        public CheckpointStore(string modelPath)
        {
            _modelPath = Path.GetFullPath(modelPath);
            _modelFile = Path.GetFileName(_modelPath);
            _folder = Path.Combine(Path.GetDirectoryName(_modelPath), Path.GetFileNameWithoutExtension(_modelPath) + ".checkpoints");
            _index = Path.Combine(_folder, "index.json");
        }

        public CheckpointListResult List()
        {
            var index = CheckpointIndex.Load(_index, _modelFile);
            // Validate every indexed file and the checkpoint directory before exposing paths.
            CheckpointPlanner.Plan(_folder, index.Checkpoints, 100);
            var items = index.Checkpoints.OrderByDescending(c => DateTimeOffset.Parse(c.CreatedUtc, CultureInfo.InvariantCulture))
                .Select(entry =>
                {
                    var file = Path.Combine(_folder, entry.File);
                    var exists = File.Exists(file);
                    return new CheckpointListItem { Entry = entry, Exists = exists, Bytes = exists ? (long?)new FileInfo(file).Length : null };
                }).ToList();
            var tracked = new HashSet<string>(index.Checkpoints.Select(c => c.File), StringComparer.OrdinalIgnoreCase) { "index.json" };
            var untracked = Directory.Exists(_folder)
                ? Directory.GetFiles(_folder).Select(Path.GetFileName).Where(name => !tracked.Contains(name)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
            return new CheckpointListResult { Items = items, UntrackedFiles = untracked, IndexStatus = index.Exists ? "ok" : "not_created" };
        }

        public CheckpointPrunePlan Plan(int keepLast)
        {
            var index = CheckpointIndex.Load(_index, _modelFile);
            return CheckpointPlanner.Plan(_folder, index.Checkpoints, keepLast);
        }

        public CheckpointPruneResult Prune(int keepLast, string approvedHash)
        {
            var result = new CheckpointPruneResult();
            if (string.IsNullOrWhiteSpace(approvedHash)) { result.Error = "PLAN_HASH_REQUIRED"; return result; }
            var pendingMissing = new List<string>();
            try
            {
                CheckpointIndex.Update(_index, _modelFile, data =>
                {
                    var plan = CheckpointPlanner.Plan(_folder, data.Checkpoints, keepLast);
                    if (!string.Equals(plan.PlanHash, approvedHash, StringComparison.OrdinalIgnoreCase)) throw new PlanChangedException();
                    if (!data.Exists) return false;
                    foreach (var missing in plan.MissingEntries)
                    {
                        data.Checkpoints.RemoveAll(c => c.Id == missing.Id);
                        pendingMissing.Add(missing.Id);
                    }
                    foreach (var candidate in plan.DeleteFiles)
                    {
                        var entry = data.Checkpoints.Single(c => c.Id == candidate.Id);
                        try
                        {
                            // Revalidate containment, reparse status and approved file metadata immediately before deletion.
                            CheckpointPlanner.Plan(_folder, new[] { entry }, 1);
                            var info = new FileInfo(candidate.FullPath);
                            if (!info.Exists || info.Length != candidate.Bytes || info.LastWriteTimeUtc != candidate.LastWriteUtc)
                            {
                                result.FailedIds.Add(candidate.Id);
                                continue;
                            }
                            File.Delete(candidate.FullPath);
                            if (File.Exists(candidate.FullPath)) { result.FailedIds.Add(candidate.Id); continue; }
                            data.Checkpoints.RemoveAll(c => c.Id == candidate.Id);
                            result.DeletedIds.Add(candidate.Id);
                        }
                        catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                        {
                            result.FailedIds.Add(candidate.Id);
                        }
                    }
                    return result.DeletedIds.Count != 0 || pendingMissing.Count != 0;
                });
                result.MissingIds.AddRange(pendingMissing);
            }
            catch (PlanChangedException) { result.Error = "PLAN_CHANGED"; }
            catch (TimeoutException) { result.Error = "CHECKPOINT_INDEX_BUSY"; }
            catch (InvalidDataException) { result.Error = result.DeletedIds.Count != 0 || pendingMissing.Count != 0 ? "CHECKPOINT_INDEX_WRITE_FAILED" : "CHECKPOINT_INDEX_UNREADABLE"; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                result.Error = result.DeletedIds.Count != 0 || pendingMissing.Count != 0
                    ? "CHECKPOINT_INDEX_WRITE_FAILED" : "CHECKPOINT_INDEX_UNREADABLE";
            }
            return result;
        }

        public CheckpointRestoreResult Restore(string checkpointId)
        {
            if (string.IsNullOrWhiteSpace(checkpointId)) return new CheckpointRestoreResult { Error = "CHECKPOINT_ID_REQUIRED" };
            var index = CheckpointIndex.Load(_index, _modelFile);
            var entry = index.Checkpoints.SingleOrDefault(c => c.Id == checkpointId);
            if (entry == null) return new CheckpointRestoreResult { Error = "CHECKPOINT_NOT_FOUND" };
            CheckpointPlanner.Plan(_folder, new[] { entry }, 1);
            var source = Path.Combine(_folder, entry.File);
            if (!File.Exists(source)) return new CheckpointRestoreResult { Error = "CHECKPOINT_FILE_MISSING" };
            var target = Path.Combine(Path.GetDirectoryName(_modelPath),
                Path.GetFileNameWithoutExtension(_modelPath) + "-restored-" + entry.Id + Path.GetExtension(_modelPath));
            if (File.Exists(target) || Directory.Exists(target)) return new CheckpointRestoreResult { Error = "RESTORE_TARGET_EXISTS" };
            try { CheckpointFiles.CopyNew(source, target); }
            catch (IOException ex) when ((ex.HResult & 0xffff) == 80 || (ex.HResult & 0xffff) == 183 || Directory.Exists(target))
            {
                return new CheckpointRestoreResult { Error = "RESTORE_TARGET_EXISTS" };
            }
            catch (UnauthorizedAccessException) when (Directory.Exists(target))
            {
                return new CheckpointRestoreResult { Error = "RESTORE_TARGET_EXISTS" };
            }
            return new CheckpointRestoreResult { Path = target };
        }

        private sealed class PlanChangedException : Exception { }
    }
}
