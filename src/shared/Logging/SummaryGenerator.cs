using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Localization;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// One path for every Summary cell: log-time (McpEventHandler), past-session
    /// load (SessionLogHistoryLoader), re-run, and language-change recompute
    /// (McpSessionLog.RefreshSummary). A params payload that already carries
    /// code_hash means the send_code body was privacy-redacted — the summary is
    /// the locked security note, not result text.
    /// </summary>
    public static class SummaryGenerator
    {
        private const int MaxLength = 60;

        public static string Generate(string toolName, string paramsJson, string resultJson, bool success, string error)
        {
            // Failed calls surface the real error before any JSON parsing —
            // truncated or non-object params must not swallow it.
            if (!success)
                return Truncate(error ?? L.T("history.summary.failed"), MaxLength);
            try
            {
                var parms = !string.IsNullOrEmpty(paramsJson) ? JObject.Parse(paramsJson) : null;

                if (toolName == "send_code_to_revit" && parms?["code_hash"] != null)
                {
                    return L.T("security.send_code.redacted_summary",
                        ("hash", parms.Value<string>("code_hash")),
                        ("length", parms.Value<long?>("code_length") ?? 0));
                }

                var result = !string.IsNullOrEmpty(resultJson) ? JObject.Parse(resultJson) : null;

                switch (toolName)
                {
                    case "send_code_to_revit":
                        return FormatSendCode(result);
                    case "ai_element_filter":
                        return FormatAiFilter(result);
                    case "get_selected_elements":
                        return FormatSelected(result);
                    default:
                        return FormatGeneric(toolName, result);
                }
            }
            catch
            {
                return L.T("history.summary.ok");
            }
        }

        private static string FormatSendCode(JObject result)
        {
            var text = result?.Value<string>("result");
            if (string.IsNullOrEmpty(text)) return L.T("history.summary.okNoOutput");
            var firstLine = text.Split('\n')[0];
            return Truncate(firstLine, MaxLength);
        }

        private static string FormatAiFilter(JObject result)
        {
            var count = result?.Value<int?>("count");
            var category = result?.Value<string>("category");
            if (count.HasValue)
                return L.T("history.summary.filterMatched",
                    ("count", count.Value),
                    ("category", category ?? L.T("history.summary.elementsFallback")));
            return L.T("history.summary.ok");
        }

        private static string FormatSelected(JObject result)
        {
            var count = result?.Value<int?>("count");
            return count.HasValue
                ? L.T("history.summary.selected", ("count", count.Value))
                : L.T("history.summary.ok");
        }

        private static string FormatGeneric(string toolName, JObject result)
        {
            var rowCount = result?.Value<int?>("rowCount");
            if (rowCount.HasValue) return L.T("history.summary.rows", ("count", rowCount.Value));
            var count = result?.Value<int?>("count");
            if (count.HasValue) return L.T("history.summary.items", ("count", count.Value));
            return L.T("history.summary.ok");
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text.Length <= max ? text : text.Substring(0, max - 3) + "...";
        }
    }
}
