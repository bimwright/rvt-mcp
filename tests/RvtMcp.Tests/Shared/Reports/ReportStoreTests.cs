using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RvtMcp.Plugin;
using RvtMcp.Plugin.Reports.Model;
using RvtMcp.Plugin.Reports.Storage;
using Xunit;

namespace RvtMcp.Tests.Shared.Reports
{
    public class ReportStoreTests : IDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "report-store-tests-" + Guid.NewGuid().ToString("N"));

        private static AgentReport Report(string task, string summary = "synthetic")
        {
            return new AgentReport { TaskId = task, Summary = summary };
        }

        public void Dispose()
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        [Fact]
        public void Saves_new_reports_with_identity_timestamp_filename_and_no_temp_files()
        {
            var store = new ReportStore(dir);
            var first = Report("a");
            var second = Report("b");

            var before = DateTime.UtcNow;
            var saved = store.Save(first);
            store.Save(second);
            var after = DateTime.UtcNow;

            Assert.False(saved.Replaced);
            Assert.Equal(first.ReportId, saved.ReportId);
            Assert.Matches("^[0-9a-f]{32}$", first.ReportId);
            Assert.Equal(first.CreatedUtc, first.UpdatedUtc);
            var created = DateTime.Parse(first.CreatedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind);
            Assert.InRange(created, before, after);
            var files = Directory.GetFiles(dir, "*.json");
            Assert.Equal(2, files.Length);
            Assert.Contains(files, p => Path.GetFileName(p) == created.ToString("yyyyMMdd'T'HHmmssfff'Z'") + "-" + first.ReportId + ".json");
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }

        [Fact]
        public void Resending_replaces_same_file_and_missing_destination_is_recreated()
        {
            var store = new ReportStore(dir);
            var first = Report("a");
            store.Save(first);
            var path = Directory.GetFiles(dir, "*.json").Single();
            System.Threading.Thread.Sleep(20);
            var second = Report("a", "second");

            var result = store.Save(second);

            Assert.True(result.Replaced);
            Assert.Equal(first.ReportId, second.ReportId);
            Assert.Equal(first.CreatedUtc, second.CreatedUtc);
            Assert.NotEqual(first.UpdatedUtc, second.UpdatedUtc);
            Assert.Equal(path, Directory.GetFiles(dir, "*.json").Single());
            Assert.Equal("second", store.LoadNewest().Single().Summary);

            File.Delete(path);
            var third = Report("a");
            Assert.True(store.Save(third).Replaced);
            Assert.True(File.Exists(path));
            Assert.Equal(first.ReportId, third.ReportId);
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }

        [Fact]
        public void New_store_does_not_replace_prior_process_report()
        {
            new ReportStore(dir).Save(Report("a"));
            var other = Report("a");

            Assert.False(new ReportStore(dir).Save(other).Replaced);
            Assert.Equal(2, Directory.GetFiles(dir, "*.json").Length);
        }

        [Fact]
        public void Loads_newest_by_filename_with_limit_skips_bad_and_future_and_logs_once()
        {
            Directory.CreateDirectory(dir);
            var logs = new List<string>();
            for (int i = 0; i < 503; i++)
                File.WriteAllText(Path.Combine(dir, i.ToString("D4") + ".json"), ReportJson.Serialize(Report(i.ToString())));
            File.WriteAllText(Path.Combine(dir, "9999.json"), "bad");
            File.WriteAllText(Path.Combine(dir, "9998.json"), "{\"schema_version\":2}");
            File.WriteAllText(Path.Combine(dir, "9999.tmp"), "ignored");
            var store = new ReportStore(dir, logs.Add);

            var loaded = store.LoadNewest();

            Assert.Equal(498, loaded.Count);
            Assert.Equal("502", loaded[0].TaskId);
            Assert.Equal("5", loaded.Last().TaskId);
            Assert.Equal(498, store.LoadNewest(1000).Count);
            Assert.Equal(2, logs.Count);
            Assert.Equal(new[] { "502", "501", "500" }, store.LoadNewest(5).Select(x => x.TaskId).ToArray());
        }

        [Fact]
        public void Delete_by_id_is_idempotent_even_after_external_delete()
        {
            var store = new ReportStore(dir);
            var report = Report("a");
            store.Save(report);

            store.Delete(report.ReportId);

            Assert.Empty(Directory.GetFiles(dir));
            store.Delete(report.ReportId);
            var next = Report("b");
            store.Save(next);
            File.Delete(Directory.GetFiles(dir).Single());
            store.Delete(next.ReportId);
        }

        [Fact]
        public void Store_preserves_content_without_masking()
        {
            const string input = "Bearer synthetic-token-123";
            Assert.NotEqual(input, SecretMasker.Mask(input));
            var store = new ReportStore(dir);
            var report = Report("a", input);
            report.Body = input + " [label](revit:123)";

            store.Save(report);
            var loaded = Assert.Single(store.LoadNewest());

            Assert.Equal(report.Summary, loaded.Summary);
            Assert.Equal(report.Body, loaded.Body);
        }

        [Fact]
        public async Task Concurrent_saves_and_loads_see_complete_reports()
        {
            var store = new ReportStore(dir);
            store.Save(Report("a"));

            var writer = Task.Run(() =>
            {
                for (int i = 0; i < 50; i++)
                    store.Save(Report("a", i.ToString()));
            });
            var reader = Task.Run(() =>
            {
                for (int i = 0; i < 50; i++)
                    Assert.Single(store.LoadNewest());
            });
            await Task.WhenAll(writer, reader);
        }
    }
}
