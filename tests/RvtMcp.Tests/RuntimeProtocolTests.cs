using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server;
using RvtMcp.Server.Memory;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("ServerStateConfig")]
    public class RuntimeProtocolTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task Real_MCP_protocol_exposes_annotations_filters_calls_and_obeys_logging(bool readOnly, bool log)
        {
            var root = Path.Combine(Path.GetTempPath(), "rvt-protocol-" + Guid.NewGuid().ToString("N"));
            var original = ServerState.Config;
            var originalSend = ToolGateway.SendOverride;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            // A separate duplex pipe is only the test transport; no Revit endpoint is opened.
            var pipeName = "rvt-protocol-" + Guid.NewGuid().ToString("N");
            using var input = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var output = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.WhenAll(input.WaitForConnectionAsync(deadline.Token), output.ConnectAsync(deadline.Token));
            try
            {
                var config = new RvtMcpConfig
                {
                    Toolsets = new List<string> { "all" }, ReadOnly = readOnly, EnableCallLog = log,
                    EnableSendCode = false, ResponseWarnBytes = 2048, ResponseStrongWarnBytes = 4096,
                    ResponseBudgetBytes = 8192, MaxResponseBytes = 32768
                };
                ServerState.Config = config;
                var calls = 0;
                ToolGateway.SendOverride = (command, args, timeout) =>
                {
                    calls++;
                    return Task.FromResult(new JObject { ["detail"] = new string('x', 30000), ["count"] = 3 });
                };
                var session = new SessionContext(log, Path.Combine(root, "journal"));
                var builder = Host.CreateApplicationBuilder();
                builder.Logging.ClearProviders();
                var mcp = builder.Services.AddMcpServer().WithStreamServerTransport(input, input);
                mcp = RuntimeToolFilter.Register(mcp, config, session);
                Program.RegisterToolsets(mcp, ToolsetFilter.Resolve(config), config);
                using var host = builder.Build();
                await host.StartAsync(deadline.Token);
                await using var client = await McpClient.CreateAsync(new StreamClientTransport(output, output), cancellationToken: deadline.Token);
                var tools = await client.ListToolsAsync(cancellationToken: deadline.Token);
                Assert.DoesNotContain(tools, tool => tool.Name == "revit_send_code_to_revit");
                if (readOnly) Assert.All(tools, tool => Assert.True(tool.ProtocolTool.Annotations.ReadOnlyHint));
                Assert.Contains(tools, tool => tool.Name == "revit_list_schedules");
                var response = await client.CallToolAsync("revit_get_element_details",
                    new Dictionary<string, object> { ["elementIds"] = new long[] { 1 } }, cancellationToken: deadline.Token);
                Assert.True(response.IsError);
                Assert.Contains("RESPONSE_TOO_LARGE", ((TextContentBlock)response.Content[0]).Text);
                Assert.Equal(1, calls);
                response = await client.CallToolAsync("revit_get_current_target", cancellationToken: deadline.Token);
                Assert.NotEqual(true, response.IsError);
                Assert.Equal(2, session.CallCount);
                Assert.Equal(log, Directory.Exists(Path.Combine(root, "journal")));
                if (log) Assert.Equal(2, File.ReadAllLines(Directory.GetFiles(Path.Combine(root, "journal"))[0]).Length);
                if (!readOnly)
                {
                    response = await client.CallToolAsync("revit_create_level",
                        new Dictionary<string, object> { ["elevation"] = 3000 }, cancellationToken: deadline.Token);
                    Assert.NotEqual(true, response.IsError);
                    Assert.True(JObject.Parse(((TextContentBlock)response.Content[0]).Text).Value<bool>("mutation_applied"));
                    Assert.Equal(2, calls);
                }
                await client.DisposeAsync();
                await host.StopAsync(deadline.Token);
            }
            finally
            {
                ServerState.Config = original;
                ToolGateway.SendOverride = originalSend;
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
