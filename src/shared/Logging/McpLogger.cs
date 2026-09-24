using System;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    public static class McpLogger
    {
        private static string _logPath;
        private static string _sessionId;
        public static string CurrentSessionId => _sessionId;
        /// <summary>Path of the active mcp-calls.jsonl, or null when logging is disabled.</summary>
        internal static string CurrentLogPath => _logPath;
        private const int LogVersion = 5;
        private const long MaxFileSize = 5 * 1024 * 1024; // 5MB
        // Field caps feed both the wire log and the History window's past-session view —
        // sized so most calls stay fully inspectable after the fact.
        private const int MaxLoggedParamsLength = 8 * 1024;
        private const int MaxLoggedResultLength = 10 * 1024;
        private const int MaxLoggedErrorLength = 4 * 1024;
        internal static string LocalAppDataOverride { get; set; }

        public static void Initialize()
        {
            var dir = Path.Combine(
                LocalAppDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RvtMcp");
            Directory.CreateDirectory(dir);
            _logPath = Path.Combine(dir, "mcp-calls.jsonl");
            _sessionId = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" +
                          Guid.NewGuid().ToString("N").Substring(0, 4);

            try { WithFileLock(_logPath, () => { RotateIfNeeded(dir); return true; }); }
            catch { _logPath = null; } // logging failure must not prevent add-in startup
        }

        /// <summary>
        /// Serialize file operations across threads, Revit versions and processes.
        /// Use the same lock for append, rotation and maintenance. Callbacks must be
        /// synchronous: mutex ownership is thread-affine. Timeout never writes unlocked.
        /// </summary>
        internal static T WithFileLock<T>(string path, Func<T> action, int timeoutMilliseconds = 2000)
        {
            var fullPath = Path.GetFullPath(path);
            var windows = Path.DirectorySeparatorChar == '\\';
            if (windows) fullPath = fullPath.ToUpperInvariant();
            // Global covers different Windows sessions of the same user sharing
            // LocalAppData. Hash the full path, not just the filename (test/profile isolation).
            var name = (windows ? @"Global\" : "") + "RvtMcp.Log." + BakeRedactor.HashBody(fullPath);
            using (var mutex = new Mutex(false, name))
            {
                var acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(timeoutMilliseconds); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired)
                        throw new TimeoutException("Timed out waiting for the MCP log file lock.");
                    return action();
                }
                finally
                {
                    if (acquired) mutex.ReleaseMutex();
                }
            }
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
                // Force rotate: format changed (old logs may lack result or send-code redaction)
                needsRotation = File.Exists(_logPath) || Directory.GetFiles(dir, "mcp-calls-*.jsonl").Length > 0;
            }
            else if (File.Exists(_logPath))
            {
                needsRotation = new FileInfo(_logPath).Length > MaxFileSize;
            }

            if (needsRotation)
            {
                var rotationSucceeded = true;
                try
                {
                    if (currentVersion < LogVersion)
                    {
                        File.Delete(_logPath);
                        foreach (var archive in Directory.GetFiles(dir, "mcp-calls-*.jsonl"))
                            File.Delete(archive);
                    }
                    else
                    {
                        var archive = Path.Combine(dir,
                            $"mcp-calls-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
                        File.Move(_logPath, archive);
                    }
                }
                catch
                {
                    rotationSucceeded = false;
                }

                if (!rotationSucceeded && currentVersion < LogVersion)
                {
                    _logPath = null;
                    return;
                }
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
                var safePayload = BuildLogSafePayload(toolName, paramsJson, code);

                var entry = new
                {
                    timestamp = DateTime.UtcNow.ToString("o"),
                    session_id = _sessionId,
                    tool = toolName,
                    success,
                    duration_ms = durationMs,
                    error = RedactAndTruncate(errorMsg, MaxLoggedErrorLength),
                    code = safePayload.Code,
                    @params = safePayload.Params,
                    result = BuildLogSafeResult(toolName, resultJson)
                };
                var line = JsonConvert.SerializeObject(entry, Formatting.None);
                WithFileLock(_logPath, () =>
                {
                    File.AppendAllText(_logPath, line + "\n");
                    return true;
                });
            }
            catch { }
        }

        internal static McpLogSafePayload BuildLogSafePayload(string toolName, string paramsJson, string code)
        {
            if (!string.Equals(toolName, "send_code_to_revit", StringComparison.OrdinalIgnoreCase))
            {
                return new McpLogSafePayload
                {
                    Code = null,
                    Params = ParseParams(RedactAndTruncate(paramsJson, MaxLoggedParamsLength))
                };
            }

            var codeBody = ExtractCodeBody(paramsJson, code);
            return new McpLogSafePayload
            {
                Code = null,
                Params = new JObject
                {
                    ["code_hash"] = BakeRedactor.HashBody(codeBody),
                    ["code_length"] = codeBody.Length
                }
            };
        }

        internal static string BuildLogSafeResult(string toolName, string resultJson)
        {
            if (resultJson == null)
                return null;

            var redactResultFields = string.Equals(toolName, "send_code_to_revit", StringComparison.OrdinalIgnoreCase);
            return RedactAndTruncate(resultJson, MaxLoggedResultLength, redactResultFields);
        }

        internal static string RedactAndTruncate(string value, int maxLength, bool redactResultFields = false)
        {
            if (value == null)
                return null;

            var redacted = BakeRedactor.RedactForBake(value, redactResultFields);
            return redacted.Length > maxLength ? redacted.Substring(0, maxLength) : redacted;
        }

        private static object ParseParams(string paramsJson)
        {
            try { return JToken.Parse(paramsJson); }
            catch { return paramsJson; }
        }

        private static string ExtractCodeBody(string paramsJson, string code)
        {
            if (!string.IsNullOrEmpty(code))
                return code;
            if (string.IsNullOrEmpty(paramsJson))
                return string.Empty;

            try
            {
                var parsed = JObject.Parse(paramsJson);
                var token = parsed["code"];
                if (token == null || token.Type == JTokenType.Null)
                    return string.Empty;
                if (token.Type == JTokenType.String)
                    return token.Value<string>() ?? string.Empty;

                return token.ToString(Formatting.None);
            }
            catch
            {
                return paramsJson;
            }
        }

        internal sealed class McpLogSafePayload
        {
            public object Params { get; set; }
            public string Code { get; set; }
        }
    }
}
