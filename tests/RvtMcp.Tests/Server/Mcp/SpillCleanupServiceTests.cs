using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RvtMcp.Plugin;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class SpillCleanupServiceTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rvt-spill-service-" + Guid.NewGuid().ToString("N"));
        private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        [Fact]
        public async Task Startup_and_later_ticks_clean_expired_files_without_new_spill_writes()
        {
            Directory.CreateDirectory(_root);
            var expired = Path.Combine(_root, "expired.txt");
            var fresh = Path.Combine(_root, "fresh.txt");
            var later = Path.Combine(_root, "later.txt");
            File.WriteAllText(expired, "synthetic old output");
            File.WriteAllText(fresh, "synthetic recent output");
            File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddHours(-2));
            var writer = new ResponseSpillWriter(_root, TimeSpan.FromHours(1));
            var startup = Signal(); var subsequent = Signal();
            var laterCreated = 0;
            using var service = new SpillCleanupService(() =>
            {
                var result = writer.Cleanup(DateTime.UtcNow);
                startup.TrySetResult(true);
                if (Volatile.Read(ref laterCreated) != 0 && !File.Exists(later)) subsequent.TrySetResult(true);
                return result;
            }, TimeSpan.FromMilliseconds(25), _ => { });
            try
            {
                await service.StartAsync(CancellationToken.None);
                await startup.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(File.Exists(expired));
                Assert.True(File.Exists(fresh));
                File.WriteAllText(later, "synthetic later expired output");
                File.SetLastWriteTimeUtc(later, DateTime.UtcNow.AddHours(-2));
                Volatile.Write(ref laterCreated, 1);
                await subsequent.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(File.Exists(fresh));
                Assert.False(File.Exists(later));
            }
            finally { await service.StopAsync(CancellationToken.None); }
        }

        [Fact]
        public async Task Storage_work_does_not_block_host_startup()
        {
            var entered = Signal();
            using var release = new ManualResetEventSlim();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var service = new SpillCleanupService(() =>
            {
                entered.TrySetResult(true);
                release.Wait(deadline.Token);
                return new SpillCleanupResult();
            }, report: _ => { });
            try
            {
                await service.StartAsync(deadline.Token).WaitAsync(deadline.Token);
                await entered.Task.WaitAsync(deadline.Token);
                Assert.False(release.IsSet);
            }
            finally
            {
                release.Set();
                await service.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task Transient_errors_are_reported_safely_and_later_ticks_continue_until_stopped()
        {
            var recovered = Signal(); var calls = 0;
            var reports = new List<string>();
            using var service = new SpillCleanupService(() =>
            {
                if (Interlocked.Increment(ref calls) == 1) throw new IOException("private synthetic path");
                recovered.TrySetResult(true);
                return new SpillCleanupResult { FailedCount = 1, RemainingCount = 2 };
            }, TimeSpan.FromMilliseconds(25), message => { lock (reports) reports.Add(message); });
            await service.StartAsync(CancellationToken.None);
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.StopAsync(CancellationToken.None);
            var stoppedCount = Volatile.Read(ref calls);
            await Task.Delay(100);
            Assert.Equal(stoppedCount, Volatile.Read(ref calls));
            Assert.Contains(reports, message => message.Contains("reason=IOException"));
            Assert.Contains(reports, message => message.Contains("remaining=2; failed=1"));
            Assert.All(reports, message => Assert.DoesNotContain("private synthetic path", message));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Maintenance_registration_is_independent_of_read_only_and_history(bool readOnly)
        {
            var services = new ServiceCollection();
            Program.RegisterRuntimeMaintenance(services, new RvtMcpConfig { ReadOnly = readOnly, EnableChangeHistory = false });
            using var provider = services.BuildServiceProvider();
            Assert.Single(provider.GetServices<IHostedService>().OfType<SpillCleanupService>());
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
}
