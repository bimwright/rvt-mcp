using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Documents;

namespace RvtMcp.Plugin.Handlers
{
    public sealed class ManageCheckpointsHandler : IRevitCommand
    {
        public string Name => "manage_checkpoints";
        public string Description => "List, prune with an approved plan, or restore a checkpoint as a new file.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""action"":{""type"":""string""},""keep_last"":{""type"":""integer""},""dry_run"":{""type"":""boolean""},""plan_hash"":{""type"":""string""},""checkpoint_id"":{""type"":""string""}},""required"":[""action""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var args = JObject.Parse(paramsJson);
            var action = args.Value<string>("action");
            if (action != "list" && action != "prune" && action != "restore") return CommandResult.Fail("action must be list, prune or restore.");
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_ACTIVE_DOCUMENT");
            var context = DocumentLifecycleSupport.Capture(doc);
            if (context.Mode == null) return CommandResult.Fail("WORKSHARED_CENTRAL_UNKNOWN");
            if (context.Mode == "workshared_local" || context.Mode == "workshared_central") return CommandResult.Fail("CHECKPOINT_WORKSHARED_UNSUPPORTED");
            if (context.Mode == "cloud") return CommandResult.Fail("CLOUD_MODEL_UNSUPPORTED");
            if (context.Mode == "unsaved") return CommandResult.Fail("DOCUMENT_NEVER_SAVED");
            if (context.Mode != "local_file" && context.Mode != "family") return CommandResult.Fail("CHECKPOINT_UNSUPPORTED");

            var store = new CheckpointStore(context.Path);
            try
            {
                if (action == "list")
                {
                    var list = store.List();
                    var entries = new JArray(list.Items.Select(item =>
                    {
                        var entry = JObject.FromObject(item.Entry);
                        entry["exists"] = item.Exists;
                        entry["bytes"] = item.Bytes == null ? JValue.CreateNull() : new JValue(item.Bytes.Value);
                        return entry;
                    }));
                    return CommandResult.Ok(new { checkpoints = entries, untracked_files = list.UntrackedFiles, index_status = list.IndexStatus });
                }
                if (action == "restore")
                {
                    var restored = store.Restore(args.Value<string>("checkpoint_id"));
                    if (restored.Error != null) return CommandResult.Fail(restored.Error);
                    return CommandResult.Ok(new
                    {
                        path = restored.Path,
                        next_steps = new[]
                        {
                            "Open with revit_open_model if the user wants to inspect it. Before writing, call revit_switch_target(verify=true, expectedDocumentId=<_target.document_id from the open result>); without expectedDocumentId the same session gets DOCUMENT_CHANGED.",
                            "To replace the original, the user must close the model in Revit and rename the files manually.",
                            "The new file has a new model scope; rules, grants and history are not inherited."
                        }
                    });
                }

                var keepLast = args.Value<int?>("keep_last") ?? 5;
                if (keepLast < 1 || keepLast > 100) return CommandResult.Fail("keep_last must be between 1 and 100.");
                if (args.Value<bool?>("dry_run") ?? true)
                {
                    var plan = store.Plan(keepLast);
                    return CommandResult.Ok(new
                    {
                        keep_last = keepLast,
                        delete = plan.DeleteFiles.Select(c => new { id = c.Id, file = c.File, bytes = c.Bytes, last_write_utc = c.LastWriteUtc?.ToString("o") }).ToArray(),
                        missing = plan.MissingEntries.Select(c => new { id = c.Id, file = c.File }).ToArray(),
                        bytes_to_free = plan.BytesToFree,
                        plan_hash = plan.PlanHash
                    });
                }
                var result = store.Prune(keepLast, args.Value<string>("plan_hash"));
                var data = new { deleted = result.DeletedIds, missing_removed = result.MissingIds, failed = result.FailedIds };
                return result.Error == null ? CommandResult.Ok(data) : new CommandResult { Success = false, Error = result.Error, Data = data };
            }
            catch (TimeoutException) { return CommandResult.Fail("CHECKPOINT_INDEX_BUSY"); }
            catch (InvalidDataException) { return CommandResult.Fail("CHECKPOINT_INDEX_UNREADABLE"); }
            catch (IOException) { return CommandResult.Fail("CHECKPOINT_IO_FAILED"); }
            catch (UnauthorizedAccessException) { return CommandResult.Fail("CHECKPOINT_IO_FAILED"); }
        }
    }
}
