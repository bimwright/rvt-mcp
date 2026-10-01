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
using RvtMcp.Server.Prompts;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("ServerStateConfig")]
    public class RuntimeProtocolTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MCP_filter_delivers_and_journals_actual_gateway_changes_on_success_or_error(bool failed)
        {
            var root = Path.Combine(Path.GetTempPath(), "rvt-changes-" + Guid.NewGuid().ToString("N"));
            var original = ServerState.Config;
            var originalSend = ToolGateway.SendOverride;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var pipeName = "rvt-changes-" + Guid.NewGuid().ToString("N");
            using var input = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var output = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.WhenAll(input.WaitForConnectionAsync(deadline.Token), output.ConnectAsync(deadline.Token));
            try
            {
                var config = new RvtMcpConfig { Toolsets = new List<string> { "meta" }, EnableCallLog = true };
                ServerState.Config = config;
                var session = new SessionContext(true, root);
                ToolGateway.SendOverride = async (command, args, timeout) =>
                {
                    await Task.Yield();
                    var a = new DocumentChangeAccumulator(); a.Observe(12, "modified", "Walls");
                    return ToolGateway.InterpretResponse(new JObject
                    {
                        ["success"] = !failed, ["data"] = new JObject { ["executed"] = true },
                        ["error"] = failed ? "failure after commit" : null,
                        ["changes"] = new JObject { ["complete"] = true, ["documents"] = new JArray(a.Snapshot("Sample")) }
                    });
                };
                var builder = Host.CreateApplicationBuilder(); builder.Logging.ClearProviders();
                var mcp = builder.Services.AddMcpServer().WithStreamServerTransport(input, input);
                mcp = RuntimeToolFilter.Register(mcp, config, session);
                Program.RegisterToolsets(mcp, ToolsetFilter.Resolve(config), config);
                using var host = builder.Build(); await host.StartAsync(deadline.Token);
                await using var client = await McpClient.CreateAsync(new StreamClientTransport(output, output), cancellationToken: deadline.Token);
                var response = await client.CallToolAsync("revit_send_code_to_revit",
                    new Dictionary<string, object> { ["code"] = "return null;" }, cancellationToken: deadline.Token);
                Assert.Equal(failed, response.IsError == true);
                var data = JObject.Parse(((TextContentBlock)response.Content[0]).Text);
                Assert.Equal(12, data["_changes"]["documents"][0]["modified"]["ids"][0].Value<int>());
                var entry = Assert.Single(session.Journal.ReadDay(DateTime.UtcNow.ToString("yyyy-MM-dd")));
                Assert.Equal(12, entry.Changes["documents"][0]["modified"]["ids"][0].Value<int>());
                await client.DisposeAsync(); await host.StopAsync(deadline.Token);
            }
            finally
            {
                ServerState.Config = original; ToolGateway.SendOverride = originalSend;
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Theory]
        [InlineData(null, false, true)]
        [InlineData("all", false, true)]
        [InlineData("query,meta", true, true)]
        [InlineData("query", true, false)]
        [InlineData("meta", false, false)]
        public async Task Real_MCP_protocol_lists_and_renders_change_without_calling_Revit(
            string toolsets, bool readOnly, bool rendersBody)
        {
            var original = ServerState.Config;
            var originalSend = ToolGateway.SendOverride;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var pipeName = "rvt-prompts-" + Guid.NewGuid().ToString("N");
            using var input = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var output = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.WhenAll(input.WaitForConnectionAsync(deadline.Token), output.ConnectAsync(deadline.Token));
            try
            {
                ServerState.Config = new RvtMcpConfig
                {
                    Toolsets = toolsets == null ? null : new List<string>(toolsets.Split(',')),
                    ReadOnly = readOnly, EnableSendCode = false
                };
                var calls = 0;
                ToolGateway.SendOverride = (command, args, timeout) =>
                {
                    calls++;
                    throw new InvalidOperationException("Rendering a prompt must not call Revit.");
                };
                var builder = Host.CreateApplicationBuilder();
                builder.Logging.ClearProviders();
                builder.Services.AddMcpServer().WithStreamServerTransport(input, input)
                    .WithPrompts<RevitPrompts>();
                using var host = builder.Build();
                await host.StartAsync(deadline.Token);
                await using var client = await McpClient.CreateAsync(
                    new StreamClientTransport(output, output), cancellationToken: deadline.Token);

                var prompts = await client.ListPromptsAsync(cancellationToken: deadline.Token);
                Assert.Equal(new[] { "revit_change", "revit_getting_started", "revit_model_audit",
                    "revit_pre_issue_check", "revit_stairs" }, prompts.Select(p => p.Name).OrderBy(n => n));
                var change = Assert.Single(prompts, p => p.Name == "revit_change");
                var argument = Assert.Single(change.ProtocolPrompt.Arguments);
                Assert.Equal("change", argument.Name);
                Assert.True(argument.Required);
                const string request = "Set Comments on element 123: phối hợp";
                var result = await client.GetPromptAsync("revit_change",
                    new Dictionary<string, object> { ["change"] = request }, cancellationToken: deadline.Token);
                var body = Assert.IsType<TextContentBlock>(Assert.Single(result.Messages).Content).Text;
                if (rendersBody)
                {
                    Assert.Contains("Requested change: " + request, body);
                    Assert.Equal(readOnly, body.Contains("Session: READ-ONLY:"));
                }
                else
                {
                    Assert.Contains("--toolsets query,meta", body);
                    Assert.DoesNotContain("# Disciplined model change", body);
                }
                await Assert.ThrowsAnyAsync<ModelContextProtocol.McpException>(() =>
                    client.GetPromptAsync("revit_change", cancellationToken: deadline.Token).AsTask());
                Assert.Equal(0, calls);
                await client.DisposeAsync();
                await host.StopAsync(deadline.Token);
            }
            finally
            {
                ServerState.Config = original;
                ToolGateway.SendOverride = originalSend;
            }
        }

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
