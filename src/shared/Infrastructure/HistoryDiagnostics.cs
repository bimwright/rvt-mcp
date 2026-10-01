using System;
using System.Collections.Generic;

namespace RvtMcp.Plugin
{
    /// <summary>Operational diagnostics without exception messages, paths, code or model data.</summary>
    public static class HistoryDiagnostics
    {
        private static readonly Dictionary<string, DateTime> Last = new Dictionary<string, DateTime>();
        public static void Report(string operation, Exception error, string callId = null, Action<string> log = null)
        {
            lock (Last)
            {
                var now = DateTime.UtcNow;
                if (Last.TryGetValue(operation, out var last) && now - last < TimeSpan.FromSeconds(30)) return;
                Last[operation] = now;
            }
            var message = "[RvtMcp] " + operation + ": " + error.GetType().Name
                + (ChangeHistoryTransfer.IsId(callId) ? "; callId=" + callId : "") + ". See local history/storage status.";
            try { (log ?? Console.Error.WriteLine)(message); }
            catch { System.Diagnostics.Trace.TraceWarning("RvtMcp diagnostic sink is unavailable."); }
        }
    }
}
