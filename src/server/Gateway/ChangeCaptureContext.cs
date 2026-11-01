using System;
using System.Threading;
using Newtonsoft.Json.Linq;
using System.Linq;

namespace RvtMcp.Server
{
    // A mutable holder created by the request filter survives awaits and stays isolated per MCP call.
    internal sealed class ChangeCaptureContext : IDisposable
    {
        private static readonly AsyncLocal<ChangeCaptureContext> Slot = new AsyncLocal<ChangeCaptureContext>();
        private readonly ChangeCaptureContext _previous;
        internal JObject Changes { get; private set; }
        internal JObject History { get; private set; }
        private readonly object _gate = new object();
        private readonly JArray _reportCaptures = new JArray();
        internal JArray ReportCaptures { get { lock (_gate) return (JArray)_reportCaptures.DeepClone(); } }
        internal ChangeCaptureContext() { _previous = Slot.Value; Slot.Value = this; }
        internal static void Record(JObject changes) { if (Slot.Value != null) Slot.Value.Changes = changes; }
        internal static void RecordHistory(JObject receipt) { if (Slot.Value != null) Slot.Value.History = receipt; }
        internal static void RecordReport(JObject receipt)
        {
            var context = Slot.Value;
            if (context == null || receipt == null) return;
            lock (context._gate)
                if (!context._reportCaptures.Any(r => JToken.DeepEquals(r["call_id"], receipt["call_id"])
                    && JToken.DeepEquals(r["client_session"], receipt["client_session"])))
                    context._reportCaptures.Add(receipt.DeepClone());
        }
        public void Dispose() { Slot.Value = _previous; }
    }
}
