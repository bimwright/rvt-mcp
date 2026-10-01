using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using RvtMcp.Plugin;

namespace RvtMcp.Server.Memory
{
    // Named kernel handle lifetime follows one gateway call, with no profile files
    // and no thread-affine mutex held across awaits. Only Windows hosts are supported.
    internal sealed class HistoryCallLease : IDisposable
    {
        private readonly EventWaitHandle _handle;
        public HistoryCallLease(string callId)
        {
            if (!ChangeHistoryTransfer.IsId(callId)) throw new ArgumentException("Invalid history call identifier.");
            _handle = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\RvtMcp.History." + callId);
        }
        public static bool IsActive(string callId)
        {
            if (!ChangeHistoryTransfer.IsId(callId)) return false;
            if (!OperatingSystem.IsWindows()) return false;
            if (!EventWaitHandle.TryOpenExisting(@"Local\RvtMcp.History." + callId, out var handle)) return false;
            handle.Dispose(); return true;
        }
        public void Dispose() => _handle.Dispose();
    }

    internal sealed class HistoryRecoveryService : BackgroundService
    {
        private readonly Action<CancellationToken> _recover;
        public HistoryRecoveryService(ChangeHistoryStore store)
            : this(token => store.RecoverPending(token, minimumAge: TimeSpan.FromSeconds(2))) { }
        internal HistoryRecoveryService(Action<CancellationToken> recover) { _recover = recover; }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    // StartAsync must never wait for file reads, parsing or SQLite work.
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    await Task.Run(() => _recover(stoppingToken), stoppingToken);
                    await Task.Delay(TimeSpan.FromSeconds(58), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { HistoryDiagnostics.Report("history_recovery_worker", ex); }
        }
    }
}
