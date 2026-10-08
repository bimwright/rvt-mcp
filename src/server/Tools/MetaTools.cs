using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Bimwright.Targeting;
using RvtMcp.Plugin; // RvtMcpConfig
using RvtMcp.Server.Bake;
using RvtMcp.Server.Handlers;
using RvtMcp.Server.Prompts;
using RvtMcp.ToolCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("meta")]
    public class MetaTools
    {
        [McpServerTool(Name = "revit_list_recent_models", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description(
            "List this Revit's recent models from its Revit.ini Recent File List. Works when no document is open. " +
            "Does not open a file. Ask the user which path to open, then call revit_open_model with that absolute path. " +
            "Returns up to 50 entries, most recent first, each with index, path, file name, and whether the file still exists.")]
        public static async Task<string> ListRecentModels()
            => await ToolGateway.CallAsync("list_recent_models");

        [McpServerTool(Name = "revit_open_model", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), System.ComponentModel.Description(
            "Open a Revit model, template or family from disk and make it the active document. " +
            "THIS IS THE TOOL TO CALL WHEN NO DOCUMENT IS OPEN. Other tools then report that Revit is running with no document and name revit_list_recent_models. Ask the user which recent file to open before calling this. " +
            "path: an absolute path on the machine Revit runs on, NOT on the MCP client's machine. " +
            "A workshared .rvt is never opened directly: a new local copy is always created from its central model " +
            "(same as Revit's Open > 'Create New Local') under the Revit.ini ProjectPath folder as <central>_<username>.rvt - " +
            "no ProjectPath means the call stops and reports it. " +
            "A file whose header cannot be read is never opened because its worksharing state is unknown. " +
            "If a local with that name already exists it is renamed with a timestamp - never deleted or overwritten - " +
            "unless it is open in the session, has changes not yet saved to central, or its header cannot be read, " +
            "in which case the call stops and asks the user. " +
            "There is no detach option - detaching from central or opening a central directly must go through revit_send_code_to_revit. " +
            "activate=true (default) opens it in the UI and makes it active; false opens it in the background with no view, where tools reading the active document will not see it. " +
            "worksets (all|none|lastViewed) applies to workshared models only. audit=true is slow - use it on a suspect file. " +
            "A model Revit already has open is reported back (and activated when activate=true) rather than reopened. " +
            "Two known refusals: a file already loaded as a link in an open document cannot be opened standalone (close the host document or unload the link first), " +
            "and a central saved in an older Revit version cannot create a local - if that fails after an existing local was renamed aside, the error names the timestamped path it was kept as; nothing is lost. " +
            "timeout_seconds: 1-900, default 600 - opening a model that pulls in many links commonly exceeds 60s (measured: a 20 MB model with 5 links took 64s). " +
            "Returns: {opened, was_already_open, title, path, saved_in_version, is_workshared, is_family, header_worksharing, central_path, local_path, local_file_created, renamed_existing_local, activated, active_view}. " +
            "NOTE: opening a model saved in an older Revit upgrades it in memory - that only reaches disk if something saves it.")]
        public static async Task<string> OpenModel(string path, bool activate = true, bool audit = false, string worksets = null, int timeout_seconds = 600)
        {
            var blocked = ServerState.BlockIfReadOnly("open_model");
            if (blocked != null) return blocked;

            var timeoutError = ToolGateway.ValidateTimeoutSeconds(timeout_seconds);
            if (timeoutError != null) return timeoutError;

            try
            {
                var parameters = new { path, activate, audit, worksets };
                var result = await ToolGateway.SendToRevit("open_model", parameters, timeout_seconds);
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
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
        internal static JObject TargetEnvelope(TargetPayload payload)
        {
            var json = payload.ToJson();
            FillWindowTitles(json);
            return new JObject
            {
                ["success"] = false,
                ["error"] = payload.Message,
                ["target"] = json
            };
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
            "List every Revit instance currently running with the rvt-mcp plugin loaded. " +
            "Reads the discovery directory %LOCALAPPDATA%\\Bimwright\\rvt-mcp\\ — both per-instance revit-<year>-<pid>.json and legacy revit-<year>.json files. " +
            "Multiple instances may share a year — identify the one you want by pid and window_title, then pin it with revit_switch_target(\"pid:<n>\"). Never guess. " +
            "Use this BEFORE revit_switch_target. " +
            "Returns: {discovery_dir, count, targets: [{year, transport, port, pipe_name, pid, discovery_file, is_currently_connected, target_id, host_year, process_start_utc, identity_verified, source, is_current, window_title}]}. " +
            "If count == 0, no Revit is running or no plugin is loaded — instruct the user to start Revit and enable the rvt-mcp plugin.")]
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
            "Report which Revit instance this MCP server will route the NEXT command to. " +
            "Returns: {pinned_target, currently_connected_year (or null), currently_connected_pid (or null), discovery_dir, selector, binding, binding_state, generation, bound_target_id, bound_pid, bound_host_year, identity_verified, window_title, next_target_id, confirmation_required, last_switch}. " +
            "When confirmation_required is true the previous bound Revit is gone — call revit_switch_target to confirm a candidate before repeating the call. " +
            "Use to verify routing before sending Revit-modifying commands when multiple Revits are open.")]
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

        [McpServerTool(Name = "revit_switch_target", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description(
            "Switch the active Revit binding to a specific instance when multiple Revits are running — including several instances of the same year. " +
            "version: a selector — 'auto', a 4-digit calendar year, 'pid:<n>', or 'id:revit-<year>-<pid>'. " +
            "When several instances share a year, ALWAYS pin with pid:<n> after listing. " +
            "DO NOT pass R-codes like 'R22' or 'R24' — they are rejected with an educational error. " +
            "DO NOT guess. ALWAYS call revit_list_available_targets first to see which instances are actually running (pid, window_title). " +
            "A valid switch after TARGET_CHANGED confirms the proposed replacement — calls keep failing until you confirm. " +
            "Switching to a different instance closes the old connection; any command still in flight ends as TARGET_INTERRUPTED. " +
            "Returns: {ok, previous: {target_id, pid}|null, binding: {…}, verified, activeView} plus legacy previousTarget/newTarget strings. " +
            "verify=true (default): immediately attempts get_current_view_info against the new target to confirm connectivity; " +
            "set verify=false to skip when the new target's document isn't in a view yet (e.g., Revit just launched).")]
        public static async Task<string> SwitchTarget(string version, bool verify = true)
        {
            try
            {
                var snapBefore = RevitTargetBinding.Snapshot();

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
                var result = ToolGateway.SwitchBinding(selector, live);
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
                    var probe = await ToolGateway.SendToRevit("get_current_view_info");
                    return JsonConvert.SerializeObject(new
                    {
                        ok = true,
                        previous,
                        binding = BindingJson(RevitTargetBinding.Snapshot(), live),
                        verified = true,
                        activeView = probe.Value<string>("viewName"),
                        previousTarget,
                        newTarget
                    }, Formatting.Indented);
                }
                catch (Exception verifyEx)
                {
                    // The switch itself succeeded; a verify failure must not replace this
                    // meta-tool response with the probe's target envelope.
                    if (callCtx != null) callCtx.Payload = null;
                    return JsonConvert.SerializeObject(new
                    {
                        ok = true,
                        previous,
                        binding = BindingJson(RevitTargetBinding.Snapshot(), live),
                        verified = false,
                        verifyError = verifyEx.Message,
                        verify_target = (verifyEx as TargetException)?.Payload is { } vp
                            ? MetaTools.TargetEnvelope(vp)["target"] : null,
                        previousTarget,
                        newTarget,
                        note = "Target set, but verify failed. The next tool call may still succeed."
                    }, Formatting.Indented);
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

            try
            {
                var result = await ToolGateway.SendToRevit("set_project_info", new
                {
                    name,
                    number,
                    client_name,
                    address,
                    status,
                    issue_date
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
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

            try
            {
                var result = await ToolGateway.SendToRevit("purge_unused", new
                {
                    targets = targets ?? new[] { "families" },
                    dry_run,
                    limit
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
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
                    var entries = journal.ReadDay(date);
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
