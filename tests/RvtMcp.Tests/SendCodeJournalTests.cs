using System;
using System.IO;
using RvtMcp.Plugin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("Sequential")]
    public class SendCodeJournalTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly RvtMcpConfig _activeConfig;
        private readonly RvtMcpConfig _inactiveConfig;

        public SendCodeJournalTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"sendcode-journal-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
            SendCodeJournal.LocalAppDataOverride = _tempDir;
            McpLogger.LocalAppDataOverride = _tempDir;
            McpLogger.Initialize();

            _activeConfig = new RvtMcpConfig
            {
                PersistSendCodeBodies = true,
                PersistSendCodeBodiesUntil = DateTimeOffset.UtcNow.AddDays(1).ToString("o")
            };

            _inactiveConfig = new RvtMcpConfig
            {
                PersistSendCodeBodies = false
            };
        }

        public void Dispose()
        {
            SendCodeJournal.LocalAppDataOverride = null;
            McpLogger.LocalAppDataOverride = null;
            if (Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, true); } catch { }
            }
        }

        [Fact]
        public void TryAppend_WhenInactive_DoesNotWriteFile()
        {
            var res = SendCodeJournal.TryAppend(_inactiveConfig, "session1", "var x = 1;", true, 50, null, "{}");
            Assert.False(res);
            Assert.False(File.Exists(SendCodeJournal.JournalPath));
        }

        [Fact]
        public void TryAppend_WhenActive_WritesRedactedJournal()
        {
            var code = "var token = \"secret_key\";\n// Path: C:\\Users\\MyUser\\Documents\\file.txt";
            var res = SendCodeJournal.TryAppend(_activeConfig, "session1", code, true, 50, "Some error occurred", "{\"data\":\"result\"}");
            
            Assert.True(res);
            Assert.True(File.Exists(SendCodeJournal.JournalPath));

            var lines = File.ReadAllLines(SendCodeJournal.JournalPath);
            Assert.Single(lines);

            var entry = JObject.Parse(lines[0]);
            Assert.Equal("session1", entry.Value<string>("session_id"));
            Assert.True(entry.Value<bool>("success"));
            Assert.Equal(50, entry.Value<long>("duration_ms"));
            Assert.Contains("Some error occurred", entry.Value<string>("error"));
            Assert.Equal(BakeRedactor.HashBody(code), entry.Value<string>("code_hash"));
            
            // Check redaction (path must be redacted to something generic like <PATH>)
            var loggedCode = entry.Value<string>("code");
            Assert.DoesNotContain("C:\\Users\\MyUser", loggedCode);
            Assert.Contains("<local_path>", loggedCode);
        }

        [Fact]
        public void Purge_DeletesFilesAfterSevenDaysOfDisabling()
        {
            // 1. Create a journal while active
            SendCodeJournal.TryAppend(_activeConfig, "session1", "var x = 1;", true, 50, null, null);
            Assert.True(File.Exists(SendCodeJournal.JournalPath));

            // 2. Mark config as inactive and call TryAppend with current time (sets disabled-at marker)
            var now = DateTimeOffset.UtcNow;
            SendCodeJournal.TryAppend(_inactiveConfig, "session1", "var x = 1;", true, 50, null, null, now);
            
            var markerFile = Path.Combine(_tempDir, "send-code-journal.disabled-at");
            Assert.True(File.Exists(markerFile));
            Assert.True(File.Exists(SendCodeJournal.JournalPath)); // Still exists, not 7 days yet

            // 3. TryAppend with now + 6 days -> still exists
            SendCodeJournal.TryAppend(_inactiveConfig, "session1", "var x = 1;", true, 50, null, null, now.AddDays(6));
            Assert.True(File.Exists(SendCodeJournal.JournalPath));

            // 4. TryAppend with now + 7.1 days -> deleted
            SendCodeJournal.TryAppend(_inactiveConfig, "session1", "var x = 1;", true, 50, null, null, now.AddDays(7.1));
            Assert.False(File.Exists(SendCodeJournal.JournalPath));
            Assert.False(File.Exists(markerFile));
        }

        [Fact]
        public void Rotation_MovesLargeFileToArchive()
        {
            // Write more than MaxFileSize bytes to the journal file directly
            var line = new string('x', 1024 * 1024); // 1MB line
            for (int i = 0; i < 6; i++)
            {
                File.AppendAllText(SendCodeJournal.JournalPath, line + "\n");
            }

            Assert.True(File.Exists(SendCodeJournal.JournalPath));
            Assert.True(new FileInfo(SendCodeJournal.JournalPath).Length > SendCodeJournal.MaxFileSize);

            // Append should trigger rotation
            var now = new DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);
            SendCodeJournal.TryAppend(_activeConfig, "session1", "var x = 1;", true, 50, null, null, now);

            // Original file should be small now (only containing the new line)
            Assert.True(File.Exists(SendCodeJournal.JournalPath));
            Assert.True(new FileInfo(SendCodeJournal.JournalPath).Length < 1000);

            // Archived file should exist
            var expectedArchive = Path.Combine(_tempDir, "send-code-journal-20260709-120000.jsonl");
            Assert.True(File.Exists(expectedArchive));
        }

        [Fact]
        public void TryFindCodeByHash_ReturnsRedactedBody_WhenJournalHasMatch()
        {
            var code = "var p = \"C:\\\\Users\\\\Me\\\\file.rvt\";";
            SendCodeJournal.TryAppend(_activeConfig, "session1", code, true, 50, null, null);

            var body = SendCodeJournal.TryFindCodeByHash(BakeRedactor.HashBody(code));

            Assert.NotNull(body);
            // Journal bodies are bake-redacted — placeholders, not the raw path.
            Assert.Contains("<project_file>", body);
            Assert.DoesNotContain("C:\\Users\\Me", body);
        }

        [Fact]
        public void TryFindCodeByHash_ReturnsNull_WhenMissingOrNoMatch()
        {
            Assert.Null(SendCodeJournal.TryFindCodeByHash("deadbeef")); // no journal file
            Assert.Null(SendCodeJournal.TryFindCodeByHash(null));

            SendCodeJournal.TryAppend(_activeConfig, "session1", "var x = 1;", true, 50, null, null);
            Assert.Null(SendCodeJournal.TryFindCodeByHash("deadbeef")); // file exists, hash doesn't
        }

        [Fact]
        public void TryFindCodeByHash_ScansRotatedArchives()
        {
            // Body lives only in a rotated archive — the live journal has no match.
            var code = "var archived = 42;";
            var hash = BakeRedactor.HashBody(code);
            var archiveLine = JsonConvert.SerializeObject(new
            {
                timestamp = "2026-09-01T00:00:00Z",
                session_id = "old-session",
                success = true,
                duration_ms = 1L,
                code_hash = hash,
                code_length = code.Length,
                error = (string)null,
                code,
                result = (string)null
            });
            File.WriteAllText(
                Path.Combine(_tempDir, "send-code-journal-20260901-000000.jsonl"),
                archiveLine + "\n");
            SendCodeJournal.TryAppend(_activeConfig, "session1", "var other = 0;", true, 10, null, null);

            Assert.Equal(code, SendCodeJournal.TryFindCodeByHash(hash));
        }

        [Fact]
        public void TryFindCodeByHash_LiveFileWinsOverArchive()
        {
            var code = "var x = 1;";
            var hash = BakeRedactor.HashBody(code);
            // Same hash in an archive with a different body — the live file is newer
            // and must win, so the lookup returns its (redacted) body first.
            var archiveLine = JsonConvert.SerializeObject(new
            {
                timestamp = "2026-09-01T00:00:00Z",
                session_id = "old",
                code_hash = hash,
                code = "STALE_ARCHIVE_BODY"
            });
            File.WriteAllText(
                Path.Combine(_tempDir, "send-code-journal-20260901-000000.jsonl"),
                archiveLine + "\n");
            SendCodeJournal.TryAppend(_activeConfig, "session1", code, true, 10, null, null);

            var body = SendCodeJournal.TryFindCodeByHash(hash);
            Assert.Equal("var x = 1;", body);
            Assert.NotEqual("STALE_ARCHIVE_BODY", body);
        }

        [Fact]
        public void RunMaintenance_CleansUpOrCreatesMarker_WithoutAppending()
        {
            // 1. Create a journal while active
            SendCodeJournal.TryAppend(_activeConfig, "session1", "var x = 1;", true, 50, null, null);
            Assert.True(File.Exists(SendCodeJournal.JournalPath));

            // 2. Run maintenance when config is inactive -> should create disabled-at marker, but NOT append any dummy log entry
            var now = DateTimeOffset.UtcNow;
            SendCodeJournal.RunMaintenance(_inactiveConfig, now);

            var markerFile = Path.Combine(_tempDir, "send-code-journal.disabled-at");
            Assert.True(File.Exists(markerFile));
            
            // Read lines to check if a new line was appended (should only have the 1 line from step 1)
            var lines = File.ReadAllLines(SendCodeJournal.JournalPath);
            Assert.Single(lines);

            // 3. Run maintenance 7.1 days later -> should delete the journal and marker
            SendCodeJournal.RunMaintenance(_inactiveConfig, now.AddDays(7.1));
            Assert.False(File.Exists(SendCodeJournal.JournalPath));
            Assert.False(File.Exists(markerFile));
        }
    }
}
