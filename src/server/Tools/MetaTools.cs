using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bimwright.Targeting;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("meta")]
    public class MetaTools
    {
        [McpServerTool(Name = "revit_list_recent_models", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description(
            "List up to 50 recent model paths from this Revit's Revit.ini, most recent first, even without an open document. " +
            "Confirm the intended absolute path before calling revit_open_model.")]
        public static async Task<string> ListRecentModels()
            => await ToolGateway.CallAsync("list_recent_models");

        [McpServerTool(Name = "revit_open_model", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description(
            "Open a confirmed absolute path on the Revit machine, including when no document is open; older files may upgrade in memory, reaching disk only if saved. " +
            "Workshared models create a new local from central under Revit.ini ProjectPath, may rename an existing safe local aside, and refuse unknown/unsafe headers; activate=false opens in background, timeout_seconds is 1-900 (default 600), and a timeout must not be retried blindly.")]
        public static async Task<string> OpenModel(string path, bool activate = true, bool audit = false, string worksets = null, int timeout_seconds = 600)
        {
            var blocked = ServerState.BlockIfReadOnly("open_model");
            if (blocked != null) return blocked;

            var timeoutError = ToolGateway.ValidateTimeoutSeconds(timeout_seconds);
            if (timeoutError != null) return timeoutError;

            return await ToolGateway.CallAsync("open_model", new { path, activate, audit, worksets }, timeout_seconds);
        }

        [McpServerTool(Name = "revit_show_message", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Show a Revit TaskDialog. Message echo is off by default; opt in with echoMessage and cap it with maxEchoChars.")]
        public static async Task<string> ShowMessage(string message = null, string title = null, bool echoMessage = false, int maxEchoChars = 1024)
        {
            try
            {
                var parameters = new { message, title, echo_message = echoMessage, max_echo_chars = maxEchoChars };
                var result = await ToolGateway.SendToRevit("show_message", parameters);
                return JsonConvert.SerializeObject(result);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        /// <summary>§6.9 binding block shared by get_current_target and switch_target.</summary>
        internal static JObject BindingJson(BindingSnapshot snap, IReadOnlyList<TargetCandidate> live)
        {
            var record = snap.Record;
            var windowTitle = record != null
                ? live.FirstOrDefault(c => c.Pid == record.Pid)?.WindowTitle
                : null;
            return new JObject
            {
                ["selector"] = snap.Selector.Canonical,
                ["binding"] = snap.State == BindingState.None || record == null
                    ? "none"
                    : record.Kind == BindingKind.Explicit ? "explicit" : "implicit",
                ["binding_state"] = snap.State switch
                {
                    BindingState.Bound => "bound",
                    BindingState.Lost => "lost",
                    _ => "none",
                },
                ["generation"] = snap.Generation,
                ["bound_target_id"] = record?.TargetId,
                ["bound_pid"] = record != null ? (JToken)record.Pid : JValue.CreateNull(),
                ["bound_host_year"] = record != null ? (JToken)record.HostYear : JValue.CreateNull(),
                ["identity_verified"] = record?.IdentityVerified ?? false,
                ["window_title"] = windowTitle != null ? (JToken)windowTitle : JValue.CreateNull(),
                ["next_target_id"] = (JToken)RevitTargetBinding.NextTargetId(live) ?? JValue.CreateNull(),
                ["confirmation_required"] = snap.ConfirmationRequired,
                ["selection_confirmed"] = record?.SelectionConfirmed ?? false,
                ["confirmed_document_id"] = record?.DocumentId,
                ["last_switch"] = snap.LastSwitch != null
                    ? new JObject
                    {
                        ["from"] = snap.LastSwitch.From,
                        ["to"] = snap.LastSwitch.To,
                        ["at_utc"] = snap.LastSwitch.AtUtc.ToString("o")
                    }
                    : JValue.CreateNull()
            };
        }

        /// <summary>§6.10 rvt envelope around a target payload: {success:false, error:"CODE: …", target:{…}}.</summary>
        internal static JObject TargetEnvelope(TargetPayload payload, JObject operation = null)
        {
            var json = payload.ToJson();
            FillWindowTitles(json);
            var envelope = new JObject
            {
                ["success"] = false,
                ["error"] = payload.Message,
                ["target"] = json
            };
            if (operation != null) envelope["_operation"] = operation.DeepClone();
            return envelope;
        }

        /// <summary>
        /// The connect path scans without window titles (probe skips MainWindowTitle);
        /// fill them here from the live process so error payloads still carry
        /// <c>window_title</c> for agent disambiguation (§6.9).
        /// </summary>
        internal static void FillWindowTitles(JObject targetJson)
        {
            Fill(targetJson["previous"] as JObject);
            Fill(targetJson["current"] as JObject);
            if (targetJson["candidates"] is JArray candidates)
                foreach (var c in candidates.OfType<JObject>()) Fill(c);

            static void Fill(JObject o)
            {
                if (o == null || o["window_title"] != null || o["pid"] is not JValue pidTok) return;
                var title = RevitTargetBinding.WindowTitle(pidTok.Value<int>());
                if (title != null) o["window_title"] = title;
            }
        }

        [McpServerTool(Name = "revit_list_available_targets", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description(
            "List discovered live Revit instances with pid, window_title, target identity and connection state. " +
            "Identify the intended instance before revit_switch_target; multiple instances may share a year, so never guess a target. Example: revit_switch_target(\"pid:<listed-pid>\").")]
        public static string ListAvailableTargets()
        {
            try
            {
                var dir = AuthToken.DiscoveryDir();
                var scan = RevitTargetBinding.Scan();
                var snap = RevitTargetBinding.Snapshot();
                var connectedId = ToolGateway.ConnectedTargetId;
                var targets = scan.Live.Select(c => new
                {
                    year = c.HostYear.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    transport = c.Descriptor.Transport,
                    port = c.Descriptor.Transport == "tcp" ? c.Descriptor.Port : null,
                    pipe_name = c.Descriptor.Transport == "pipe" ? c.Descriptor.PipeName : null,
                    pid = c.Pid,
                    discovery_file = Path.Combine(dir, c.Descriptor.FileName),
                    is_currently_connected = string.Equals(c.TargetId, connectedId, StringComparison.OrdinalIgnoreCase),
                    target_id = c.TargetId,
                    host_year = c.HostYear,
                    process_start_utc = c.ObservedStartUtc?.ToString("o"),
                    identity_verified = c.IdentityVerified,
                    source = c.Descriptor.Source == DescriptorSource.PerInstance ? "per_instance" : "legacy",
                    is_current = snap.Record != null && string.Equals(snap.Record.TargetId, c.TargetId, StringComparison.OrdinalIgnoreCase),
                    window_title = c.WindowTitle
                }).ToArray();
                return JsonConvert.SerializeObject(new
                {
                    discovery_dir = dir,
                    count = targets.Length,
                    targets,
                    note = targets.Length == 0
                        ? "No revit-*.json descriptors found. Start Revit and ensure the rvt-mcp plugin is loaded (Add-Ins ribbon)."
                        : "Pin an instance with revit_switch_target — 'version' accepts a 4-digit year, 'pid:<n>' or 'id:<target_id>'. When several instances share a year, always pin with pid:<n>.",
                    update_available = UpdateStatus.ForStatusResult()
                }, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_current_target", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description(
            "Read the next command's Revit binding, connected pid, generation and confirmation state. " +
            "When confirmation_required is true, select and verify the intended instance/document with revit_switch_target before writing.")]
        public static string GetCurrentTarget()
        {
            try
            {
                var snap = RevitTargetBinding.Snapshot();
                var live = RevitTargetBinding.Scan().Live;
                var block = BindingJson(snap, live);
                block["pinned_target"] = snap.Selector.Canonical;
                block["currently_connected_year"] = ToolGateway.ConnectedTargetId != null
                    ? ToolGateway.CurrentRevitVersion
                    : null;
                block["currently_connected_pid"] = ToolGateway.ConnectedPid;
                block["discovery_dir"] = AuthToken.DiscoveryDir();
                block["update_available"] = UpdateStatus.ForStatusResult() is { } upd
                    ? JToken.FromObject(upd)
                    : JValue.CreateNull();
                return JsonConvert.SerializeObject(block, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_switch_target", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description(
            "Pin an instance listed by revit_list_available_targets using pid:<n>, id:revit-<year>-<pid>, auto or a calendar year (never R-codes); verify=true confirms the document for writes. " +
            "Switching closes the old connection and may interrupt in-flight calls; rebind=true leaves a live confirmed target, expectedDocumentId checks identity, and verify=false permits open_model but requires re-verification before model writes. Example: {version: \"pid:1234\"}.")]
        public static async Task<string> SwitchTarget(string version, bool verify = true, bool rebind = false, string expectedDocumentId = null)
        {
            try
            {
                // Parse the selector first — an invalid one changes nothing (§6.8).
                if (!SelectorParser.TryParse(HostProduct.Revit, version, out var selector, out var selError, out var selMessage))
                {
                    var payload = TargetPayload.InvalidSelector(HostProduct.Revit, version,
                        selError == SelectorError.RCode
                            ? "Invalid version format '" + version + "'. v0.5+ uses 4-digit calendar years, NOT R-codes. " +
                              "Translate: R22=2022, R23=2023, R24=2024, R25=2025, R26=2026, R27=2027. " +
                              "BEFORE calling this tool again, call revit_list_available_targets to see exactly which instances are running on this machine (pid, window_title). " +
                              "Do not guess."
                            : "Invalid version '" + version + "'. Allowed: auto | 2022..2027 | pid:<n> | id:revit-<year>-<pid>. " +
                              (selMessage != null ? "(" + selMessage + ") " : "") +
                              "Call revit_list_available_targets first to see which instances are actually running on this machine.");
                    var invalidEnv = TargetEnvelope(payload);
                    invalidEnv["ok"] = false;
                    invalidEnv["allowed_versions"] = new JArray(AuthToken.AllVersions);
                    invalidEnv["recommended_next_tool"] = "revit_list_available_targets";
                    return JsonConvert.SerializeObject(invalidEnv, Formatting.Indented);
                }

                var live = RevitTargetBinding.Scan().Live;
                var result = ToolGateway.SwitchBinding(selector, live, out var snapBefore, rebind);
                if (!result.Ok)
                {
                    var failEnv = TargetEnvelope(result.Failure);
                    failEnv["ok"] = false;
                    failEnv["allowed_versions"] = new JArray(AuthToken.AllVersions);
                    failEnv["recommended_next_tool"] = result.Failure.RecommendedNextTool;
                    return JsonConvert.SerializeObject(failEnv, Formatting.Indented);
                }

                // This call's pinned generation follows the binding it just established,
                // so the verify probe below is sent under the new generation.
                var callCtx = TargetCallContext.Current;
                if (callCtx != null) callCtx.PinnedGeneration = RevitTargetBinding.Snapshot().Generation;

                var previous = result.Previous != null
                    ? (JToken)new JObject { ["target_id"] = result.Previous.TargetId, ["pid"] = result.Previous.Pid }
                    : JValue.CreateNull();
                var previousTarget = snapBefore.Record?.HostYear.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "auto";
                var newTarget = result.Current?.HostYear.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "auto";

                if (!verify)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        ok = true,
                        previous,
                        binding = BindingJson(RevitTargetBinding.Snapshot(), live),
                        verified = false,
                        previousTarget,
                        newTarget,
                        note = "Target updated. Next tool call will connect to new target."
                    }, Formatting.Indented);
                }

                try
                {
                    var probe = await ToolGateway.SendToRevit("get_current_view_info", requireWriteCapabilities: true);
                    var stamp = probe["_target"] as JObject;
                    var verified = !ToolGateway.IsFailureOrPending(probe)
                        && (expectedDocumentId == null || stamp?.Value<string>("document_id") == expectedDocumentId)
                        && ToolGateway.ConfirmDocument(result.Current.Generation, stamp);
                    var restored = !verified && ToolGateway.RestoreAfterFailedVerification(snapBefore, result.Current.Generation);
                    var verification = JObject.FromObject(new
                    {
                        ok = verified,
                        previous,
                        binding = BindingJson(RevitTargetBinding.Snapshot(), live),
                        verified,
                        restored_previous = restored,
                        document = verified ? stamp : null,
                        activeView = verified ? probe.Value<string>("viewName") : null,
                        previousTarget,
                        newTarget
                    });
                    if (!verified)
                    {
                        verification["verifyError"] = probe["error"] ?? (expectedDocumentId != null && !string.IsNullOrEmpty(stamp?.Value<string>("document_id")) && stamp.Value<string>("document_id") != expectedDocumentId
                            ? "DOCUMENT_CHANGED: the active document does not match expectedDocumentId. No model write was sent."
                            : "DOCUMENT_UNCONFIRMED: the selected instance's active document could not be verified. Open the intended model and call revit_switch_target(verify=true) again.");
                        SafetyResponseMetadata.Attach(verification, probe);
                    }
                    // The probe is a real Revit call: what it reports once (dialogs the user handled,
                    // operations that finished after their receipt) must reach the agent through this response.
                    ToolGateway.CarryOnce(probe, verification);
                    return JsonConvert.SerializeObject(verification, Formatting.Indented);
                }
                catch (Exception verifyEx)
                {
                    // The switch itself succeeded; a verify failure must not replace this
                    // meta-tool response with the probe's target envelope.
                    if (callCtx != null) { callCtx.Payload = null; callCtx.Operation = null; }
                    var restored = ToolGateway.RestoreAfterFailedVerification(snapBefore, result.Current.Generation);
                    var verification = JObject.FromObject(new
                    {
                        ok = false,
                        previous,
                        binding = BindingJson(RevitTargetBinding.Snapshot(), live),
                        verified = false,
                        restored_previous = restored,
                        verifyError = verifyEx.Message,
                        verify_target = (verifyEx as TargetException)?.Payload is { } vp
                            ? MetaTools.TargetEnvelope(vp)["target"] : null,
                        previousTarget,
                        newTarget,
                        note = restored ? "Verification failed; the previous live binding was restored."
                            : "Verification failed; the current binding is reported above. Writes require a confirmed document."
                    });
                    if (verifyEx is TargetException targetEx && targetEx.Operation != null)
                        verification["_operation"] = targetEx.Operation.DeepClone();
                    return JsonConvert.SerializeObject(verification, Formatting.Indented);
                }
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_batch_execute", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description(
            "Run multiple MCP commands atomically inside one Revit TransactionGroup (single undo on success). " +
            "send_code_to_revit and run_baked_tool must be called directly, outside a batch. " +
            "At most 20 commands; split larger jobs. " +
            "Input: commands — JSON array of {command, params}, e.g. " +
            "'[{\"command\":\"create_level\",\"params\":{\"elevation\":3000}}, " +
            "{\"command\":\"create_grid\",\"params\":{\"startX\":0,\"startY\":0,\"endX\":5000,\"endY\":0}}]'. " +
            "On any failure the whole group rolls back unless continueOnError=true. " +
            "Returns: {results: [{index, ok, data|error}], rolledBack}. output=file writes NDJSON to a local same-machine absolute path with bounded preview; remote clients receive preview only." +
            " Subcommands may overwrite files or shared parameter data that Revit Undo cannot restore. Back up affected files first.")]
        public static async Task<string> BatchExecute(string commands, bool continueOnError = false, string output = "inline")
        {
            try
            {
                var parsed = JArray.Parse(commands);
                if (parsed.Count > 20) // keep in sync with BatchExecutor.MaxCommands
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "batch_execute supports at most 20 commands (got " + parsed.Count + "). Split the batch."
                    }, Formatting.Indented);
                }
                var result = await ToolGateway.SendToRevit("batch_execute", new { commands = parsed, continueOnError, output });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_set_project_info", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Replace project information fields; restore them with Revit Undo. Set typed fields on doc.ProjectInformation. Params: name, number, client_name, address, status, issue_date (all optional, at least one required). Returns changed_fields and skipped reasons for read-only/missing parameters.")]
        public static async Task<string> SetProjectInfo(
            string name = null, string number = null,
            string client_name = null, string address = null,
            string status = null, string issue_date = null)
        {
            var blocked = ServerState.BlockIfReadOnly("set_project_info");
            if (blocked != null) return blocked;

            return await ToolGateway.CallAsync("set_project_info", new
            {
                name,
                number,
                client_name,
                address,
                status,
                issue_date
            });
        }

        [McpServerTool(Name = "revit_purge_unused", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description("Delete unused family symbols; restore them with Revit Undo. Conservative purge of unused loadable family symbols. MVP supports targets=['families'] only. Skips in-place families and symbols with any placed instance. dry_run defaults to true.")]
        public static async Task<string> PurgeUnused(
            string[] targets = null, bool dry_run = true, int limit = 500)
        {
            if (!dry_run)
            {
                var blocked = ServerState.BlockIfReadOnly("purge_unused");
                if (blocked != null) return blocked;
            }

            return await ToolGateway.CallWithParamsAsync("purge_unused", () => new
            {
                targets = targets ?? new[] { "families" },
                dry_run,
                limit
            });
        }

        [McpServerTool(Name = "revit_analyze_usage_patterns", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description(
            "Analyze MCP tool usage. Returns session stats (call counts, success rates, top tools, flags) " +
            "plus historical data from journal files. " +
            "Params: days (int, default 1) — days of history to include. " +
            "Use to spot most-used tools, frequent failures, repeated patterns.")]
        public static string AnalyzeUsagePatterns(int days = 1)
        {
            try
            {
                var session = ToolGateway.Session;
                if (session == null) return JsonConvert.SerializeObject(new { error = "No active session" });

                var report = session.GetPatternReport();

                var journal = session.Journal;
                var historicalTools = new Dictionary<string, int>();
                var historicalErrors = new Dictionary<string, int>();
                int historicalTotal = 0;

                var dates = journal.ListDates();
                var cutoff = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd");

                foreach (var date in dates)
                {
                    if (string.Compare(date, cutoff, StringComparison.Ordinal) < 0) continue;
                    var entries = journal.ReadDayEntries(date);
                    foreach (var entry in entries)
                    {
                        historicalTotal++;
                        if (!historicalTools.ContainsKey(entry.Tool)) historicalTools[entry.Tool] = 0;
                        historicalTools[entry.Tool]++;
                        if (!entry.Success)
                        {
                            if (!historicalErrors.ContainsKey(entry.Tool)) historicalErrors[entry.Tool] = 0;
                            historicalErrors[entry.Tool]++;
                        }
                    }
                }

                var result = new
                {
                    session = new
                    {
                        total_calls = report.TotalCalls,
                        total_errors = report.TotalErrors,
                        top_tools = report.TopTools.Select(t => new { t.Tool, t.CallCount, t.ErrorCount, error_rate = t.ErrorRate.ToString("P0") }),
                        error_prone = report.ErrorProne.Select(t => new { t.Tool, t.CallCount, t.ErrorCount, error_rate = t.ErrorRate.ToString("P0") }),
                        flags = report.Flags
                    },
                    history = new
                    {
                        days_included = days,
                        total_calls = historicalTotal,
                        top_tools = historicalTools.OrderByDescending(kv => kv.Value).Take(10)
                            .Select(kv => new { tool = kv.Key, count = kv.Value }),
                        error_tools = historicalErrors.OrderByDescending(kv => kv.Value).Take(5)
                            .Select(kv => new { tool = kv.Key, errors = kv.Value })
                    }
                };

                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }
    }
}
