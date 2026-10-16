using System;
using RvtMcp.Plugin;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    internal static class GatewayRuntime
    {
        private static readonly Lazy<JObject> Captured = new Lazy<JObject>(() =>
            RuntimeIdentity.Capture(typeof(Program).Assembly, "gateway"));

        // One immutable capture per process; callers cannot mutate later replies.
        internal static JObject Identity => (JObject)Captured.Value.DeepClone();
    }
}
