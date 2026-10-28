using System;
using System.Collections.Generic;
using System.IO;
using RvtMcp.Plugin.Reports.Storage;
using Xunit;

namespace RvtMcp.Tests.Shared.Reports
{
    public class ReportFolderWatcherTests : IDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "report-watcher-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        [Fact]
        public void Creates_missing_directory_and_can_dispose()
        {
            using (var watcher = new ReportFolderWatcher(dir))
            {
                Assert.True(Directory.Exists(dir));
            }
        }

        [Fact]
        public void Existing_directory_can_be_watched_and_disposed_twice()
        {
            Directory.CreateDirectory(dir);

            var watcher = new ReportFolderWatcher(dir);
            watcher.Dispose();
            watcher.Dispose();
        }

        [Fact]
        public void Startup_failure_is_logged_without_throwing()
        {
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "file");
            File.WriteAllText(file, "x");
            var logs = new List<string>();

            using (new ReportFolderWatcher(file, logs.Add)) { }

            Assert.NotEmpty(logs);
        }
    }
}
