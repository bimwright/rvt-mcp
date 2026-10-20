using System;
using System.IO;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests;

public class JournalDurationTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rvt-journal-duration-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(1)] [InlineData(4)] [InlineData(8)] [InlineData(12)]
    [InlineData(24)] [InlineData(48)] [InlineData(168)] [InlineData(336)]
    public void Settings_duration_is_preserved_through_save_and_reload(int hours)
    {
        var path = Path.Combine(_folder, "config.json");
        var before = DateTimeOffset.UtcNow;
        var result = RvtMcpConfig.TryApplySettings(new RvtMcpConfig.SettingsPatch { PersistSendCodeBodiesHours = hours, PersistSendCodeBodies = true }, path);
        Assert.Empty(result.Failed);
        var loaded = RvtMcpConfig.LoadReadOnly(path, _ => null);
        Assert.Equal(hours, loaded.PersistSendCodeBodiesHoursOrDefault);
        Assert.InRange(DateTimeOffset.Parse(loaded.PersistSendCodeBodiesUntil), before.AddHours(hours).AddSeconds(-1), DateTimeOffset.UtcNow.AddHours(hours));
    }

    [Theory]
    [InlineData("7d", 7)] [InlineData("14d", 14)] [InlineData("100d", 14)]
    public void Explicit_cli_journal_windows_support_up_to_fourteen_days(string ttl, int days)
    {
        var before = DateTimeOffset.UtcNow;
        var loaded = RvtMcpConfig.Load(new[] { "--persist-send-code-bodies-for", ttl }, Path.Combine(_folder, "config.json"), _ => null);
        Assert.InRange(DateTimeOffset.Parse(loaded.PersistSendCodeBodiesUntil), before.AddDays(days).AddSeconds(-1), DateTimeOffset.UtcNow.AddDays(days));
    }
    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
}
