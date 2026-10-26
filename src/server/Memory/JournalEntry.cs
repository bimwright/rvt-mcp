using System;
using RvtMcp.Plugin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server.Memory
{
    public class JournalEntry
    {
        public string Timestamp { get; set; }
        public string Tool { get; set; }
        public bool Success { get; set; }
        public long DurationMs { get; set; }
        public string Error { get; set; }
        public string Params { get; set; }
        public string Result { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public JObject Changes { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public JObject Script { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public JObject Task { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public JObject Operation { get; set; }

        public static JournalEntry Create(string tool, string paramsJson, bool success,
            long durationMs, string error = null, string resultJson = null, JObject changes = null,
            string rejectedArguments = null)
            => CreateWithReceipt(tool, paramsJson, success, durationMs, error, resultJson, changes, rejectedArguments);

        internal static JournalEntry CreateWithReceipt(string tool, string paramsJson, bool success,
            long durationMs, string error = null, string resultJson = null, JObject changes = null,
            string rejectedArguments = null, JObject metadataReceipt = null)
        {
            JObject receipt = metadataReceipt;
            if (receipt == null) { try { receipt = JObject.Parse(resultJson ?? "{}"); } catch { } }
            return new JournalEntry
            {
                Timestamp = DateTime.UtcNow.ToString("o"),
                Tool = tool,
                Success = success,
                DurationMs = durationMs,
                Error = RedactAndTruncate(error, 2048),
                // Only the guard supplies this already bounded key-only summary; keep its JSON intact.
                Params = rejectedArguments == null ? RedactParams(tool, paramsJson)
                    : BakeRedactor.RedactForBake(rejectedArguments),
                Result = RedactResult(tool, resultJson),
                Script = RvtMcp.Scripting.ScriptTelemetry.BoundScript(receipt?["_script"] as JObject),
                Task = RvtMcp.Scripting.ScriptTelemetry.BoundTask(receipt?["_task"] as JObject),
                Operation = receipt?["_operation"] is JObject operation ? SafetyResponseMetadata.Bound(operation, 4096) : null,
                Changes = changes == null ? null : JObject.Parse(BakeRedactor.RedactForBake(
                    ChangeSummary.ForAgent(changes).ToString(Formatting.None)))
            };
        }

        private static string RedactParams(string tool, string paramsJson)
        {
            if (CodeModuleAudit.IsTool(tool)) return CodeModuleAudit.SafeArguments(paramsJson);
            if (!string.Equals(tool, "send_code_to_revit", StringComparison.OrdinalIgnoreCase))
                return RedactAndTruncate(paramsJson, 1024);

            var code = ExtractCodeBody(paramsJson);
            return RvtMcp.Scripting.ScriptTelemetry.SafeArguments(paramsJson, code).ToString(Formatting.None);
        }

        private static string RedactResult(string tool, string resultJson)
        {
            if (resultJson == null)
                return null;
            if (CodeModuleAudit.IsTool(tool)) resultJson = CodeModuleAudit.SafeResult(resultJson);

            var redactResultFields = string.Equals(tool, "send_code_to_revit", StringComparison.OrdinalIgnoreCase);
            return RedactAndTruncate(resultJson, 2048, redactResultFields);
        }

        private static string RedactAndTruncate(string value, int maxLength, bool redactResultFields = false)
        {
            if (value == null)
                return null;

            var redacted = BakeRedactor.RedactForBake(value, redactResultFields);
            return redacted.Length > maxLength ? redacted.Substring(0, maxLength) : redacted;
        }

        private static string ExtractCodeBody(string paramsJson)
        {
            if (string.IsNullOrEmpty(paramsJson))
                return string.Empty;

            try
            {
                var obj = JObject.Parse(paramsJson);
                var code = obj["code"];
                if (code == null || code.Type == JTokenType.Null)
                    return string.Empty;
                if (code.Type == JTokenType.String)
                    return code.Value<string>() ?? string.Empty;

                return code.ToString(Formatting.None);
            }
            catch (JsonException)
            {
                return paramsJson;
            }
        }
    }
}
