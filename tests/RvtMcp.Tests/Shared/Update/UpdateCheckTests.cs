using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RvtMcp.Plugin.Update;
using Xunit;

namespace RvtMcp.Tests
{
    public class UpdateCheckTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "rvtmcp-update-" + Guid.NewGuid().ToString("N"));
        private string StatePath => Path.Combine(_dir, "update-check.json");

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static string Release(string tag, bool prerelease = false, bool draft = false) =>
            "{\"tag_name\":\"" + tag + "\",\"prerelease\":" + (prerelease ? "true" : "false") +
            ",\"draft\":" + (draft ? "true" : "false") +
            ",\"html_url\":\"https://github.com/bimwright/rvt-mcp/releases/tag/" + tag + "\"}";

        [Theory]
        [InlineData("0.8.2", "0.8.10", -1)]
        [InlineData("v0.9.0", "0.8.99", 1)]
        [InlineData("1.0.0", "1.0.0-rc.1", 1)]
        [InlineData("1.0.0-rc.2", "1.0.0-rc.10", -1)]
        [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
        [InlineData("0.8.2+abc", "v0.8.2", 0)]
        public void Semver_precedence(string a, string b, int expected)
        {
            Assert.True(SemanticVersion.TryParse(a, out var va));
            Assert.True(SemanticVersion.TryParse(b, out var vb));
            Assert.Equal(expected, Math.Sign(va.CompareTo(vb)));
        }

        [Theory]
        [InlineData("")]
        [InlineData("latest")]
        [InlineData("v1")]
        [InlineData("1.2.x")]
        public void Semver_rejects_garbage(string text) => Assert.False(SemanticVersion.TryParse(text, out _));

        [Fact]
        public void Prerelease_and_draft_releases_are_ignored()
        {
            Assert.Null(UpdateChecker.ParseLatestRelease(Release("v0.9.0", prerelease: true)));
            Assert.Null(UpdateChecker.ParseLatestRelease(Release("v0.9.0", draft: true)));
            Assert.Null(UpdateChecker.ParseLatestRelease(Release("v0.9.0-beta.1")));
            Assert.Null(UpdateChecker.ParseLatestRelease("not json"));
            Assert.Equal("v0.9.0", UpdateChecker.ParseLatestRelease(Release("v0.9.0")).Item1);
        }

        [Fact]
        public async Task Newer_release_is_reported_and_cached_for_24_hours()
        {
            var calls = 0;
            Func<CancellationToken, Task<string>> fetch = _ => { calls++; return Task.FromResult(Release("v0.9.0")); };
            var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

            var info = await UpdateChecker.CheckAsync("0.8.2", StatePath, fetch, now);
            Assert.NotNull(info);
            Assert.Equal("v0.9.0", info.LatestVersion);
            Assert.Equal("v0.8.2", info.CurrentVersion);
            Assert.Equal(1, calls);

            await UpdateChecker.CheckAsync("0.8.2", StatePath, fetch, now.AddHours(23));
            Assert.Equal(1, calls);

            await UpdateChecker.CheckAsync("0.8.2", StatePath, fetch, now.AddHours(25));
            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task Failures_are_silent_and_still_count_as_the_daily_attempt()
        {
            var calls = 0;
            Func<CancellationToken, Task<string>> fetch = _ => { calls++; throw new InvalidOperationException("offline"); };
            var now = DateTime.UtcNow;
            Assert.Null(await UpdateChecker.CheckAsync("0.8.2", StatePath, fetch, now));
            Assert.Null(await UpdateChecker.CheckAsync("0.8.2", StatePath, fetch, now.AddHours(1)));
            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task Same_or_older_release_is_not_reported()
        {
            Assert.Null(await UpdateChecker.CheckAsync("0.8.2", StatePath, _ => Task.FromResult(Release("v0.8.2")), DateTime.UtcNow));
            Assert.Null(UpdateChecker.FromCache("0.9.1", StatePath));
        }

        [Fact]
        public async Task Skipped_version_is_flagged_until_a_newer_one_appears()
        {
            var now = DateTime.UtcNow;
            await UpdateChecker.CheckAsync("0.8.2", StatePath, _ => Task.FromResult(Release("v0.9.0")), now);
            UpdateChecker.SkipVersion("v0.9.0", StatePath);
            Assert.True(UpdateChecker.FromCache("0.8.2", StatePath).IsSkipped);

            await UpdateChecker.CheckAsync("0.8.2", StatePath, _ => Task.FromResult(Release("v0.9.1")), now.AddDays(2));
            var info = UpdateChecker.FromCache("0.8.2", StatePath);
            Assert.False(info.IsSkipped);
            Assert.Equal("v0.9.1", info.LatestVersion);
        }

        [Fact]
        public void Agent_prompt_names_both_versions_and_the_release_page()
        {
            var info = new UpdateInfo { CurrentVersion = "v0.8.2", LatestVersion = "v0.9.0", ReleaseUrl = "https://github.com/bimwright/rvt-mcp/releases/tag/v0.9.0" };
            var prompt = UpdateNoticeText.BuildAgentPrompt(info);
            Assert.Contains("v0.8.2", prompt);
            Assert.Contains("v0.9.0", prompt);
            Assert.Contains(info.ReleaseUrl, prompt);
        }

        [Fact]
        public void Composed_instructions_lead_with_the_notice_and_stay_under_the_cap()
        {
            var baseText = "keywords paragraph\n\n" + string.Join("\n", new string[40].Select((_, i) => "- toolset" + i + ": tool_a, tool_b"));
            var notice = "rvt-mcp v0.9.0 is available (running v0.8.2).";
            var text = UpdateNoticeText.ComposeInstructions(baseText, notice, 512);
            Assert.StartsWith("UPDATE: " + notice, text);
            Assert.True(Encoding.UTF8.GetByteCount(text) <= 512);
            Assert.Contains("keywords paragraph", text);
            Assert.Equal(baseText, UpdateNoticeText.ComposeInstructions(baseText, null));
        }
    }
}
