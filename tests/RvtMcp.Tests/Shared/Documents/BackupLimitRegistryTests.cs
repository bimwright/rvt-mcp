using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Documents;
using Xunit;

namespace RvtMcp.Tests.Shared.Documents
{
    public class BackupLimitRegistryTests : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "rvt-backup-registry-" + Guid.NewGuid().ToString("N"));
        private string RegistryPath => Path.Combine(directory, "backup-limits.json");
        private string Model => Path.Combine(directory, "Tower.rvt");
        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 3, 8, 30, 12, TimeSpan.Zero);
        private static Task StartWorker(Action action) => Task.Factory.StartNew(action,
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        private static Task<T> StartWorker<T>(Func<T> action) => Task.Factory.StartNew(action,
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

        [Theory]
        [InlineData(0, true, "raised")]
        [InlineData(19, true, "raised")]
        [InlineData(20, false, "observed")]
        [InlineData(50, false, "observed")]
        public void Unregistered_model_selects_save_method_and_records_only_after_success(int count, bool saveAs, string ensuredBy)
        {
            var registry = new BackupLimitRegistry(RegistryPath);
            var decision = registry.Decide(Model, count);
            Assert.Equal(saveAs, decision.UseSaveAs);
            Assert.Equal(ensuredBy, decision.EnsuredBy);
            Assert.True(decision.MustRecord);
            Assert.Null(registry.Get(Model));

            registry.Record(Model, decision.EnsuredBy, Now, 2026);
            var record = new BackupLimitRegistry(RegistryPath).Get(Model.ToUpperInvariant());
            Assert.Equal(20, record.MinBackups);
            Assert.Equal(ensuredBy, record.EnsuredBy);
            var json = JObject.Parse(File.ReadAllText(RegistryPath));
            var stored = Assert.Single(json.Properties()).Value;
            Assert.Equal(ensuredBy, (string)stored["ensured_by"]);
            Assert.Null(stored["limit_applied"]);
        }

        [Fact]
        public void Registered_model_uses_normal_save_even_when_count_is_below_twenty()
        {
            var registry = new BackupLimitRegistry(RegistryPath);
            registry.Record(Model, "observed", Now, 2026);
            var decision = registry.Decide(Model, 0);
            Assert.False(decision.UseSaveAs);
            Assert.False(decision.MustRecord);
            Assert.Equal("observed", decision.EnsuredBy);
        }

        [Fact]
        public void Registered_model_does_not_need_to_enumerate_backups_before_saving()
        {
            var registry = new BackupLimitRegistry(RegistryPath);
            registry.Record(Model, "observed", Now, 2026);
            var decision = registry.Decide(Model, () => throw new IOException("enumeration should not run"));
            Assert.False(decision.UseSaveAs);
            Assert.False(decision.MustRecord);
        }

        [Fact]
        public void Unregistered_model_uses_observed_backup_count()
        {
            var called = false;
            var decision = new BackupLimitRegistry(RegistryPath).Decide(Model, () => { called = true; return 20; });
            Assert.True(called);
            Assert.False(decision.UseSaveAs);
            Assert.True(decision.MustRecord);
            Assert.Equal("observed", decision.EnsuredBy);
        }

        [Theory]
        [InlineData("{broken")]
        [InlineData("{\"C:\\\\M.RVT\":{\"min_backups\":20,\"ensured_by\":\"invalid\"}}")]
        public void Unreadable_registry_is_treated_as_empty_and_can_be_rewritten(string corrupt)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(RegistryPath, corrupt);
            var registry = new BackupLimitRegistry(RegistryPath);
            Assert.Null(registry.Get(Model));
            registry.Record(Model, "raised", Now, 2026);
            Assert.Equal("raised", registry.Get(Model).EnsuredBy);
            Assert.Single(Directory.GetFiles(directory));
        }

        public static IEnumerable<object[]> InvalidRegistryContent()
        {
            JObject Record(int minimum = 20, string evidence = "raised", int year = 2026) => new JObject
            {
                ["min_backups"] = minimum, ["ensured_by"] = evidence,
                ["recorded_utc"] = "2026-11-03T08:30:12Z", ["revit_year"] = year
            };
            string Registry(JToken entry) => new JObject(new JProperty(@"C:\Model.rvt", entry)).ToString();

            yield return new object[] { "null root", "null" };
            yield return new object[] { "null entry", Registry(JValue.CreateNull()) };
            yield return new object[] { "wrong minimum", Registry(Record(minimum: 19)) };
            yield return new object[] { "unknown evidence", Registry(Record(evidence: "other")) };
            yield return new object[] { "zero year", Registry(Record(year: 0)) };
            yield return new object[] { "negative year", Registry(Record(year: -1)) };
            yield return new object[] { "case-insensitive duplicate keys", new JObject(
                new JProperty(@"C:\Model.rvt", Record()),
                new JProperty(@"c:\MODEL.RVT", Record())).ToString() };
        }

        [Theory]
        [MemberData(nameof(InvalidRegistryContent))]
        public void Valid_json_with_invalid_content_is_empty_for_decide_and_record(string reason, string corrupt)
        {
            Directory.CreateDirectory(directory);
            var registry = new BackupLimitRegistry(RegistryPath);
            File.WriteAllText(RegistryPath, corrupt);
            var decision = registry.Decide(Model, 0);
            Assert.True(decision.UseSaveAs, reason);
            Assert.True(decision.MustRecord, reason);
            Assert.Equal("raised", decision.EnsuredBy);

            // Exercise Record against the corrupt file independently of Decide.
            File.WriteAllText(RegistryPath, corrupt);
            registry.Record(Model, "observed", Now, 2026);
            Assert.Equal("observed", new BackupLimitRegistry(RegistryPath).Get(Model).EnsuredBy);
            Assert.Single(Directory.GetFiles(directory));
        }

        [Fact]
        public async Task Concurrent_writers_complete_and_leave_a_readable_registry()
        {
            var tasks = Enumerable.Range(0, 8).Select(i => StartWorker<bool>(() =>
                new BackupLimitRegistry(RegistryPath).Record(Model, i % 2 == 0 ? "raised" : "observed", Now, 2026)));
            Assert.All(await Task.WhenAll(tasks), recorded => Assert.True(recorded));
            var record = new BackupLimitRegistry(RegistryPath).Get(Model);
            Assert.Contains(record.EnsuredBy, new[] { "raised", "observed" });
            Assert.Single(Directory.GetFiles(directory));
        }

        [Fact]
        public async Task Contended_registry_write_times_out_without_writing_and_can_be_retried()
        {
            var registry = new BackupLimitRegistry(RegistryPath);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var holder = StartWorker(() =>
            {
                using (var mutex = new Mutex(false, registry.MutexName))
                {
                    mutex.WaitOne();
                    try { entered.SetResult(true); release.Task.GetAwaiter().GetResult(); }
                    finally { mutex.ReleaseMutex(); }
                }
            });
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var watch = Stopwatch.StartNew();
                var write = StartWorker(() => registry.Record(Model, "observed", Now, 2026));
                Assert.False(await write.WaitAsync(TimeSpan.FromSeconds(25)));
                Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(24));
                Assert.False(File.Exists(RegistryPath));
            }
            finally
            {
                release.TrySetResult(true);
                await holder.WaitAsync(TimeSpan.FromSeconds(10));
            }
            Assert.True(registry.Record(Model, "observed", Now, 2026));
            Assert.Equal("observed", registry.Get(Model).EnsuredBy);
        }
    }
}
