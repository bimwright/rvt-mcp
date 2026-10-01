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
                using (var capture = new ChangeCaptureContext())
                {
                    var result = await next(request, ct);
                    return Apply(request.Params, result, config, session, durationMs: clock.ElapsedMilliseconds,
                        changes: capture.Changes);
                }
            }));

        internal static CallToolResult Apply(CallToolRequestParams request, CallToolResult result,
            RvtMcpConfig config, SessionContext session = null, ResponseSpillWriter writer = null, long durationMs = 0,
            JObject changes = null)
        {
            var name = request?.Name ?? "";
            var command = name.StartsWith("revit_", StringComparison.Ordinal) ? name.Substring(6) : name;
            var arguments = System.Text.Json.JsonSerializer.Serialize(request?.Arguments);
            var text = result.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text;
            var failed = result.IsError == true || IsFailure(text, command);
            if (failed) result.IsError = true;
            var wire = Serialize(result);
            // Reserve room for the JSON-RPC envelope. Measurement includes JSON escaping,
            // content blocks, structured content and metadata, rather than only text length.
            var limit = Math.Min(config.MaxResponseBytesOrDefault - 512,
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
            if (changes != null)
            {
                JObject data;
                var currentText = result.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text;
                try { data = JObject.Parse(currentText ?? "{}"); }
                catch { data = new JObject { [result.IsError == true ? "error" : "result"] = currentText }; }
                data["_changes"] = changes;
                result = TextResult(data, result.IsError == true);
                if (Encoding.UTF8.GetByteCount(Serialize(result)) > limit)
                {
                    data["_changes"] = ChangeSummary.Omitted();
                    result = TextResult(data, result.IsError == true);
                    if (Encoding.UTF8.GetByteCount(Serialize(result)) > limit)
                        result = TextResult(new JObject
                        {
                            ["success"] = result.IsError != true,
                            ["response_compacted"] = true,
                            ["mutation_applied"] = JValue.CreateNull(),
                            ["_changes"] = ChangeSummary.Omitted()
                        }, result.IsError == true);
                }
            }
            text = result.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text;
            if (IsFailure(text, command)) result.IsError = true;
            session?.RecordCall(command, arguments, result.IsError != true, durationMs,
                error: result.IsError == true ? text : null, resultJson: text, changes: changes);
            return result;
        }

        internal static bool IsFailure(string text, string command = null)
        {
            if (text?.TrimStart().StartsWith("Error:", StringComparison.OrdinalIgnoreCase) == true) return true;
            try
            {
                var obj = JObject.Parse(text ?? "{}");
                return obj.Value<bool?>("ok") == false || obj.Value<bool?>("success") == false
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
