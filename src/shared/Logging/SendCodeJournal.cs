using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    public static class SendCodeJournal
    {
        public const long MaxFileSize = 5 * 1024 * 1024; // 5MB
        public static string LocalAppDataOverride { get; set; }

        private static string RootDir =>
            LocalAppDataOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RvtMcp");

        public static string JournalPath => Path.Combine(RootDir, "send-code-journal.jsonl");

        public static void RunMaintenance(RvtMcpConfig config, DateTimeOffset? now = null)
        {
            try
            {
                McpLogger.WithFileLock(JournalPath, () =>
                {
                    var utc = now ?? DateTimeOffset.UtcNow;
                    var root = RootDir;
                    Directory.CreateDirectory(root);
                    var isActive = config != null && config.IsPersistSendCodeBodiesActive(utc);
                    MaybePurge(root, isActive, utc);
                    return true;
                });
            }
            catch { } // best-effort maintenance; never mutate without the lock
        }

        public static bool TryAppend(
            RvtMcpConfig config,
            string sessionId,
            string rawCode,
            bool success,
            long durationMs,
            string error,
            string resultJson,
            DateTimeOffset? now = null)
        {
            try
            {
                return McpLogger.WithFileLock(JournalPath, () => AppendUnderLock(
                    config, sessionId, rawCode, success, durationMs, error, resultJson, now));
            }
            catch { return false; } // includes lock timeout; no unlocked fallback
        }

        private static bool AppendUnderLock(
            RvtMcpConfig config, string sessionId, string rawCode, bool success,
            long durationMs, string error, string resultJson, DateTimeOffset? now)
        {
            var utc = now ?? DateTimeOffset.UtcNow;
            var root = RootDir;
            Directory.CreateDirectory(root);

            var isActive = config != null && config.IsPersistSendCodeBodiesActive(utc);
            
            // Maintenance: purge if inactive/expired, remove marker if active
            MaybePurge(root, isActive, utc);

            if (!isActive)
                return false;

            RotateIfNeeded(root, utc);

            var raw = rawCode ?? string.Empty;
            var entry = new
            {
                timestamp = utc.ToString("o"),
                session_id = sessionId,
                success,
                duration_ms = durationMs,
                code_hash = BakeRedactor.HashBody(raw),
                code_length = raw.Length,
                error = McpLogger.RedactAndTruncate(error, 2048),
                code = BakeRedactor.RedactForBake(raw, redactResultFields: true),
                result = McpLogger.BuildLogSafeResult("send_code_to_revit", resultJson)
            };

            try
            {
                var line = JsonConvert.SerializeObject(entry, Formatting.None);
                File.AppendAllText(JournalPath, line + "\n");
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Journal body matching a code_hash, or null. Scans the live journal then
        /// rotated archives newest-first; first match wins (same hash ⇒ same body).
        /// Bodies are bake-redacted (paths/secrets become placeholders) — callers
        /// re-running a recovered body must surface that caveat.
        /// </summary>
        public static string TryFindCodeByHash(string codeHash)
        {
            if (string.IsNullOrEmpty(codeHash)) return null;
            try
            {
                // File.ReadLines otherwise denies a concurrent writer's open on Windows.
                return McpLogger.WithFileLock(JournalPath, () =>
                {
                    foreach (var file in JournalFilesNewestFirst())
                    {
                        var body = FindInFile(file, codeHash);
                        if (body != null) return body;
                    }
                    return (string)null;
                });
            }
            catch { return null; }
        }

        private static IEnumerable<string> JournalFilesNewestFirst()
        {
            var dir = Path.GetDirectoryName(JournalPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) yield break;

            if (File.Exists(JournalPath)) yield return JournalPath;

            // Archive names carry a timestamp → ordinal-desc = newest first.
            var archives = Directory.GetFiles(dir, "send-code-journal-*.jsonl");
            Array.Sort(archives, StringComparer.OrdinalIgnoreCase);
            for (var i = archives.Length - 1; i >= 0; i--)
                yield return archives[i];
        }

        private static string FindInFile(string path, string codeHash)
        {
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var obj = JObject.Parse(line);
                        if (string.Equals(obj.Value<string>("code_hash"), codeHash, StringComparison.Ordinal))
                            return obj.Value<string>("code");
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private static void MaybePurge(string rootDir, bool isActive, DateTimeOffset now)
        {
            var disabledMarker = Path.Combine(rootDir, "send-code-journal.disabled-at");
            if (isActive)
            {
                if (File.Exists(disabledMarker))
                {
                    try { File.Delete(disabledMarker); } catch { }
                }
                return;
            }

            // Write marker if not present and journal files exist
            var journal = Path.Combine(rootDir, "send-code-journal.jsonl");
            bool hasFiles = File.Exists(journal) || (Directory.Exists(rootDir) && Directory.GetFiles(rootDir, "send-code-journal-*.jsonl").Length > 0);
            if (hasFiles && !File.Exists(disabledMarker))
            {
                try
                {
                    File.WriteAllText(disabledMarker, now.ToString("o"));
                }
                catch { }
            }

            if (File.Exists(disabledMarker))
            {
                try
                {
                    var text = File.ReadAllText(disabledMarker);
                    if (DateTimeOffset.TryParse(text, out var disabledAt))
                    {
                        if (now - disabledAt >= TimeSpan.FromDays(7))
                        {
                            if (File.Exists(journal)) File.Delete(journal);
                            foreach (var f in Directory.GetFiles(rootDir, "send-code-journal-*.jsonl"))
                            {
                                File.Delete(f);
                            }
                            File.Delete(disabledMarker);
                        }
                    }
                }
                catch { }
            }
        }

        private static void RotateIfNeeded(string rootDir, DateTimeOffset now)
        {
            var journal = Path.Combine(rootDir, "send-code-journal.jsonl");
            if (File.Exists(journal) && new FileInfo(journal).Length > MaxFileSize)
            {
                try
                {
                    var archive = Path.Combine(rootDir,
                        $"send-code-journal-{now.ToString("yyyyMMdd-HHmmss")}.jsonl");
                    File.Move(journal, archive);
                }
                catch { }
            }
        }
    }
}
