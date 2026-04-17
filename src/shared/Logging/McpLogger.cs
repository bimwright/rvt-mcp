using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bimwright.Rvt.Plugin
{
    public static class McpLogger
    {
        private static string _logPath;
        private static string _sessionId;
        private const int LogVersion = 2;
        private const long MaxFileSize = 5 * 1024 * 1024; // 5MB

        public static void Initialize()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Bimwright");
            Directory.CreateDirectory(dir);
            _logPath = Path.Combine(dir, "mcp-calls.jsonl");
            _sessionId = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" +
                          Guid.NewGuid().ToString("N").Substring(0, 4);

            RotateIfNeeded(dir);
        }

        private static void RotateIfNeeded(string dir)
        {
            var versionFile = Path.Combine(dir, "mcp-calls.version");
            int currentVersion = 0;
            if (File.Exists(versionFile))
            {
                int.TryParse(File.ReadAllText(versionFile).Trim(), out currentVersion);
            }

            bool needsRotation = false;
            if (currentVersion < LogVersion)
            {
                // Force rotate: format changed (old logs lack result field)
                needsRotation = File.Exists(_logPath);
            }
            else if (File.Exists(_logPath))
            {
                needsRotation = new FileInfo(_logPath).Length > MaxFileSize;
            }

            if (needsRotation)
            {
                var archive = Path.Combine(dir,
                    $"mcp-calls-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
                try { File.Move(_logPath, archive); } catch { }
            }

            File.WriteAllText(versionFile, LogVersion.ToString());
        }

        public static void Log(string toolName, string paramsJson, bool success,
                                long durationMs, string errorMsg = null,
                                string code = null, string resultJson = null)
        {
            if (_logPath == null) return;
            try
            {
                // Truncate result for disk (max 2KB)
                string truncatedResult = null;
                if (resultJson != null)
                {
                    truncatedResult = resultJson.Length > 2048
                        ? resultJson.Substring(0, 2048)
                        : resultJson;
                }

                // Parse paramsJson to store as proper JSON object (not double-encoded string)
                object parsedParams = paramsJson;
                try { parsedParams = JToken.Parse(paramsJson); } catch { }

                var entry = new
                {
                    timestamp = DateTime.UtcNow.ToString("o"),
                    session_id = _sessionId,
                    tool = toolName,
                    success,
                    duration_ms = durationMs,
                    error = errorMsg,
                    code,
                    @params = parsedParams,
                    result = truncatedResult
                };
                var line = JsonConvert.SerializeObject(entry, Formatting.None);
                File.AppendAllText(_logPath, line + "\n");
            }
            catch { }
        }
    }
}
