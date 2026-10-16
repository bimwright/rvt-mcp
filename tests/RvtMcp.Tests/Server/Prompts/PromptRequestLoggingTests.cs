using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server;
using RvtMcp.Server.Prompts;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("ServerStateConfig")]
    public class PromptRequestLoggingTests
    {
        [Fact]
        public void Prompt_log_contains_only_name_time_and_session()
        {
            Assert.Equal("prompts/get name=revit_change utc=2026-10-17T00:00:00.0000000+00:00 session=session-1",
                PromptRequestLogging.Format("revit_change", "session-1",
                    new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero)));
        }

        [Theory]
        [InlineData("revit_change", false)]
        [InlineData("missing_prompt", true)]
        public async Task Prompt_filter_logs_once_without_argument_text(string prompt, bool missing)
        {
            var root = Path.Combine(Path.GetTempPath(), "rvt-prompt-log-" + Guid.NewGuid().ToString("N"));
            var previousRoot = DiagnosticLog.LocalAppDataOverride;
            var previousConfig = ServerState.Config;
            var pipe = "rvt-prompt-log-" + Guid.NewGuid().ToString("N");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var input = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var output = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.WhenAll(input.WaitForConnectionAsync(timeout.Token), output.ConnectAsync(timeout.Token));
            try
            {
                DiagnosticLog.Flush();
                DiagnosticLog.LocalAppDataOverride = root;
                ServerState.Config = new RvtMcpConfig();
                var builder = Host.CreateApplicationBuilder();
                builder.Logging.ClearProviders();
                var mcp = builder.Services.AddMcpServer().WithStreamServerTransport(input, input);
                PromptRequestLogging.Register(mcp).WithPrompts<RevitPrompts>();
                using var host = builder.Build();
                await host.StartAsync(timeout.Token);
                using var reader = new StreamReader(output, Encoding.UTF8, false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(output, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                try
                {
                    await Send(1, "initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "prompt-log-test", version = "1" } });
                    await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
                    var reply = await Send(2, "prompts/get", new { name = prompt, arguments = new { change = "DO_NOT_LOG_SENTINEL" } });
                    Assert.Equal(missing, reply["error"] != null);
                    Assert.True(DiagnosticLog.Flush());
                    var log = File.ReadAllText(Path.Combine(root, "Bimwright", "rvt-mcp", "rvt-mcp.log"));
                    var entry = Assert.Single(log.Split('\n').Where(line => line.Contains("[Prompts]")));
                    Assert.Contains("prompts/get name=" + prompt + " utc=", entry);
                    Assert.Contains(" session=", entry);
                    Assert.DoesNotContain("DO_NOT_LOG_SENTINEL", log);
                }
                finally { await host.StopAsync(timeout.Token); }

                async Task<JObject> Send(int id, string method, object parameters)
                {
                    await writer.WriteLineAsync(JsonConvert.SerializeObject(new { jsonrpc = "2.0", id, method, @params = parameters }));
                    while (true)
                    {
                        var reply = JObject.Parse(await reader.ReadLineAsync().WaitAsync(timeout.Token));
                        if (reply.Value<int?>("id") == id) return reply;
                    }
                }
            }
            finally
            {
                DiagnosticLog.Flush();
                DiagnosticLog.LocalAppDataOverride = previousRoot;
                ServerState.Config = previousConfig;
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
