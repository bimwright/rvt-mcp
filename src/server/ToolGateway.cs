using System;
using System.Linq;
using System.Threading.Tasks;
using Bimwright.Targeting;
using RvtMcp.Plugin; // RvtMcpConfig
using RvtMcp.Server.Bake;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    /// <summary>
    /// Shared plugin-connection plumbing used by every toolset class. Owns the socket/
    /// pipe lifecycle, response read loop, pending-request correlation, and session
    /// call recording. Toolset classes contain only the MCP tool-method shells.
    /// Every wire command is gated by <see cref="RevitTargetBinding"/> (spec §6):
    /// a call pins the binding generation when it arrives, and the command is only
    /// written to the connection while the pinned generation still matches.
    /// </summary>
    internal static partial class ToolGateway
    {
        public static Memory.SessionContext Session { get; set; }
        internal static Memory.ChangeHistoryStore History { get; set; }
        private static readonly string HistorySession = Guid.NewGuid().ToString("N");
        public static UsageEventLogger UsageLogger { get; set; }

        /// <summary>Calendar year of the bound Revit instance, or null when nothing is bound.</summary>
        public static string CurrentRevitVersion =>
            RevitTargetBinding.Snapshot().Record?.HostYear.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Plugin-side wait is the primary timeout authority; the server adds a
        // short grace so the plugin's structured timeout response wins the race.
        private static readonly TimeSpan TransportGrace = TimeSpan.FromSeconds(5);

        // Tests assign this to record the wire call without opening a socket.
        internal static Func<string, object, int?, Task<JObject>> SendOverride;

        public static async Task<JObject> SendToRevit(string command, object parameters = null, int? timeoutSeconds = null)
        {
            var config = ServerState.Config ?? new RvtMcpConfig();
            var rejection = ToolReadPolicy.Rejection(command, config.ReadOnlyOrDefault, config.EnableSendCodeOrDefault);
            if (rejection != null) throw new InvalidOperationException(rejection);
            var sendOverride = SendOverride;
            if (sendOverride != null)
                return await sendOverride(command, parameters, timeoutSeconds);

            var callCtx = TargetCallContext.Current;
            long pinned;
            if (callCtx != null && callCtx.InToolCall)
            {
                pinned = callCtx.PinnedGeneration;
            }
            else
            {
                // Code outside a tool call must never trigger first selection (§6.4);
                // it may reuse the existing binding only.
                var snap = RevitTargetBinding.Snapshot();
                if (snap.State == BindingState.None || snap.Record == null)
                    throw new InvalidOperationException(
                        "No Revit target is bound; this call did not come through an MCP tool request.");
                pinned = snap.Generation;
            }

            var id = $"req-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid().ToString("N").Substring(0, 6)}";
            var history = config.EnableChangeHistoryOrDefault ? new ChangeHistoryRequest { Id = Guid.NewGuid().ToString("N"), Session = HistorySession } : null;
            using var historyLease = history == null ? null : new Memory.HistoryCallLease(history.Id);
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var binding = RevitTargetBinding.Binding;

            // Establishment can take seconds (scan, descriptor wait, socket connect,
            // catalog handshake) — run it off the caller's context.
            var (conn, effectiveGeneration) = await Task.Run(() => EnsureConnectedFor(pinned, callCtx));
            (conn, effectiveGeneration) = await WriteRequest(
                conn, effectiveGeneration, id, command, parameters, timeoutSeconds, config, history, tcs, callCtx);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int budgetSeconds = timeoutSeconds ?? 60;
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(budgetSeconds) + TransportGrace);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                _pending.TryRemove(id, out _);
                sw.Stop();
                var paramsStr = parameters != null ? JsonConvert.SerializeObject(parameters, RequestJsonSettings) : null;
                UsageLogger?.RecordToolCall(command, paramsStr, false);
                throw new TimeoutException($"Request timed out ({budgetSeconds}s). Revit may be in a modal dialog.");
            }

            sw.Stop();
            string responseLine;
            try
            {
                responseLine = await tcs.Task;
            }
            catch (ConnectionInterruptedException)
            {
                var paramsStr = parameters != null ? JsonConvert.SerializeObject(parameters, RequestJsonSettings) : null;
                UsageLogger?.RecordToolCall(command, paramsStr, false);
                throw new TargetException(binding.Interrupted(effectiveGeneration));
            }
            var response = JObject.Parse(responseLine);
            var paramsJson = parameters != null ? JsonConvert.SerializeObject(parameters, RequestJsonSettings) : null;
            UsageLogger?.RecordToolCall(command, paramsJson, response.Value<bool>("success"));
            CaptureHistory(response, history);
            return InterpretResponse(response);
        }

        /// <summary>
        /// Standard tool-shell call: send the command, return the plug-in data as indented JSON,
        /// and surface any failure to the agent as "Error: ..." text instead of throwing.
        /// Shells that parse or validate input must do it inside their own try, not in the arguments here.
        /// </summary>
        internal static async Task<string> CallAsync(string command, object parameters = null, int? timeoutSeconds = null)
        {
            try
            {
                var result = await SendToRevit(command, parameters, timeoutSeconds);
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        /// <summary>
        /// <see cref="CallAsync"/> for shells that parse or normalize input: the parameters are built
        /// inside the same try, so bad input surfaces as "Error: ..." text and never reaches the wire.
        /// </summary>
        internal static async Task<string> CallWithParamsAsync(string command, Func<object> buildParameters, int? timeoutSeconds = null)
        {
            try
            {
                var parameters = buildParameters();
                var result = await SendToRevit(command, parameters, timeoutSeconds);
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        internal static void CaptureHistory(JObject response, ChangeHistoryRequest request)
        {
            if (request == null) return;
            JObject receipt = null;
            var marker = response["history_transfer"] as JObject;
            if (marker?.Value<string>("id") == request.Id)
            {
                try { receipt = (History ??= new Memory.ChangeHistoryStore()).Consume(request.Id); }
                catch (Exception ex) { HistoryDiagnostics.Report("history_consume", ex, request.Id); receipt = new JObject { ["status"] = "storage_failed", ["callId"] = request.Id,
                    ["note"] = "Change history could not be recorded. The private capture remains pending for recovery. Do not repeat the model operation." }; }
            }
            else if (marker?.Value<string>("status") == "skipped_identity")
                receipt = new JObject { ["status"] = "skipped_identity", ["skippedDocuments"] = marker["skippedDocuments"],
                    ["note"] = "Changed documents have no stable identity; history was skipped without writing pending files. Do not replay the model operation." };
            else if (marker != null || response["changes"] != null)
                receipt = new JObject { ["status"] = "unavailable",
                    ["note"] = "Change history is unavailable for this operation. Check plugin compatibility or local storage; do not repeat the model operation." };
            if (receipt != null) ChangeCaptureContext.RecordHistory(receipt);
        }

        internal static JObject InterpretResponse(JObject response)
        {
            var changes = ChangeSummary.ForAgent(response["changes"]);
            ChangeCaptureContext.Record(changes);

            if (response.Value<bool>("success"))
            {
                var data = response["data"] as JObject ?? new JObject();
                var responseWarning = response.Value<string>("warning");
                if (!string.IsNullOrWhiteSpace(responseWarning))
                    data["_response_warning"] = responseWarning;
                if (changes != null) data["_changes"] = changes;
                return data;
            }
            else
            {
                var error = response.Value<string>("error") ?? "Unknown error from Revit";
                throw new InvalidOperationException(UnknownCommandGuidance(error, CurrentRevitVersion) ?? error);
            }
        }

        /// <summary>
        /// A plugin that predates a tool answers "Unknown command: &lt;name&gt;". Say what that means and what to do,
        /// because the same server can reach several Revit years that carry different add-in builds.
        /// Returns null when the text is not that error.
        /// </summary>
        internal static string UnknownCommandGuidance(string error, string revitYear)
        {
            const string prefix = "Unknown command: ";
            if (string.IsNullOrWhiteSpace(error) || !error.StartsWith(prefix, StringComparison.Ordinal))
                return null;

            var command = error.Substring(prefix.Length).Trim();
            if (command.Length == 0 || command.Any(char.IsWhiteSpace))
                return null;

            var year = string.IsNullOrWhiteSpace(revitYear) ? "this Revit" : "Revit " + revitYear;
            return $"The add-in loaded in {year} does not know the command '{command}'. It is most likely an older build than this server. "
                 + "Install the add-in from the same release as the server for that Revit year and restart Revit. "
                 + "If another Revit instance already has the newer add-in, call revit_list_available_targets to see its pid, "
                 + "then pin it with revit_switch_target(\"pid:<n>\") — never guess.";
        }

        /// <summary>
        /// Long-run tools accept a per-call plugin wait of 1-900 seconds (default 600).
        /// Out-of-range values are rejected rather than clamped so a wrong unit
        /// (e.g. 600000 milliseconds) surfaces as an error instead of a silent cap.
        /// </summary>
        internal static string ValidateTimeoutSeconds(int timeoutSeconds)
        {
            if (timeoutSeconds < 1 || timeoutSeconds > 900)
                return $"Error: timeout_seconds must be between 1 and 900 seconds (got {timeoutSeconds}). The default is 600 and the unit is seconds, not milliseconds.";
            return null;
        }
    }
}
