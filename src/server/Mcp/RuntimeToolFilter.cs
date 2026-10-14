using System;
using System.Linq;
using System.Text;
using ModelContextProtocol.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server.Memory;

namespace RvtMcp.Server
{
    internal static class RuntimeToolFilter
    {
        internal static IMcpServerBuilder Register(IMcpServerBuilder mcp, RvtMcpConfig config, SessionContext session)
            => mcp.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (request, ct) =>
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                // Pin the binding generation at call entry (spec §6.6/§8). When the
                // binding layer produced a §6.10 payload the tool result is replaced
                // with the target envelope — regardless of how the tool formatted it.
                var targetCtx = TargetCallContext.Begin();
                targetCtx.Cancellation = ct;
                try
                {
                    using (var capture = new ChangeCaptureContext())
                    {
                        CallToolResult result;
                        try
                        {
                            result = await next(request, ct);
                        }
                        catch when (targetCtx.Payload != null)
                        {
                            // Even an uncaught TargetException leaves its payload on the
                            // context — surface the §6.10 envelope, not a bare exception.
                            result = TextResult(MetaTools.TargetEnvelope(targetCtx.Payload, targetCtx.Operation), failed: true);
                        }
                        if (targetCtx.Payload != null)
                            result = TextResult(MetaTools.TargetEnvelope(targetCtx.Payload, targetCtx.Operation), failed: true);
                        return Apply(request.Params, result, config, session, durationMs: clock.ElapsedMilliseconds,
                            changes: capture.Changes, history: capture.History, previousOperations: ToolGateway.PreviousOperations);
                    }
                }
                finally
                {
                    TargetCallContext.End(targetCtx);
                }
            }));

        internal static CallToolResult Apply(CallToolRequestParams request, CallToolResult result,
            RvtMcpConfig config, SessionContext session = null, ResponseSpillWriter writer = null, long durationMs = 0,
            JObject changes = null, JObject history = null, PreviousOperationTracker previousOperations = null)
        {
            var name = request?.Name ?? "";
            var command = name.StartsWith("revit_", StringComparison.Ordinal) ? name.Substring(6) : name;
            var arguments = System.Text.Json.JsonSerializer.Serialize(request?.Arguments);
            var text = result.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text;
            if (TargetCallContext.Current?.ActualTarget is JObject actualTarget)
            {
                JObject routed;
                try { routed = JObject.Parse(text ?? "{}"); }
                catch { routed = new JObject { ["result"] = text }; }
                SafetyResponseMetadata.Attach(routed, new JObject { ["_target"] = actualTarget });
                result = TextResult(routed, result.IsError == true);
                text = routed.ToString(Formatting.None);
            }
            JObject safety = null;
            try { safety = JObject.Parse(text ?? "{}"); } catch { }
            var failed = result.IsError == true || IsFailure(text, command);
            if (failed) result.IsError = true;
            var wire = Serialize(result);
            // Reserve room for the JSON-RPC envelope. Measurement includes JSON escaping,
            // content blocks, structured content and metadata, rather than only text length.
            // At the supported 1 KiB minimum, a fixed 512-byte reserve leaves too little
            // room for both safety receipts. Keep envelope headroom proportional there.
            var envelopeReserve = Math.Min(512, config.MaxResponseBytesOrDefault / 4);
            var limit = Math.Min(config.MaxResponseBytesOrDefault - envelopeReserve,
                config.EnableResponseGuardOrDefault ? config.ResponseBudgetBytesOrDefault : int.MaxValue);
            var bytes = Encoding.UTF8.GetByteCount(wire);
            if (bytes > limit || (config.EnableResponseGuardOrDefault && bytes >= config.ResponseWarnBytesOrDefault))
            {
                JToken data;
                try { data = JToken.Parse(text ?? "{}"); }
                catch { data = new JObject { ["message"] = text ?? "" }; }
                if (result.Content?.Count != 1 || result.Content[0] is not TextContentBlock)
                    data = JObject.Parse(wire);
                var effective = config.ToRuntimeOptions();
                // The guard sees the exact MCP result bytes; its emergency envelope must
                // also fit the transport, even when optional warnings are disabled.
                effective.MaxResponseBytes = Math.Max(1024, limit);
                effective.ResponseBudgetBytes = Math.Max(1024, limit);
                effective.ResponseStrongWarnBytes = Math.Min(config.ResponseStrongWarnBytesOrDefault, effective.ResponseBudgetBytes.Value);
                effective.ResponseWarnBytes = Math.Min(config.ResponseWarnBytesOrDefault, effective.ResponseStrongWarnBytes.Value);
                var envelope = ResponseEnvelopeGuard.Apply(command, arguments, new JObject
                {
                    ["success"] = !failed,
                    ["data"] = data,
                    ["error"] = failed ? text : null
                }, effective, writer, wire);
                if (!envelope.Value<bool>("success"))
                {
                    result = TextResult(new JObject { ["success"] = false, ["error"] = envelope["error"] }, true);
                }
                else
                {
                    var guarded = envelope["data"] as JObject ?? new JObject { ["result"] = envelope["data"] };
                    if (envelope["warning"] != null) guarded["_response_warning"] = envelope["warning"];
                    result = TextResult(guarded, failed);
                }
                if (Encoding.UTF8.GetByteCount(Serialize(result)) > limit)
                {
                    var guarded = envelope["data"] as JObject;
                    result = TextResult(new JObject
                    {
                        ["success"] = !failed && envelope.Value<bool>("success"),
                        ["mutation_applied"] = ToolReadPolicy.IsReadOnly(command) ? new JValue(false)
                            : guarded?["mutation_applied"] ?? JValue.CreateNull(),
                        ["response_compacted"] = true,
                        ["original_byte_count"] = guarded?["original_byte_count"] ?? new JValue(bytes),
                        ["path"] = guarded?["path"] ?? guarded?["output_path"] ?? guarded?["summary"]?["output_path"],
                        ["error"] = failed ? "Operation failed; oversized error detail omitted."
                            : envelope.Value<bool>("success") ? null : "RESPONSE_TOO_LARGE: narrow the request; do not repeat the same unscoped call."
                    }, failed || !envelope.Value<bool>("success"));
                    if (Encoding.UTF8.GetByteCount(Serialize(result)) > limit)
                    {
                        var minimal = JObject.Parse(((TextContentBlock)result.Content[0]).Text);
                        minimal.Remove("path");
                        minimal["note"] = "Detail omitted to fit the response cap. Inspect local spill storage; do not rerun the command.";
                        result = TextResult(minimal, result.IsError == true);
                    }
                }
            }
            changes = ChangeSummary.ForAgent(changes);
            if (changes != null || history != null)
            {
                JObject data;
                var currentText = result.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text;
                try { data = JObject.Parse(currentText ?? "{}"); }
                catch { data = new JObject { [result.IsError == true ? "error" : "result"] = currentText }; }
                if (changes != null) data["_changes"] = changes;
                if (history != null) data["_history"] = history;
                result = TextResult(data, result.IsError == true);
                if (Encoding.UTF8.GetByteCount(Serialize(result)) > limit)
                {
                    if (changes != null) data["_changes"] = ChangeSummary.Omitted();
                    result = TextResult(data, result.IsError == true);
                    if (Encoding.UTF8.GetByteCount(Serialize(result)) > limit)
                        result = TextResult(new JObject
                        {
                            ["success"] = result.IsError != true,
                            ["response_compacted"] = true,
                            ["mutation_applied"] = JValue.CreateNull(),
                            ["_changes"] = changes == null ? null : ChangeSummary.Omitted(),
                            ["_history"] = history == null ? null : new JObject { ["status"] = history["status"], ["callId"] = history["callId"], ["modelKey"] = history["modelKey"], ["identityStatus"] = history["identityStatus"] }
                        }, result.IsError == true);
                }
            }
            if (safety?["_failure"] != null || safety?["_operation"] != null || safety?["_target"] != null)
            {
                JObject preserved;
                try { preserved = JObject.Parse(result.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "{}"); }
                catch { preserved = new JObject { ["success"] = !failed }; }
                SafetyResponseMetadata.Attach(preserved, safety);
                if (safety["target"] != null) preserved["target"] = safety["target"].DeepClone();
                if (failed) { preserved["success"] = false; preserved["error"] = safety["error"] ?? preserved["error"]; }
                result = TextResult(preserved, failed || result.IsError == true);
                if (Encoding.UTF8.GetByteCount(Serialize(result)) > limit)
                {
                    preserved = new JObject { ["success"] = !failed, ["error"] = safety["error"], ["response_compacted"] = true };
                    SafetyResponseMetadata.Attach(preserved, safety, compact: true);
                    if (safety["target"] is JObject target)
                        preserved["target"] = new JObject { ["code"] = target["code"], ["sent"] = target["sent"],
                            ["outcome"] = target["outcome"], ["summaryTruncated"] = true };
                    if (history != null) preserved["_history"] = new JObject { ["status"] = history["status"], ["callId"] = history["callId"] };
                    if (changes != null) preserved["_changes"] = ChangeSummary.Omitted();
                    result = McpSafetyResponseCompactor.Fit(preserved, failed || result.IsError == true, limit);
                }
            }
            CarryOnceFields(result, safety, previousOperations, limit);
            text = result.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text;
            if (IsFailure(text, command)) result.IsError = true;
            session?.RecordCall(command, arguments, result.IsError != true, durationMs,
                error: result.IsError == true ? text : null, resultJson: text, changes: changes);
            return result;
        }

        // Reported once, so they are added last, to the response the agent receives: dialogs the user handled (the
        // add-in has already let go of them) and operations that finished after their pending receipt. A plain-text
        // response carries neither. A finished operation that does not fit waits for the next response.
        private static void CarryOnceFields(CallToolResult result, JObject original, PreviousOperationTracker previousOperations, int limit)
        {
            var block = result.Content?.OfType<TextContentBlock>().FirstOrDefault();
            JObject data;
            try { data = JObject.Parse(block?.Text ?? ""); } catch (JsonException) { return; }
            var userDialogs = original?["_userDialogs"] as JObject ?? (original?["data"] as JObject)?["_userDialogs"] as JObject;
            if (userDialogs != null && data["_userDialogs"] == null)
            {
                data["_userDialogs"] = userDialogs.DeepClone();
                if (!TrySetText(result, block, data, limit)) data.Remove("_userDialogs");
            }
            var taken = previousOperations?.Attach(data);
            if (taken?.Count > 0 && !TrySetText(result, block, data, limit)) previousOperations.Return(taken);
        }

        private static bool TrySetText(CallToolResult result, TextContentBlock block, JObject data, int limit)
        {
            var before = block.Text;
            block.Text = data.ToString(Formatting.None);
            if (Encoding.UTF8.GetByteCount(Serialize(result)) <= limit) return true;
            block.Text = before;
            return false;
        }

        internal static bool IsFailure(string text, string command = null)
        {
            if (text?.TrimStart().StartsWith("Error:", StringComparison.OrdinalIgnoreCase) == true) return true;
            try
            {
                var obj = JObject.Parse(text ?? "{}");
                return ToolGateway.IsFailureOrPending(obj)
                    // A successful binding change is distinct from its unsuccessful probe.
                    || (command == "switch_target" && obj.Value<bool?>("verified") == false && obj.Value<string>("verifyError") != null)
                    || ResponseSizePolicyCatalog.GetOperationError(command, obj) != null;
            }
            catch { return false; }
        }

        internal static string Serialize(CallToolResult result) => System.Text.Json.JsonSerializer.Serialize(
            result, ModelContextProtocol.McpJsonUtilities.DefaultOptions);

        private static CallToolResult TextResult(JObject data, bool failed) => new CallToolResult
        {
            Content = new[] { new TextContentBlock { Text = data.ToString(Formatting.None) } },
            IsError = failed
        };
    }
}
