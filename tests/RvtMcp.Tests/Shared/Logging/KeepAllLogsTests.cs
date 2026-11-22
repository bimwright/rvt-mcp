using System;
using System.IO;
using System.Linq;
using RvtMcp.Plugin;
using RvtMcp.Plugin.Reports.Storage;
using Xunit;

namespace RvtMcp.Tests;

/// <summary>
/// BIMWRIGHT_KEEP_ALL_LOGS on: automatic deletion is skipped, rotation still archives.
/// The policy is process-wide, so these tests run alone and restore it to off.
/// </summary>
[Collection("ServerStateConfig")]
public sealed class KeepAllLogsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rvtmcp-keepall-" + Guid.NewGuid().ToString("N"));
    private readonly string _previousDiagnosticRoot = DiagnosticLog.LocalAppDataOverride;

    public KeepAllLogsTests()
    {
        Assert.True(DiagnosticLog.Flush(5000));
        LogRetentionPolicy.KeepAllOverride = true;
    }

    public void Dispose()
    {
        try { DiagnosticLog.Flush(5000); }
        finally
        {
            LogRetentionPolicy.KeepAllOverride = false;
            DiagnosticLog.LocalAppDataOverride = _previousDiagnosticRoot;
            SendCodeJournal.LocalAppDataOverride = null;
            McpLogger.LocalAppDataOverride = null;
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(" yes ", true)]
    [InlineData("On", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    [InlineData("off", false)]
    [InlineData("2", false)]
    [InlineData("enabled", false)]
    public void Keep_all_is_on_only_for_1_true_yes_on_trimmed_and_case_insensitive(string value, bool expected)
        => Assert.Equal(expected, LogRetentionPolicy.IsTruthy(value));

    [Fact]
    public void Keep_all_test_seam_overrides_the_environment_both_ways()
    {
        LogRetentionPolicy.KeepAllOverride = false;
        Assert.False(LogRetentionPolicy.KeepAll);
        LogRetentionPolicy.KeepAllOverride = true;
        Assert.True(LogRetentionPolicy.KeepAll);
    }

    [Fact]
    public void Keep_all_keeps_generated_archives_older_than_30_days()
    {
        Directory.CreateDirectory(_root);
        var old = Path.Combine(_root, "usage-20200101-000000.jsonl");
        File.WriteAllText(old, "old");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-31));

        BoundedLogFile.Append(Path.Combine(_root, "usage.jsonl"), "new");
        BoundedLogFile.MaintainGroup(_root, "usage");

        Assert.Equal("old", File.ReadAllText(old));
    }

    [Fact]
    public void Keep_all_keeps_a_group_over_100_MB_and_still_appends()
    {
        Directory.CreateDirectory(_root);
        var big = Path.Combine(_root, "usage-20200101-000000.jsonl");
        using (var stream = new FileStream(big, FileMode.CreateNew)) stream.SetLength(BoundedLogFile.RecordGroupBytes + 1024 * 1024);
        var path = Path.Combine(_root, "usage.jsonl");

        BoundedLogFile.Append(path, "new");
        BoundedLogFile.MaintainGroup(_root, "usage");

        Assert.Equal(BoundedLogFile.RecordGroupBytes + 1024 * 1024, new FileInfo(big).Length);
        Assert.Equal(new[] { "new" }, File.ReadAllLines(path));
    }

    [Fact]
    public void Keep_all_still_rotates_an_oversized_or_stale_active_log_to_an_archive()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "usage.jsonl");
        // 9-byte records, 30-byte files: three per file, then rotation; 90 bytes exceed the 60-byte group.
        for (var i = 0; i < 10; i++) BoundedLogFile.Append(path, "record " + i, fileBytes: 30, groupBytes: 60);
        var files = BoundedLogFile.Files(_root, "usage");
        Assert.Equal(4, files.Length);
        Assert.Equal(new[] { "record 9" }, File.ReadAllLines(path));
        Assert.Equal(Enumerable.Range(0, 10).Select(i => "record " + i),
            files.SelectMany(File.ReadAllLines).OrderBy(line => line, StringComparer.Ordinal));

        var stale = Path.Combine(_root, "stale");
        Directory.CreateDirectory(stale);
        var active = Path.Combine(stale, "usage.jsonl");
        File.WriteAllText(active, "old\n");
        File.SetLastWriteTimeUtc(active, DateTime.UtcNow.AddDays(-31));
        BoundedLogFile.MaintainGroup(stale, "usage");
        var archive = Assert.Single(BoundedLogFile.Files(stale, "usage"));
        Assert.NotEqual(active, archive);
        Assert.Equal("old\n", File.ReadAllText(archive));
    }

    [Fact]
    public void Keep_all_archives_the_diagnostic_log_with_a_timestamp_instead_of_overwriting_the_backup()
    {
        DiagnosticLog.LocalAppDataOverride = _root;
        var dir = Path.Combine(_root, "Bimwright", "rvt-mcp");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "rvt-mcp.log");
        var oversized = string.Concat(Enumerable.Repeat(new string('x', 1023) + "\n", 11000));
        File.WriteAllText(path, oversized);
        File.WriteAllText(path + ".1", "previous backup\n");

        DiagnosticLog.Write("Pipe", "keep-all rotation check", "2024");
        Assert.True(DiagnosticLog.Flush(5000));

        Assert.Equal("previous backup\n", File.ReadAllText(path + ".1"));
        var archive = Assert.Single(Directory.GetFiles(dir, "rvt-mcp-*.log"));
        Assert.Equal(oversized.Length, new FileInfo(archive).Length); // neither compacted nor overwritten
        Assert.Contains("keep-all rotation check", File.ReadAllText(path));
    }

    [Fact]
    public void Keep_all_leaves_legacy_logs_untrimmed_and_notes_keep_all_in_the_diagnostic_log_at_startup()
    {
        DiagnosticLog.LocalAppDataOverride = _root;
        var dir = Path.Combine(_root, "Bimwright", "rvt-mcp");
        Directory.CreateDirectory(dir);
        var legacy = Path.Combine(dir, "debug.log");
        var content = string.Concat(Enumerable.Repeat("legacy line\n", 20000));
        File.WriteAllText(legacy, content);

        DiagnosticLog.MaintainLegacyLogs();
        Assert.True(DiagnosticLog.Flush(5000));

        Assert.Equal(content, File.ReadAllText(legacy));
        Assert.Contains("[LogRetention] BIMWRIGHT_KEEP_ALL_LOGS is on", File.ReadAllText(Path.Combine(dir, "rvt-mcp.log")));
    }

    [Fact]
    public void Keep_all_archives_a_pre_v5_call_log_instead_of_deleting_it()
    {
        var dir = Path.Combine(_root, "Bimwright", "rvt-mcp");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "mcp-calls.version"), "3");
        File.WriteAllText(Path.Combine(dir, "mcp-calls.jsonl"), "active v3 record");
        var oldArchive = Path.Combine(dir, "mcp-calls-20260426-010101.jsonl");
        File.WriteAllText(oldArchive, "archived v3 record");

        McpLogger.LocalAppDataOverride = _root;
        McpLogger.Initialize(enabled: true);

        Assert.False(File.Exists(Path.Combine(dir, "mcp-calls.jsonl")));
        Assert.Equal("archived v3 record", File.ReadAllText(oldArchive));
        var archived = Assert.Single(Directory.GetFiles(dir, "mcp-calls-*.jsonl"), f => f != oldArchive);
        Assert.Equal("active v3 record", File.ReadAllText(archived));
        Assert.Equal("5", File.ReadAllText(Path.Combine(dir, "mcp-calls.version")));
    }

    [Fact]
    public void Keep_all_keeps_the_send_code_journal_seven_days_after_it_was_disabled()
    {
        Directory.CreateDirectory(_root);
        SendCodeJournal.LocalAppDataOverride = _root;
        McpLogger.LocalAppDataOverride = _root;
        var active = new RvtMcpConfig
        {
            EnableCallLog = true,
            PersistSendCodeBodies = true,
            PersistSendCodeBodiesUntil = DateTimeOffset.UtcNow.AddDays(1).ToString("o")
        };
        var inactive = new RvtMcpConfig { EnableCallLog = true, PersistSendCodeBodies = false };

        Assert.True(SendCodeJournal.TryAppend(active, "session1", "var x = 1;", true, 50, null, null));
        var now = DateTimeOffset.UtcNow;
        SendCodeJournal.TryAppend(inactive, "session1", "var x = 1;", true, 50, null, null, now);
        SendCodeJournal.TryAppend(inactive, "session1", "var x = 1;", true, 50, null, null, now.AddDays(7.1));
        SendCodeJournal.RunMaintenance(inactive, now.AddDays(30));

        Assert.True(File.Exists(SendCodeJournal.JournalPath));
        Assert.Single(File.ReadAllLines(SendCodeJournal.JournalPath)); // still records nothing while inactive
    }

    [Fact]
    public void Keep_all_report_retention_deletes_nothing()
    {
        Directory.CreateDirectory(_root);
        var now = DateTime.UtcNow;
        foreach (var name in new[] { "old.json", "old.tmp" })
        {
            File.WriteAllText(Path.Combine(_root, name), "synthetic");
            File.SetLastWriteTimeUtc(Path.Combine(_root, name), now.AddDays(-400));
        }

        Assert.Equal(0, ReportRetention.Cleanup(_root, TimeSpan.FromDays(30), now, null));
        Assert.Equal(new[] { "old.json", "old.tmp" }, Directory.GetFiles(_root).Select(Path.GetFileName).OrderBy(x => x).ToArray());
    }
}
