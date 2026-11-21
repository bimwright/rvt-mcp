using System;
using System.IO;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Plugin.Views.Toast;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class AgentDisplayNameTests
    {
        [Theory]
        [InlineData("claude-code", "Claude")]
        [InlineData("claude-ai", "Claude")]
        [InlineData("codex-mcp-client", "Codex")]
        [InlineData("Cline", "Cline")]
        [InlineData("cursor-vscode", "Cursor")]
        [InlineData("Windsurf", "Windsurf")]
        [InlineData("antigravity-client", "Antigravity")]
        [InlineData("gemini-cli-mcp-client", "Gemini")]
        [InlineData("Visual Studio Code", "VS Code")]
        public void Known_clients_get_their_product_name(string clientName, string expected) =>
            Assert.Equal(expected, AgentDisplayName.For(clientName));

        [Theory]
        [InlineData(null, null)]
        [InlineData("   ", null)]
        [InlineData("‮​", null)]
        [InlineData("rmcp", "rmcp")]
        [InlineData("  my\tagent \n x ", "my agent x")]
        [InlineData("a‮b​c\u0007d", "abc d")]
        public void Other_names_are_shown_as_sent_without_invisible_or_control_characters(string clientName, string expected) =>
            Assert.Equal(expected, AgentDisplayName.For(clientName));

        [Fact]
        public void Long_names_are_capped()
        {
            var shown = AgentDisplayName.For(new string('x', 500));
            Assert.Equal(AgentDisplayName.MaxLength, shown.Length);
            Assert.EndsWith("…", shown);
        }
    }

    [Collection("ServerStateConfig")]
    public sealed class ToastSnoozeTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "rvtmcp-snooze-" + Guid.NewGuid().ToString("N"));
        private readonly string _previousPath = RvtMcpConfig.ConfigFilePathOverride;
        private readonly Func<DateTime> _previousNow = ToastSnooze.Now;
        private DateTime _now = new DateTime(2026, 11, 21, 23, 59, 0);

        public ToastSnoozeTests()
        {
            Directory.CreateDirectory(_directory);
            RvtMcpConfig.ConfigFilePathOverride = Path.Combine(_directory, "rvtmcp.config.json");
            ToastSnooze.Now = () => _now;
        }

        [Fact]
        public void A_pause_lasts_until_local_midnight_and_keeps_other_preferences()
        {
            File.WriteAllText(RvtMcpConfig.ConfigFilePathOverride, "{\"enableToast\":true,\"showBranding\":true}");
            Assert.False(ToastSnooze.IsActive());
            var changed = 0;
            Action onChanged = () => changed++;
            ToastSnooze.Changed += onChanged;
            try
            {
                Assert.True(ToastSnooze.PauseForToday(out var error), error);
                Assert.True(ToastSnooze.IsActive());
                var saved = JObject.Parse(File.ReadAllText(RvtMcpConfig.ConfigFilePathOverride));
                Assert.Equal("2026-11-21", saved.Value<string>("toastSnoozedDate"));
                Assert.True(saved.Value<bool>("showBranding"));

                _now = new DateTime(2026, 11, 22, 0, 0, 0);
                Assert.False(ToastSnooze.IsActive());

                _now = new DateTime(2026, 11, 21, 12, 0, 0);
                Assert.True(ToastSnooze.IsActive());
                Assert.True(ToastSnooze.Resume(out error), error);
                Assert.False(ToastSnooze.IsActive());
                Assert.Null(JObject.Parse(File.ReadAllText(RvtMcpConfig.ConfigFilePathOverride))["toastSnoozedDate"]);
                Assert.Equal(2, changed);
            }
            finally { ToastSnooze.Changed -= onChanged; }
        }

        [Fact]
        public void Turning_toasts_on_ends_only_an_active_pause()
        {
            Assert.True(ToastSnooze.ResumeIfPaused(out var error), error);
            Assert.False(File.Exists(RvtMcpConfig.ConfigFilePathOverride));
            Assert.True(ToastSnooze.PauseForToday(out error), error);
            Assert.True(ToastSnooze.ResumeIfPaused(out error), error);
            Assert.False(ToastSnooze.IsActive());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("tomorrow")]
        [InlineData("2026-11-20")]
        [InlineData("2026-11-21T10:00:00")]
        public void Missing_old_or_malformed_dates_do_not_pause(string value) =>
            Assert.False(ToastSnooze.IsActive(value, new DateTime(2026, 11, 21, 9, 0, 0)));

        public void Dispose()
        {
            RvtMcpConfig.ConfigFilePathOverride = _previousPath;
            ToastSnooze.Now = _previousNow;
            try { Directory.Delete(_directory, true); } catch { }
        }
    }
}
