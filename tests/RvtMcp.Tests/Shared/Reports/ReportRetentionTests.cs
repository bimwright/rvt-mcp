using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RvtMcp.Plugin.Reports.Storage;
using Xunit;

namespace RvtMcp.Tests.Shared.Reports
{
    public class ReportRetentionTests : IDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "report-retention-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        [Fact]
        public void Deletes_only_old_json_and_tmp_by_last_write_time()
        {
            Directory.CreateDirectory(dir);
            var now = DateTime.UtcNow;
            var cutoff = now.AddDays(-30);
            foreach (var name in new[] { "old.json", "old.tmp", "equal.json", "new.json", "old.txt" })
                File.WriteAllText(Path.Combine(dir, name), "synthetic");
            foreach (var name in new[] { "old.json", "old.tmp", "old.txt" })
                File.SetLastWriteTimeUtc(Path.Combine(dir, name), cutoff.AddSeconds(-1));
            File.SetLastWriteTimeUtc(Path.Combine(dir, "equal.json"), cutoff);
            File.SetLastWriteTimeUtc(Path.Combine(dir, "new.json"), now);

            var deleted = ReportRetention.Cleanup(dir, TimeSpan.FromDays(30), now, null);

            Assert.Equal(2, deleted);
            Assert.Equal(new[] { "equal.json", "new.json", "old.txt" }, Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(x => x).ToArray());
        }

        [Fact]
        public void Missing_directory_and_locked_file_do_not_abort_cleanup()
        {
            var logs = new List<string>();
            var now = DateTime.UtcNow;

            Assert.Equal(0, ReportRetention.Cleanup(dir, TimeSpan.FromDays(1), now, logs.Add));

            Directory.CreateDirectory(dir);
            var locked = Path.Combine(dir, "locked.json");
            var other = Path.Combine(dir, "other.json");
            File.WriteAllText(locked, "x");
            File.WriteAllText(other, "x");
            File.SetLastWriteTimeUtc(locked, now.AddDays(-2));
            File.SetLastWriteTimeUtc(other, now.AddDays(-2));

            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Equal(1, ReportRetention.Cleanup(dir, TimeSpan.FromDays(1), now, logs.Add));
                Assert.True(File.Exists(locked));
                Assert.False(File.Exists(other));
                Assert.NotEmpty(logs);
            }
        }
    }
}
