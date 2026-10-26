using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("Sequential")]
    public class ScriptBaselineHistoryTests
    {
        [Fact]
        public void Legacy_archive_and_current_rows_load_without_rewriting_status_or_source_bytes()
        {
            var root = NewRoot();
            try
            {
                ScriptBaselineFixture.CopyHistory(root);
                var files = Directory.GetFiles(root).OrderBy(p => p, StringComparer.Ordinal).ToArray();
                var before = files.Select(File.ReadAllBytes).ToArray();
                var expected = files.Where(p => p.EndsWith(".jsonl", StringComparison.Ordinal))
                    .SelectMany(File.ReadAllLines).Select(JObject.Parse).ToArray();

                var entries = SessionLogHistoryLoader.LoadPastSessions(root, "unrelated-session", 0);

                Assert.Equal(expected.Length, entries.Count);
                Assert.Equal(expected.Select(r => r.Value<bool>("success")), entries.Select(e => e.Success));
                Assert.All(entries, e => Assert.True(e.IsHistorical));
                var mismatch = entries.Single(e => e.CallId == "fixture-call-1-0");
                Assert.True(mismatch.Success);
                var result = JObject.Parse(mismatch.ResultJson);
                Assert.False(result["result"]!.Value<bool>("passed"));
                Assert.Equal("unknown", result["_operation"]!.Value<string>("outcome"));
                Assert.All(expected, row => Assert.Null(row["usage_context"]));
                for (var i = 0; i < files.Length; i++) Assert.Equal(before[i], File.ReadAllBytes(files[i]));
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public void Logger_startup_with_current_marker_preserves_old_archives_and_appends()
        {
            var root = NewRoot();
            var previousOverride = McpLogger.LocalAppDataOverride;
            try
            {
                var logDir = Path.Combine(root, "Bimwright", "rvt-mcp");
                ScriptBaselineFixture.CopyHistory(logDir);
                var archive = Directory.GetFiles(logDir, "mcp-calls-*.jsonl").Single();
                var archiveBytes = File.ReadAllBytes(archive);
                var current = Path.Combine(logDir, "mcp-calls.jsonl");
                var currentBytes = File.ReadAllBytes(current);
                McpLogger.LocalAppDataOverride = root;

                McpLogger.Initialize(enabled: true);
                McpLogger.Log("get_current_view_info", "{}", true, 1, callId: "fixture-appended");

                Assert.Equal(archiveBytes, File.ReadAllBytes(archive));
                var appended = File.ReadAllBytes(current);
                Assert.True(appended.Length > currentBytes.Length);
                Assert.Equal(currentBytes, appended.Take(currentBytes.Length).ToArray());
                Assert.Equal("5", File.ReadAllText(Path.Combine(logDir, "mcp-calls.version")));
                Assert.Equal("fixture-appended", JObject.Parse(File.ReadAllLines(current).Last()).Value<string>("call_id"));
            }
            finally
            {
                McpLogger.LocalAppDataOverride = previousOverride;
                McpLogger.Initialize(enabled: false);
                Directory.Delete(root, recursive: true);
            }
        }

        private static string NewRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), "rvt-script-history-baseline-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }
    }
}
