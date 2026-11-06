using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Documents;

namespace RvtMcp.Plugin.Handlers
{
    public sealed class SaveCheckpointHandler : IRevitCommand
    {
        public string Name => "save_checkpoint";
        public string Description => "Save if needed and create a verified indexed checkpoint of a local document.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""label"":{""type"":""string""},""reason"":{""type"":""string""},""milestone"":{""type"":""boolean""},""allow_format_upgrade"":{""type"":""boolean""}},""required"":[""label""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var args = JObject.Parse(paramsJson);
            var label = args.Value<string>("label");
            var reason = args.Value<string>("reason");
            if (string.IsNullOrWhiteSpace(label) || label.Length > 80) return CommandResult.Fail("label must contain 1-80 characters.");
            if (reason != null && reason.Length > 500) return CommandResult.Fail("reason must not exceed 500 characters.");
            var prep = DocumentLifecycleSupport.Prepare(app, app.ActiveUIDocument?.Document, args.Value<bool?>("allow_format_upgrade") ?? false, true);
            if (prep.Error != null) return CommandResult.Fail(prep.Error);
            var context = prep.Context;
            var folder = DocumentLifecycleSupport.CheckpointDirectory(context.Path);
            var indexPath = DocumentLifecycleSupport.IndexPath(context.Path);
            var modelFile = Path.GetFileName(context.Path);
            CheckpointIndexData index;
            try
            {
                index = CheckpointIndex.Load(indexPath, modelFile);
                if (Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                    return CommandResult.Fail("CHECKPOINT_INDEX_UNREADABLE: checkpoint directory is a reparse point.");
            }
            catch (TimeoutException) { return CommandResult.Fail("CHECKPOINT_INDEX_BUSY"); }
            catch (InvalidDataException) { return CommandResult.Fail("CHECKPOINT_INDEX_UNREADABLE"); }
            catch (IOException) { return CommandResult.Fail("CHECKPOINT_INDEX_UNREADABLE"); }
            catch (UnauthorizedAccessException) { return CommandResult.Fail("CHECKPOINT_INDEX_UNREADABLE"); }

            try
            {
                if (!CheckpointSpace.HasEnough(new FileInfo(context.Path).Length, CheckpointSpace.AvailableBytes(context.Path)))
                    return CommandResult.Fail("INSUFFICIENT_DISK_SPACE");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
            {
                return CommandResult.Fail("FREE_SPACE_UNAVAILABLE");
            }

            DocumentLifecycleSupport.SaveOutcome saved = null;
            if (context.Document.IsModified) saved = DocumentLifecycleSupport.Save(app, prep);
            var savedFirst = saved?.Saved == true;
            var current = app.ActiveUIDocument?.Document;
            if (current == null || !current.Equals(context.Document)) return Failure("DOCUMENT_CHANGED", savedFirst, null);
            DocumentLifecycleSupport.Versions versions;
            try { versions = DocumentLifecycleSupport.ReadVersions(current, context.Path, DocumentLifecycleSupport.HostYear(app)); }
            catch { return Failure("FILE_VERSION_UNAVAILABLE", savedFirst, null); }
            if (versions.Status == "changed_on_disk") return Failure("FILE_CHANGED_ON_DISK", savedFirst, null);
            try
            {
                if (!CheckpointSpace.HasEnough(new FileInfo(context.Path).Length, CheckpointSpace.AvailableBytes(context.Path)))
                    return Failure("INSUFFICIENT_DISK_SPACE", savedFirst, null);
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
            {
                return Failure("CHECKPOINT_COPY_FAILED", savedFirst, null);
            }

            CheckpointName name = null;
            string target = null;
            var createdLocal = DateTimeOffset.Now;
            try
            {
                for (var attempt = 0; attempt < 10000; attempt++)
                {
                    name = CheckpointNaming.Next(folder, Path.GetExtension(context.Path), createdLocal, index.Checkpoints);
                    target = Path.Combine(folder, name.File);
                    try { CheckpointFiles.CopyNew(context.Path, target); break; }
                    catch (IOException ex) when ((ex.HResult & 0xffff) == 80 || (ex.HResult & 0xffff) == 183)
                    {
                        target = null;
                    }
                }
                if (target == null) return Failure("CHECKPOINT_COPY_FAILED: no free checkpoint name.", savedFirst, null);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                return Failure("CHECKPOINT_COPY_FAILED", savedFirst, null, target);
            }

            Tuple<int, Guid> sourceVersion;
            long copiedBytes;
            try
            {
                sourceVersion = DocumentLifecycleSupport.DiskVersion(context.Path);
                var copiedVersion = DocumentLifecycleSupport.DiskVersion(target);
                copiedBytes = new FileInfo(target).Length;
                if (new FileInfo(context.Path).Length != copiedBytes
                    || sourceVersion.Item1 != copiedVersion.Item1 || sourceVersion.Item2 != copiedVersion.Item2)
                    return Failure("CHECKPOINT_VERIFY_FAILED", savedFirst, Cleanup(target), target);
            }
            catch
            {
                return Failure("CHECKPOINT_VERIFY_FAILED", savedFirst, Cleanup(target), target);
            }

            CheckpointEntry entry;
            try
            {
                entry = new CheckpointEntry
                {
                    Id = name.Id, File = name.File, Label = label, Reason = reason,
                    Milestone = args.Value<bool?>("milestone") ?? false,
                    CreatedUtc = createdLocal.ToUniversalTime().ToString("o"), Bytes = copiedBytes,
                    Source = new CheckpointSource { NumberOfSaves = sourceVersion.Item1, VersionGuid = sourceVersion.Item2.ToString() },
                    RevitYear = DocumentLifecycleSupport.HostYear(app), User = app.Application.Username ?? ""
                };
            }
            catch { return Failure("CHECKPOINT_INDEX_WRITE_FAILED", savedFirst, Cleanup(target), target); }
            CheckpointIndexData updated;
            try { updated = CheckpointIndex.Update(indexPath, modelFile, data => data.Checkpoints.Add(entry)); }
            catch (TimeoutException) { return Failure("CHECKPOINT_INDEX_BUSY", savedFirst, Cleanup(target), target); }
            catch { return Failure("CHECKPOINT_INDEX_WRITE_FAILED", savedFirst, Cleanup(target), target); }
            var nonMilestones = updated.Checkpoints.Count(c => !c.Milestone);
            var warnings = saved?.Warnings ?? new List<string>();
            return CommandResult.Ok(new
            {
                checkpoint = entry,
                saved_first = savedFirst,
                totals = new { count = updated.Checkpoints.Count, total_bytes = updated.Checkpoints.Sum(c => c.Bytes) },
                prune_hint = nonMilestones > 5 ? "More than 5 non-milestone checkpoints; preview pruning if the user approves." : null,
                warnings
            });
        }

        private static bool Cleanup(string path)
        {
            try { File.Delete(path); return !File.Exists(path); }
            catch { return false; }
        }

        private static CommandResult Failure(string code, bool savedFirst, bool? copyRemoved, string checkpointPath = null) => new CommandResult
        {
            Success = false,
            Error = code,
            Data = new { saved_first = savedFirst, checkpoint_copy_removed = copyRemoved, checkpoint_path = checkpointPath }
        };
    }
}
