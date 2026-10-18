using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using RvtMcp.Plugin;

namespace RvtMcp.Server
{
    internal sealed class SpillCleanupService : BackgroundService
    {
        internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
        private readonly Func<SpillCleanupResult> _cleanup;
        private readonly TimeSpan _interval;
        private readonly Action<string> _report;

        internal SpillCleanupService(Func<SpillCleanupResult> cleanup, TimeSpan? interval = null,
            Action<string> report = null)
        {
            _cleanup = cleanup;
            _interval = interval ?? Interval;
            _report = report ?? (message => DiagnosticLog.Write("spill_cleanup", message));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // On .NET 8 StartAsync executes until the first await: never put file I/O on startup.
            await Task.Yield();
            using var timer = new PeriodicTimer(_interval);
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        var result = _cleanup();
                        if (result.DeletedCount != 0 || result.FailedCount != 0)
                            _report($"deleted={result.DeletedCount}; remaining={result.RemainingCount}; failed={result.FailedCount}");
                    }
                    catch (Exception ex)
                    {
                        // Maintenance must neither stop the gateway nor expose paths or output contents.
                        _report("failed=1; reason=" + ex.GetType().Name);
                    }
                    if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }
}
