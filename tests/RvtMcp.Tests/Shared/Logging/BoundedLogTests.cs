using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using RvtMcp.Plugin;
using RvtMcp.Server.Memory;
using Xunit;

namespace RvtMcp.Tests;

[Collection("ServerStateConfig")]
public sealed class BoundedLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rvtmcp-bounded-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Utf8_append_enforces_both_file_and_group_budgets_and_keeps_unknown_files()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "usage.jsonl");
        var unknown = Path.Combine(_root, "usage-owner-notes.jsonl");
        File.WriteAllText(unknown, "owner data");
        for (int i = 0; i < 25; i++) BoundedLogFile.Append(path, new string('界', 50), fileBytes: 256, groupBytes: 1024);
        var generated = BoundedLogFile.Files(_root, "usage");
        Assert.All(generated, file => Assert.InRange(new FileInfo(file).Length, 1, 256));
        Assert.True(generated.Sum(file => new FileInfo(file).Length) <= 1024);
        Assert.Equal("owner data", File.ReadAllText(unknown));
    }

    [Fact]
    public void Retention_prunes_old_generated_archives()
    {
        Directory.CreateDirectory(_root);
        var old = Path.Combine(_root, "usage-20200101-000000.jsonl");
        File.WriteAllText(old, "old");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-31));
        BoundedLogFile.Append(Path.Combine(_root, "usage.jsonl"), "new");
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void A_locked_active_file_never_falls_back_to_a_second_or_unbounded_file()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "usage.jsonl");
        File.WriteAllText(path, "old\n");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => BoundedLogFile.Append(path, "new"));
        Assert.Equal("old\n", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void Diagnostics_collapse_interleaved_repeats_and_include_date_pid_and_year()
    {
        var previous = DiagnosticLog.LocalAppDataOverride;
        try
        {
            DiagnosticLog.LocalAppDataOverride = _root;
            for (var i = 0; i < 40; i++)
            {
                DiagnosticLog.Write("Pipe", "busy", "2027");
                DiagnosticLog.Write("Pipe", "disconnected", "2027");
            }
            Assert.True(DiagnosticLog.Flush(5000));
            var path = Path.Combine(_root, "Bimwright", "rvt-mcp", "rvt-mcp.log");
            var lines = File.ReadAllLines(path);
            Assert.Equal(4, lines.Length);
            Assert.All(lines, line => { Assert.Contains("pid=", line); Assert.Contains("revit=2027", line); Assert.Contains(DateTime.UtcNow.ToString("yyyy-MM-dd"), line); });
            Assert.Equal(2, lines.Count(line => line.Contains("repeated=39")));
        }
        finally { DiagnosticLog.LocalAppDataOverride = previous; }
    }

    [Fact]
    public void Diagnostics_file_lock_does_not_block_the_caller_or_grow_a_fallback_log()
    {
        var previous = DiagnosticLog.LocalAppDataOverride;
        try
        {
            DiagnosticLog.LocalAppDataOverride = _root;
            var dir = Path.Combine(_root, "Bimwright", "rvt-mcp");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "rvt-mcp.log");
            File.WriteAllText(path, "held");
            var before = DiagnosticLog.Dropped;
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var sw = Stopwatch.StartNew();
                DiagnosticLog.Write("Pipe", "locked", "2022");
                Assert.True(sw.ElapsedMilliseconds < 100);
                Assert.True(DiagnosticLog.Flush(5000));
            }
            Assert.True(DiagnosticLog.Dropped > before);
            Assert.Equal("held", File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(dir));
        }
        finally { DiagnosticLog.LocalAppDataOverride = previous; }
    }

    [Fact]
    public void Legacy_cleanup_retains_a_bounded_tail_and_preserves_configuration()
    {
        var previous = DiagnosticLog.LocalAppDataOverride;
        try
        {
            DiagnosticLog.LocalAppDataOverride = _root;
            var legacy = Path.Combine(_root, "RvtMcp");
            Directory.CreateDirectory(legacy);
            var old = Path.Combine(legacy, "revit-mcp.log");
            File.WriteAllText(old, string.Concat(Enumerable.Repeat("old noise\n", 20000)) + "last useful message\n");
            var config = Path.Combine(legacy, "rvtmcp.config.json");
            File.WriteAllText(config, "{\"owner\":true}");
            DiagnosticLog.MaintainLegacyLogs();
            Assert.True(DiagnosticLog.Flush(5000));
            Assert.True(new FileInfo(old).Length <= 64 * 1024);
            Assert.Contains("last useful message", File.ReadAllText(old));
            Assert.Equal("{\"owner\":true}", File.ReadAllText(config));
        }
        finally { DiagnosticLog.LocalAppDataOverride = previous; }
    }

    [Fact]
    public void A_daily_journal_reader_includes_rotated_segments_without_allocating_all_lines()
    {
        Directory.CreateDirectory(_root);
        var day = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var path = Path.Combine(_root, day + ".jsonl");
        BoundedLogFile.Append(path, "{\"Tool\":\"first\"}", "journal", fileBytes: 40);
        BoundedLogFile.Append(path, "{\"Tool\":\"second\"}", "journal", fileBytes: 40);
        var logger = new JournalLogger(true, _root);
        Assert.Equal(new[] { "first", "second" }, logger.ReadDayEntries(day).Select(entry => entry.Tool));
        Assert.Equal(new[] { day }, logger.ListDates());
    }

    [Fact]
    public void Startup_retention_also_cleans_an_inactive_log_group()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "usage.jsonl");
        File.WriteAllText(path, "old\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-31));
        BoundedLogFile.MaintainGroup(_root, "usage");
        Assert.Empty(BoundedLogFile.Files(_root, "usage"));
    }

    [Fact]
    public void Maintenance_refuses_a_linked_directory_and_preserves_its_target()
    {
        Directory.CreateDirectory(_root);
        var target = Path.Combine(_root, "owner");
        Directory.CreateDirectory(target);
        var path = Path.Combine(target, "usage.jsonl");
        File.WriteAllText(path, "owner data");
        var link = Path.Combine(_root, "linked");
        Directory.CreateSymbolicLink(link, target);
        try
        {
            Assert.Throws<IOException>(() => BoundedLogFile.MaintainGroup(link, "usage"));
            Assert.Throws<IOException>(() => BoundedLogFile.Append(Path.Combine(link, "usage.jsonl"), "new"));
            Assert.Equal("owner data", File.ReadAllText(path));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void Diagnostics_compact_oversized_existing_files_and_keep_only_one_backup()
    {
        var previous = DiagnosticLog.LocalAppDataOverride;
        try
        {
            DiagnosticLog.LocalAppDataOverride = _root;
            var dir = Path.Combine(_root, "Bimwright", "rvt-mcp");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "rvt-mcp.log");
            var old = string.Concat(Enumerable.Repeat(new string('x', 1023) + "\n", 11000));
            File.WriteAllText(path, old);
            File.WriteAllText(path + ".1", old);
            DiagnosticLog.Write("Pipe", "rotation check", "2024");
            Assert.True(DiagnosticLog.Flush(5000));
            Assert.All(Directory.GetFiles(dir), file => Assert.InRange(new FileInfo(file).Length, 1, DiagnosticLog.FileBytes));
            Assert.Equal(2, Directory.GetFiles(dir).Length);
            Assert.Contains("rotation check", File.ReadAllText(path));
        }
        finally { DiagnosticLog.LocalAppDataOverride = previous; }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
