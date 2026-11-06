using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RvtMcp.Plugin.Documents;
using Xunit;

namespace RvtMcp.Tests.Shared.Documents
{
    public class CheckpointIndexTests : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "rvt-checkpoint-index-" + Guid.NewGuid().ToString("N"));
        private string IndexPath => Path.Combine(directory, "index.json");

        private static Task StartWorker(Action action) => Task.Factory.StartNew(action,
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        private static Task<T> StartWorker<T>(Func<T> action) => Task.Factory.StartNew(action,
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        private static CheckpointIndexData Data(string label) => new CheckpointIndexData
        {
            ModelFile = "Tower.rvt",
            Checkpoints = new List<CheckpointEntry>
            {
                new CheckpointEntry
                {
                    Id = "20261103-153012", File = "20261103-153012.rvt", Label = label,
                    CreatedUtc = "2026-11-03T08:30:12Z", Bytes = 42,
                    Source = new CheckpointSource { NumberOfSaves = 35, VersionGuid = "11111111-1111-1111-1111-111111111111" },
                    RevitYear = 2026, User = "test"
                }
            }
        };

        private void ReplaceWith(string label) => CheckpointIndex.Update(IndexPath, "Tower.rvt",
            index => index.Checkpoints = Data(label).Checkpoints);

        public void Dispose()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        [Fact]
        public void Reading_missing_index_does_not_create_directory()
        {
            var data = CheckpointIndex.Load(IndexPath, "Tower.rvt");
            Assert.Empty(data.Checkpoints);
            Assert.False(data.Exists);
            Assert.False(Directory.Exists(directory));
        }

        [Fact]
        public void Update_creates_and_replaces_complete_index()
        {
            ReplaceWith("first");
            ReplaceWith("second");

            var loaded = CheckpointIndex.Load(IndexPath, "Tower.rvt");
            Assert.True(loaded.Exists);
            Assert.Equal("second", Assert.Single(loaded.Checkpoints).Label);
            Assert.Equal(35, loaded.Checkpoints[0].Source.NumberOfSaves);
            Assert.Single(Directory.GetFiles(directory));
        }

        [Theory]
        [InlineData("{broken")]
        [InlineData("{}")]
        [InlineData("{\"schema\":1,\"model_file\":\"Tower.rvt\",\"checkpoints\":null}")]
        public void Unreadable_index_is_not_overwritten(string corrupt)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(IndexPath, corrupt);
            Assert.Throws<InvalidDataException>(() => ReplaceWith("replacement"));
            Assert.Equal(corrupt, File.ReadAllText(IndexPath));
            Assert.Single(Directory.GetFiles(directory));
        }

        [Fact]
        public async Task Concurrent_reader_only_sees_complete_old_or_new_index()
        {
            ReplaceWith("first");
            var writer = StartWorker(() =>
            {
                for (var i = 0; i < 30; i++) ReplaceWith(i % 2 == 0 ? "first" : "second");
            });
            var reader = StartWorker(() =>
            {
                for (var i = 0; i < 60; i++)
                    Assert.Contains(Assert.Single(CheckpointIndex.Load(IndexPath, "Tower.rvt").Checkpoints).Label,
                        new[] { "first", "second" });
            });
            await Task.WhenAll(writer, reader);
        }

        [Fact]
        public async Task Concurrent_updates_and_reads_preserve_every_entry()
        {
            ReplaceWith("seed");
            using (var start = new ManualResetEventSlim())
            {
                var writers = Enumerable.Range(0, 4).Select(worker => StartWorker(() =>
                {
                    start.Wait();
                    for (var i = 0; i < 12; i++)
                    {
                        var id = new DateTime(2026, 11, 3, 15, 31, 0).AddSeconds(worker * 12 + i)
                            .ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                        var entry = Data(id).Checkpoints[0];
                        entry.Id = id;
                        entry.File = id + ".rvt";
                        CheckpointIndex.Update(IndexPath, "Tower.rvt", index => index.Checkpoints.Add(entry));
                    }
                })).ToArray();
                var readers = Enumerable.Range(0, 4).Select(_ => StartWorker(() =>
                {
                    start.Wait();
                    var seen = new HashSet<string>();
                    for (var i = 0; i < 40; i++)
                    {
                        var snapshot = CheckpointIndex.Load(IndexPath, "Tower.rvt");
                        Assert.Contains(snapshot.Checkpoints, c => c.Label == "seed");
                        var ids = new HashSet<string>(snapshot.Checkpoints.Select(c => c.Id));
                        Assert.Equal(snapshot.Checkpoints.Count, ids.Count);
                        Assert.True(seen.IsSubsetOf(ids), "A previously observed checkpoint disappeared.");
                        seen.UnionWith(ids);
                    }
                })).ToArray();
                start.Set();
                await Task.WhenAll(writers.Concat(readers));
            }
            var entries = CheckpointIndex.Load(IndexPath, "Tower.rvt").Checkpoints;
            Assert.Equal(49, entries.Count);
            Assert.Equal(49, entries.Select(c => c.Id).Distinct().Count());
            Assert.Single(Directory.GetFiles(directory));
        }

        [Fact]
        public async Task Reader_waits_for_whole_read_modify_write_operation()
        {
            ReplaceWith("first");
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var readerStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var writer = StartWorker(() => CheckpointIndex.Update(IndexPath, "Tower.rvt", index =>
            {
                entered.SetResult(true);
                release.Task.GetAwaiter().GetResult();
                index.Checkpoints[0].Label = "second";
            }));
            Task<CheckpointIndexData> reader = null;
            Task completedFirst;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                reader = StartWorker(() =>
                {
                    readerStarted.SetResult(true);
                    return CheckpointIndex.Load(IndexPath, "Tower.rvt");
                });
                await readerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                completedFirst = await Task.WhenAny(reader, Task.Delay(150));
            }
            finally
            {
                release.TrySetResult(true);
            }
            await writer;
            Assert.NotSame(reader, completedFirst);
            Assert.Equal("second", Assert.Single((await reader).Checkpoints).Label);
        }

        [Fact]
        public void Failed_mutation_does_not_replace_existing_index()
        {
            ReplaceWith("first");
            Assert.Throws<InvalidOperationException>(() => CheckpointIndex.Update(IndexPath, "Tower.rvt", index =>
            {
                index.Checkpoints[0].Label = "unfinished";
                throw new InvalidOperationException("abort");
            }));
            Assert.Equal("first", Assert.Single(CheckpointIndex.Load(IndexPath, "Tower.rvt").Checkpoints).Label);
            Assert.Single(Directory.GetFiles(directory));
        }

        [Fact]
        public async Task Contended_index_read_and_update_time_out_without_changing_index()
        {
            ReplaceWith("first");
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var holder = StartWorker(() => CheckpointIndex.Update(IndexPath, "Tower.rvt", _ =>
            {
                entered.SetResult(true);
                release.Task.GetAwaiter().GetResult();
            }));
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var watch = Stopwatch.StartNew();
                var read = StartWorker(() => Assert.Throws<TimeoutException>(() => CheckpointIndex.Load(IndexPath, "Tower.rvt")));
                var update = StartWorker(() => Assert.Throws<TimeoutException>(() =>
                    CheckpointIndex.Update(IndexPath, "Tower.rvt", index => index.Checkpoints.Clear())));
                await Task.WhenAll(read, update).WaitAsync(TimeSpan.FromSeconds(25));
                Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(24));
                Assert.Equal("CHECKPOINT_INDEX_BUSY", (await read).Message);
                Assert.Equal("CHECKPOINT_INDEX_BUSY", (await update).Message);
            }
            finally
            {
                release.TrySetResult(true);
                await holder.WaitAsync(TimeSpan.FromSeconds(10));
            }
            Assert.Equal("first", Assert.Single(CheckpointIndex.Load(IndexPath, "Tower.rvt").Checkpoints).Label);
        }
    }
}
