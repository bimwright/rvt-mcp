using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests.Helpers
{
    /// <summary>Captures the wire call a tool shell sends through ToolGateway.SendOverride.
    /// SendOverride is a process-wide static and xUnit runs test classes in parallel,
    /// so every capture holds a shared gate for the duration of the call.</summary>
    internal sealed class Capture
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        public string Command { get; private set; }
        public object Parameters { get; private set; }
        public int? TimeoutSeconds { get; private set; }

        public static async Task<Capture> Send(Func<Task<string>> call)
        {
            var captured = new Capture();
            await Gate.WaitAsync();
            try
            {
                ToolGateway.SendOverride = (command, parameters, timeout) =>
                {
                    captured.Command = command;
                    captured.Parameters = parameters;
                    captured.TimeoutSeconds = timeout;
                    return Task.FromResult(new JObject());
                };

                var text = await call();
                Assert.False(text.StartsWith("Error:", StringComparison.Ordinal), text);
                return captured;
            }
            finally
            {
                ToolGateway.SendOverride = null;
                Gate.Release();
            }
        }

        /// <summary>Run a body with a custom SendOverride under the same gate.</summary>
        public static async Task WithSendOverride(Func<string, object, int?, Task<JObject>> sendOverride, Func<Task> body)
        {
            await Gate.WaitAsync();
            try
            {
                ToolGateway.SendOverride = sendOverride;
                await body();
            }
            finally
            {
                ToolGateway.SendOverride = null;
                Gate.Release();
            }
        }

        public JObject Json()
        {
            if (Parameters == null)
                return new JObject();
            return JObject.FromObject(Parameters, JsonSerializer.Create(new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            }));
        }
    }
}
