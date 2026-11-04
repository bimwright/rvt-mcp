using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RvtMcp.Plugin.Documents;
using RvtMcp.Plugin.Failures;

namespace RvtMcp.Plugin.Handlers
{
    internal static class DocumentLifecycleSupport
    {
        internal sealed class Context
        {
            internal Document Document;
            internal string Path;
            internal string Mode;
            internal string CentralPath;
            internal string ModeError;
        }

        internal sealed class Versions
        {
            internal int MemorySaves;
            internal Guid MemoryGuid;
            internal int DiskSaves;
            internal Guid DiskGuid;
            internal string Status;
            internal string SavedInVersion;
            internal bool? UpgradeOnSave;
        }

        internal sealed class Preparation
        {
            internal Context Context;
            internal Versions Versions;
            internal string Error;
        }

        internal sealed class SaveOutcome
        {
            internal bool Saved;
            internal string Reason;
            internal string Path;
            internal string Mode;
            internal int? NumberOfSaves;
            internal int? BackupCount;
            internal int? MinBackupsEnsured;
            internal string EnsuredBy;
            internal bool RaisedNow;
            internal List<string> Warnings = new List<string>();

            internal object ToDto() => new
            {
                saved = Saved,
                reason = Reason,
                path = Path,
                mode = Mode,
                number_of_saves = NumberOfSaves,
                backups = new { count_on_disk = BackupCount, min_backups_ensured = MinBackupsEnsured, ensured_by = EnsuredBy, raised_now = RaisedNow },
                warnings = Warnings,
                note = Mode == "workshared_local" ? "Saved locally; changes have not been synchronized to central." : null
            };
        }

        internal static int HostYear(UIApplication app) => int.Parse(app.Application.VersionNumber, CultureInfo.InvariantCulture);

        internal static Context Capture(Document doc)
        {
            var result = new Context { Document = doc, Path = doc.PathName };
            var cloud = doc.IsModelInCloud;
            if (!cloud && doc.IsWorkshared && !string.IsNullOrWhiteSpace(result.Path))
            {
                try
                {
                    var central = doc.GetWorksharingCentralModelPath();
                    if (central != null && !central.Empty)
                        result.CentralPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
                }
                catch { result.CentralPath = null; }
            }
            try
            {
                result.Mode = DocumentModeClassifier.Classify(cloud, doc.IsFamilyDocument, doc.IsWorkshared, result.Path, result.CentralPath);
            }
            catch (ArgumentException) when (doc.IsWorkshared && string.IsNullOrWhiteSpace(result.CentralPath))
            {
                result.ModeError = "WORKSHARED_CENTRAL_UNKNOWN";
            }
            return result;
        }

        internal static Versions ReadVersions(Document doc, string path, int hostYear)
        {
            var memory = Document.GetDocumentVersion(doc);
            using (var header = BasicFileInfo.Extract(path))
            {
                var disk = header.GetDocumentVersion();
                var format = header.Format;
                return new Versions
                {
                    MemorySaves = memory.NumberOfSaves,
                    MemoryGuid = memory.VersionGUID,
                    DiskSaves = disk.NumberOfSaves,
                    DiskGuid = disk.VersionGUID,
                    Status = FileVersionStatus.Compare(memory.NumberOfSaves, memory.VersionGUID, disk.NumberOfSaves, disk.VersionGUID),
                    SavedInVersion = format,
                    UpgradeOnSave = DocumentFormatPolicy.RequiresUpgrade(format, hostYear)
                };
            }
        }

        internal static Preparation Prepare(UIApplication app, Document doc, bool allowUpgrade, bool checkpoint)
        {
            if (doc == null) return new Preparation { Error = "NO_ACTIVE_DOCUMENT" };
            var context = Capture(doc);
            var modeError = DocumentSavePolicy.Check(context.Mode, true, "in_sync", true, false, true, false, checkpoint);
            if (modeError != null) return new Preparation { Context = context, Error = modeError };
            if (!File.Exists(context.Path)) return new Preparation { Context = context, Error = "FILE_MISSING_ON_DISK" };

            var versions = ReadVersions(doc, context.Path, HostYear(app));
            var modified = doc.IsModified;
            if (modified && versions.Status != "changed_on_disk" && !versions.UpgradeOnSave.HasValue)
                return new Preparation { Context = context, Versions = versions, Error = "FILE_FORMAT_UNKNOWN" };
            var upgrade = versions.UpgradeOnSave.GetValueOrDefault();
            var readOnly = modified && versions.Status != "changed_on_disk"
                && (!upgrade || allowUpgrade)
                && (File.GetAttributes(context.Path) & FileAttributes.ReadOnly) != 0;
            var error = DocumentSavePolicy.Check(context.Mode, true, versions.Status, modified,
                upgrade, allowUpgrade, readOnly, checkpoint);
            return new Preparation { Context = context, Versions = versions, Error = error };
        }

        internal static SaveOutcome Save(UIApplication app, Preparation prep)
        {
            var context = prep.Context;
            var result = new SaveOutcome { Path = context.Path, Mode = context.Mode, NumberOfSaves = prep.Versions.MemorySaves };
            if (prep.Error == "no_changes")
            {
                result.Reason = "no_changes";
                if (context.Mode == "local_file" || context.Mode == "family")
                {
                    try { result.BackupCount = BackupFileCounter.Count(context.Path); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                    {
                        result.Warnings.Add("BACKUP_COUNT_UNAVAILABLE");
                    }
                    var known = new BackupLimitRegistry().Get(context.Path);
                    if (known != null) { result.MinBackupsEnsured = known.MinBackups; result.EnsuredBy = known.EnsuredBy; }
                }
                return result;
            }
            if (prep.Error != null) throw new InvalidOperationException(prep.Error);

            BackupSaveDecision decision = null;
            BackupLimitRegistry registry = null;
            if (context.Mode == "local_file" || context.Mode == "family")
            {
                registry = new BackupLimitRegistry();
                decision = registry.Decide(context.Path, () => BackupFileCounter.Count(context.Path));
                if (decision.UseSaveAs)
                {
                    using (var options = new SaveAsOptions { OverwriteExistingFile = true, MaximumBackups = BackupLimitRegistry.MinimumBackups })
                        context.Document.SaveAs(context.Path, options);
                }
                else context.Document.Save();
            }
            else context.Document.Save();

            // Native pre-events may cancel even if the API returns normally.
            FailureControl.ThrowIfStopped();
            result.Saved = true;
            result.RaisedNow = decision?.UseSaveAs == true;
            if (decision != null)
            {
                result.MinBackupsEnsured = BackupLimitRegistry.MinimumBackups;
                result.EnsuredBy = decision.EnsuredBy;
                if (decision.MustRecord)
                {
                    try
                    {
                        if (!registry.Record(context.Path, decision.EnsuredBy, DateTimeOffset.UtcNow, HostYear(app)))
                            result.Warnings.Add("BACKUP_REGISTRY_BUSY");
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                    {
                        result.Warnings.Add("BACKUP_REGISTRY_WRITE_FAILED");
                    }
                }
                try { result.BackupCount = BackupFileCounter.Count(context.Path); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                {
                    result.Warnings.Add("BACKUP_COUNT_UNAVAILABLE");
                }
            }
            try
            {
                var active = app.ActiveUIDocument?.Document;
                var current = active != null && active.Equals(context.Document) ? active : context.Document;
                if (active == null || !active.Equals(context.Document)) result.Warnings.Add("ACTIVE_DOCUMENT_CHANGED");
                result.NumberOfSaves = Document.GetDocumentVersion(current).NumberOfSaves;
            }
            catch
            {
                result.NumberOfSaves = null;
                result.Warnings.Add("SAVED_VERSION_UNAVAILABLE");
            }
            return result;
        }

        internal static Tuple<int, Guid> DiskVersion(string path)
        {
            using (var header = BasicFileInfo.Extract(path))
            {
                var version = header.GetDocumentVersion();
                return Tuple.Create(version.NumberOfSaves, version.VersionGUID);
            }
        }

        internal static string CheckpointDirectory(string modelPath) =>
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(modelPath), System.IO.Path.GetFileNameWithoutExtension(modelPath) + ".checkpoints");

        internal static string IndexPath(string modelPath) => System.IO.Path.Combine(CheckpointDirectory(modelPath), "index.json");
    }
}
