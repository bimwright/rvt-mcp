using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Documents;

namespace RvtMcp.Plugin.Handlers
{
    public sealed class GetDocumentStateHandler : IRevitCommand
    {
        public string Name => "get_document_state";
        public string Description => "Inspect document mode, file version, backup and checkpoint state.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""scope"":{""type"":""string""}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var scope = JObject.Parse(paramsJson).Value<string>("scope") ?? "active";
            if (scope != "active" && scope != "all") return CommandResult.Fail("scope must be active or all.");
            var active = app.ActiveUIDocument?.Document;
            var documents = new JArray();
            JObject policy = null;
            if (scope == "active")
            {
                if (active != null) AppendDocument(app, active, active, false, documents, ref policy);
            }
            else
            {
                foreach (Document doc in app.Application.Documents)
                    AppendDocument(app, doc, active, true, documents, ref policy);
            }
            return CommandResult.Ok(new JObject { ["documents"] = documents, ["policy"] = policy });
        }

        private static void AppendDocument(UIApplication app, Document doc, Document active, bool skipLinks,
            JArray documents, ref JObject policy)
        {
            if (doc == null) return;
            bool? isActive = ReferenceEquals(doc, active) ? true : active == null ? false : (bool?)null;
            try
            {
                if (skipLinks && doc.IsLinked) return;
                if (!isActive.HasValue) isActive = doc.Equals(active);
                var state = Describe(app, doc, isActive.Value, out var docPolicy);
                documents.Add(state);
                if (isActive.Value) policy = docPolicy;
            }
            catch (Exception)
            {
                string title = null;
                string path = null;
                try { title = doc.Title; } catch { }
                try { path = doc.PathName; } catch { }
                documents.Add(new JObject
                {
                    ["title"] = title, ["path"] = path,
                    ["is_active"] = isActive.HasValue ? new JValue(isActive.Value) : JValue.CreateNull(),
                    ["error"] = "DOCUMENT_STATE_UNAVAILABLE"
                });
            }
        }

        private static JObject Describe(UIApplication app, Document doc, bool isActive, out JObject policy)
        {
            var context = DocumentLifecycleSupport.Capture(doc);
            policy = null;
            if (isActive && context.Mode != null)
            {
                var value = DocumentPolicy.ForMode(context.Mode);
                policy = new JObject { ["mode"] = value.Mode, ["rules"] = new JArray(value.Rules), ["allowed"] = new JArray(value.Allowed) };
            }

            var path = context.Path;
            var hostYear = DocumentLifecycleSupport.HostYear(app);
            var localPath = context.Mode != "cloud" && !string.IsNullOrWhiteSpace(path);
            var exists = localPath && File.Exists(path);
            var warnings = new JArray();
            DocumentLifecycleSupport.Versions versions = null;
            JObject memory = null;
            JObject disk = null;
            string versionStatus = null;
            if (exists)
            {
                try
                {
                    versions = DocumentLifecycleSupport.ReadVersions(doc, path, hostYear);
                    memory = Version(versions.MemorySaves, versions.MemoryGuid);
                    disk = Version(versions.DiskSaves, versions.DiskGuid);
                    versionStatus = versions.Status;
                    if (!versions.UpgradeOnSave.HasValue) warnings.Add("FILE_FORMAT_UNKNOWN");
                }
                catch { warnings.Add("FILE_VERSION_UNAVAILABLE"); }
            }
            else
            {
                if (localPath) versionStatus = "missing_on_disk";
                try
                {
                    var current = Document.GetDocumentVersion(doc);
                    memory = Version(current.NumberOfSaves, current.VersionGUID);
                }
                catch { warnings.Add("MEMORY_VERSION_UNAVAILABLE"); }
            }

            bool? isReadOnly = null;
            long? bytes = null;
            long? free = null;
            if (exists)
            {
                try { isReadOnly = (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0; }
                catch { warnings.Add("FILE_ATTRIBUTES_UNAVAILABLE"); }
                try { bytes = new FileInfo(path).Length; }
                catch { warnings.Add("FILE_SIZE_UNAVAILABLE"); }
                try { free = CheckpointSpace.AvailableBytes(path); }
                catch { warnings.Add("FREE_SPACE_UNAVAILABLE"); }
            }

            int? backupCount = null;
            BackupLimitRecord backup = null;
            JObject checkpoints = null;
            if ((context.Mode == "local_file" || context.Mode == "family") && localPath)
            {
                if (exists)
                {
                    try { backupCount = BackupFileCounter.Count(path); }
                    catch { warnings.Add("BACKUP_COUNT_UNAVAILABLE"); }
                }
                backup = new BackupLimitRegistry().Get(path);
                checkpoints = CheckpointSummary(path);
            }
            return new JObject
            {
                ["title"] = doc.Title,
                ["path"] = path,
                ["document_id"] = DocumentTargetGuard.GetOrCreateToken(doc),
                ["is_active"] = isActive,
                ["is_family"] = doc.IsFamilyDocument,
                ["mode"] = context.Mode,
                ["mode_error"] = context.ModeError,
                ["is_modified"] = doc.IsModified,
                ["is_read_only"] = isReadOnly == null ? JValue.CreateNull() : new JValue(isReadOnly.Value),
                ["saved_in_version"] = versions?.SavedInVersion,
                ["host_year"] = hostYear,
                ["upgrade_on_save"] = versions?.UpgradeOnSave == null ? JValue.CreateNull() : new JValue(versions.UpgradeOnSave.Value),
                ["central_path"] = context.CentralPath,
                ["file_version"] = new JObject { ["memory"] = memory, ["disk"] = disk, ["status"] = versionStatus },
                ["file_bytes"] = bytes == null ? JValue.CreateNull() : new JValue(bytes.Value),
                ["free_disk_bytes"] = free == null ? JValue.CreateNull() : new JValue(free.Value),
                ["backups"] = new JObject
                {
                    ["count_on_disk"] = backupCount == null ? JValue.CreateNull() : new JValue(backupCount.Value),
                    ["min_backups_ensured"] = backup == null ? JValue.CreateNull() : new JValue(backup.MinBackups),
                    ["ensured_by"] = backup?.EnsuredBy
                },
                ["checkpoints"] = checkpoints,
                ["warnings"] = warnings
            };
        }

        private static JObject Version(int saves, Guid guid) => new JObject { ["number_of_saves"] = saves, ["version_guid"] = guid.ToString() };

        private static JObject CheckpointSummary(string path)
        {
            try
            {
                var index = CheckpointIndex.Load(DocumentLifecycleSupport.IndexPath(path), Path.GetFileName(path));
                var folder = DocumentLifecycleSupport.CheckpointDirectory(path);
                CheckpointPlanner.Plan(folder, index.Checkpoints, 100);
                var latest = index.Checkpoints.OrderByDescending(c => DateTimeOffset.Parse(c.CreatedUtc, CultureInfo.InvariantCulture)).FirstOrDefault();
                long totalBytes = 0;
                foreach (var checkpoint in index.Checkpoints)
                {
                    var file = Path.Combine(folder, checkpoint.File);
                    if (File.Exists(file)) totalBytes += new FileInfo(file).Length;
                }
                return new JObject
                {
                    ["count"] = index.Checkpoints.Count,
                    ["total_bytes"] = totalBytes,
                    ["latest"] = latest == null ? null : new JObject { ["id"] = latest.Id, ["label"] = latest.Label, ["created_utc"] = latest.CreatedUtc },
                    ["index_status"] = index.Exists ? "ok" : "not_created"
                };
            }
            catch (TimeoutException) { return UnavailableIndex("busy"); }
            catch (InvalidDataException) { return UnavailableIndex("unreadable"); }
            catch (IOException) { return UnavailableIndex("unreadable"); }
            catch (UnauthorizedAccessException) { return UnavailableIndex("unreadable"); }
        }

        private static JObject UnavailableIndex(string status) => new JObject
        {
            ["count"] = JValue.CreateNull(), ["total_bytes"] = JValue.CreateNull(),
            ["latest"] = JValue.CreateNull(), ["index_status"] = status
        };
    }
}
