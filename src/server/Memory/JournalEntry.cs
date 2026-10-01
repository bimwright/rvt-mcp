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

        public static JournalEntry Create(string tool, string paramsJson, bool success,
            long durationMs, string error = null, string resultJson = null, JObject changes = null)
        {
            return new JournalEntry
            {
                Timestamp = DateTime.UtcNow.ToString("o"),
                Tool = tool,
                Success = success,
                DurationMs = durationMs,
                Error = RedactAndTruncate(error, 2048),
                Params = RedactParams(tool, paramsJson),
                Result = RedactResult(tool, resultJson),
                Changes = changes == null ? null : JObject.Parse(BakeRedactor.RedactForBake(
                    ChangeSummary.ForAgent(changes).ToString(Formatting.None)))
            };
        }

        private static string RedactParams(string tool, string paramsJson)
        {
            if (!string.Equals(tool, "send_code_to_revit", StringComparison.OrdinalIgnoreCase))
                return RedactAndTruncate(paramsJson, 1024);

            var code = ExtractCodeBody(paramsJson);
            return JsonConvert.SerializeObject(new
            {
                code_hash = BakeRedactor.HashBody(code),
                code_length = code.Length
            });
        }

        private static string RedactResult(string tool, string resultJson)
        {
            if (resultJson == null)
                return null;

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
