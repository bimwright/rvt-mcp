using System;
using System.IO;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("Sequential")]
    public class SendCodeJournalGateTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _tempConfigPath;

        public SendCodeJournalGateTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"journal-gate-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
            SendCodeJournal.LocalAppDataOverride = _tempDir;
            McpLogger.LocalAppDataOverride = _tempDir;
            McpLogger.Initialize();

            _tempConfigPath = Path.Combine(_tempDir, "rvtmcp.config.json");
            // SendCodeJournalGate calls RvtMcpConfig.Load(args: null) with no path —
            // the override keeps this test off the user's real config file.
            RvtMcpConfig.ConfigFilePathOverride = _tempConfigPath;
            RvtMcpConfig.SavePersistSendCodeBodies(true, DateTimeOffset.UtcNow.AddHours(2), _tempConfigPath);
        }

        public void Dispose()
        {
            SendCodeJournal.LocalAppDataOverride = null;
            McpLogger.LocalAppDataOverride = null;
            RvtMcpConfig.ConfigFilePathOverride = null;
            if (Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, true); } catch { }
            }
        }

        [Fact]
        public void OnSendCodeLogged_WhenNonSendCodeTool_DoesNothing()
        {
            SendCodeJournalGate.OnSendCodeLogged(
                "get_current_view_info",
                "{}",
                null,
                true,
                10,
                null,
                "{}");

            Assert.False(File.Exists(SendCodeJournal.JournalPath));
        }

        [Fact]
        public void OnSendCodeLogged_WhenSendCodeTool_AppendsToJournal()
        {
            SendCodeJournalGate.OnSendCodeLogged(
                "send_code_to_revit",
                "{}",
                "var x = 1;",
                true,
                15,
                null,
                "{}");

            Assert.True(File.Exists(SendCodeJournal.JournalPath));
            var lines = File.ReadAllLines(SendCodeJournal.JournalPath);
            Assert.Single(lines);
            Assert.Contains("var x = 1;", lines[0]);
        }
    }
}
