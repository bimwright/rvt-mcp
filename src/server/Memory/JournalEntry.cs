using System;

namespace RevitMcp.Server.Memory
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

        public static JournalEntry Create(string tool, string paramsJson, bool success,
            long durationMs, string error = null, string resultJson = null)
        {
            return new JournalEntry
            {
                Timestamp = DateTime.UtcNow.ToString("o"),
                Tool = tool,
                Success = success,
                DurationMs = durationMs,
                Error = error,
                Params = paramsJson?.Length > 1024 ? paramsJson.Substring(0, 1024) : paramsJson,
                Result = resultJson?.Length > 2048 ? resultJson.Substring(0, 2048) : resultJson
            };
        }
    }
}
