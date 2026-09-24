using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("Sequential")]
    public class SessionLogHistoryLoaderTests : IDisposable
    {
        private readonly string _tempDir;

        public SessionLogHistoryLoaderTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"history-loader-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, true); } catch { }
            }
        }

        private static string Line(string sessionId, string tool,
            string paramsJson = null, string result = null, bool success = true,
            string error = null, string timestamp = "2026-09-22T10:00:00.0000000Z")
        {
            return JsonConvert.SerializeObject(new
            {
                timestamp,
                session_id = sessionId,
                tool,
                success,
                duration_ms = 42L,
                error,
                code = (string)null,
                @params = paramsJson == null ? null : JToken.Parse(paramsJson),
                result
            }, Formatting.None);
        }

        [Fact]
        public void LoadPastSessions_SkipsCurrentSessionRows()
        {
            File.WriteAllLines(Path.Combine(_tempDir, "mcp-calls.jsonl"), new[]
            {
                Line("20260921-090000-abcd", "create_level"),
                Line("20260922-100000-wxyz", "create_grid")
            });

            var entries = SessionLogHistoryLoader.LoadPastSessions(_tempDir, "20260922-100000-wxyz");

            Assert.Single(entries);
            Assert.Equal("create_level", entries[0].ToolName);
            Assert.True(entries[0].IsHistorical);
            Assert.Equal(-1, entries[0].Index);
        }

        [Fact]
        public void LoadPastSessions_ReadsArchivesThenCurrentFile_Chronological()
        {
            File.WriteAllLines(Path.Combine(_tempDir, "mcp-calls-20260901-000000.jsonl"), new[]
            {
                Line("20260901-080000-aaaa", "get_current_view_info")
            });
            File.WriteAllLines(Path.Combine(_tempDir, "mcp-calls.jsonl"), new[]
            {
                Line("20260922-100000-bbbb", "list_rooms")
            });

            var entries = SessionLogHistoryLoader.LoadPastSessions(_tempDir, "no-match-session");

            Assert.Equal(2, entries.Count);
            Assert.Equal("get_current_view_info", entries[0].ToolName);
            Assert.Equal("list_rooms", entries[1].ToolName);
            Assert.Equal(-2, entries[0].Index);
            Assert.Equal(-1, entries[1].Index);
        }

        [Fact]
        public void LoadPastSessions_SkipsMalformedLines()
        {
            File.WriteAllLines(Path.Combine(_tempDir, "mcp-calls.jsonl"), new[]
            {
                Line("20260921-090000-abcd", "create_level"),
                "this is not json",
                "{\"tool\":\"missing_session\"}",
                Line("20260921-090100-abcd", "create_grid")
            });

            var entries = SessionLogHistoryLoader.LoadPastSessions(_tempDir, "other");

            // "not json" is skipped; a line with tool but no session_id is kept
            // (permissive parse) with an unknown session tag.
            Assert.Equal(3, entries.Count);
            Assert.Equal("create_level", entries[0].ToolName);
            Assert.Equal("missing_session", entries[1].ToolName);
            Assert.Equal("?", entries[1].SessionTag);
            Assert.Equal("create_grid", entries[2].ToolName);
        }

        [Fact]
        public void LoadPastSessions_ConvertsUtcTimestampToLocal()
        {
            const string ts = "2026-09-22T02:30:00.0000000Z";
            File.WriteAllLines(Path.Combine(_tempDir, "mcp-calls.jsonl"), new[]
            {
                Line("20260921-090000-abcd", "create_level", timestamp: ts)
            });

            var entries = SessionLogHistoryLoader.LoadPastSessions(_tempDir, "other");

            var expected = DateTimeOffset.Parse(ts).LocalDateTime;
            Assert.Single(entries);
            Assert.Equal(expected, entries[0].Timestamp);
            Assert.Equal(expected.ToString("MM-dd HH:mm"), entries[0].TimeLabel);
        }

        [Fact]
        public void LoadPastSessions_CapsToNewestEntries()
        {
            File.WriteAllLines(Path.Combine(_tempDir, "mcp-calls.jsonl"), new[]
            {
                Line("20260921-090000-abcd", "tool_oldest"),
                Line("20260921-090100-abcd", "tool_middle"),
                Line("20260921-090200-abcd", "tool_newest")
            });

            var entries = SessionLogHistoryLoader.LoadPastSessions(_tempDir, "other", maxEntries: 2);

            Assert.Equal(2, entries.Count);
            Assert.Equal("tool_middle", entries[0].ToolName);
            Assert.Equal("tool_newest", entries[1].ToolName);
        }

        [Fact]
        public void LoadPastSessions_MapsFields_AndBuildsSessionTag()
        {
            File.WriteAllLines(Path.Combine(_tempDir, "mcp-calls.jsonl"), new[]
            {
                Line("20260921-093015-abcd", "create_level",
                    paramsJson: "{\"elevation\":3000}",
                    result: "{\"created\":true}",
                    error: "boom", success: false)
            });

            var entries = SessionLogHistoryLoader.LoadPastSessions(_tempDir, "other");

            var e = entries[0];
            Assert.False(e.Success);
            Assert.Equal(42, e.DurationMs);
            Assert.Equal("boom", e.ErrorMessage);
            Assert.Contains("3000", e.ParamsJson);
            Assert.Equal("{\"created\":true}", e.ResultJson);
            Assert.Equal("0921-0930", e.SessionTag);
            Assert.False(string.IsNullOrEmpty(e.Summary));
        }

        [Fact]
        public void LoadPastSessions_MissingDir_ReturnsEmpty()
        {
            var entries = SessionLogHistoryLoader.LoadPastSessions(
                Path.Combine(_tempDir, "does-not-exist"), "x");
            Assert.Empty(entries);
        }
    }
}
