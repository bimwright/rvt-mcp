using System;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    // A mutable holder created by the request filter survives awaits and stays isolated per MCP call.
    internal sealed class ChangeCaptureContext : IDisposable
    {
        private static readonly AsyncLocal<ChangeCaptureContext> Slot = new AsyncLocal<ChangeCaptureContext>();
        private readonly ChangeCaptureContext _previous;
        internal JObject Changes { get; private set; }
        internal ChangeCaptureContext() { _previous = Slot.Value; Slot.Value = this; }
        internal static void Record(JObject changes) { if (Slot.Value != null) Slot.Value.Changes = changes; }
        public void Dispose() { Slot.Value = _previous; }
    }
}
