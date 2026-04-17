using Newtonsoft.Json.Linq;

namespace Bimwright.Plugin
{
    public static class SummaryGenerator
    {
        private const int MaxLength = 60;

        public static string Generate(string toolName, string paramsJson, string resultJson, bool success, string error)
        {
            if (!success)
                return Truncate(error ?? "Failed", MaxLength);

            try
            {
                var result = !string.IsNullOrEmpty(resultJson) ? JObject.Parse(resultJson) : null;
                var parms = !string.IsNullOrEmpty(paramsJson) ? JObject.Parse(paramsJson) : null;

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
                return success ? "OK" : "Failed";
            }
        }

        private static string FormatSendCode(JObject result)
        {
            var text = result?.Value<string>("result");
            if (string.IsNullOrEmpty(text)) return "OK (no output)";
            var firstLine = text.Split('\n')[0];
            return Truncate(firstLine, MaxLength);
        }

        private static string FormatAiFilter(JObject result)
        {
            var count = result?.Value<int?>("count");
            var category = result?.Value<string>("category");
            if (count.HasValue)
                return $"{count} {category ?? "elements"}";
            return "OK";
        }

        private static string FormatSelected(JObject result)
        {
            var count = result?.Value<int?>("count");
            return count.HasValue ? $"{count} elements selected" : "OK";
        }

        private static string FormatGeneric(string toolName, JObject result)
        {
            var rowCount = result?.Value<int?>("rowCount");
            if (rowCount.HasValue) return $"{rowCount} rows";
            var count = result?.Value<int?>("count");
            if (count.HasValue) return $"{count} items";
            return "OK";
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text.Length <= max ? text : text.Substring(0, max - 3) + "...";
        }
    }
}
