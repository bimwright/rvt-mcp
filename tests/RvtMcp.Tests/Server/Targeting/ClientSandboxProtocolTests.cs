using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bimwright.Targeting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server;
using RvtMcp.Server.Memory;
using Xunit;
using ServerAuthToken = RvtMcp.Server.AuthToken;

namespace RvtMcp.Tests;

/// <summary>
/// FC-TGT-07: inside the client's AppContainer sandbox with nothing discovered, NO_TARGET becomes
/// CLIENT_SANDBOXED and the discovery/health tools explain why. Calls go through the real MCP server.
/// </summary>
[Collection("ServerStateConfig")]
public sealed class ClientSandboxProtocolTests
{
    private const int FakePid = 515151;
    private static readonly DateTime FakeStart = new DateTime(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc);

    private static readonly ClientSandboxResult Sandboxed = ClientSandbox.Classify(true,
        @"C:\Users\x\AppData\Local\Packages\Vendor.Client_abc123\AC");

    [Fact]
    public async Task FC_TGT_07_sandboxed_with_no_targets_reports_client_sandboxed_everywhere()
    {
        await WithServer(Sandboxed, liveTarget: false, async (call, dir) =>
        {
            var (listError, list) = await call("revit_list_available_targets");
            Assert.False(listError);
            Assert.Equal(0, list.Value<int>("count"));
            Assert.Empty((JArray)list["targets"]);
            Assert.Equal(ClientSandboxGuidance.Message, list.Value<string>("note"));
            AssertSandbox(list["sandbox"], dir);

            var (toolError, tool) = await call("revit_get_current_view_info");
            Assert.True(toolError);
            Assert.Equal(ClientSandboxGuidance.Message, tool.Value<string>("error"));
            Assert.StartsWith("CLIENT_SANDBOXED: rvt-mcp is running inside your MCP client's sandbox", tool.Value<string>("error"));
            Assert.Equal("CLIENT_SANDBOXED", tool["target"].Value<string>("code"));
            Assert.False(tool["target"].Value<bool>("sent"));
            Assert.Equal("not_started", tool["target"].Value<string>("outcome"));
            AssertSandbox(tool["target"]["sandbox"], dir);

            var (healthError, health) = await call("revit_health");
            Assert.True(healthError);
            Assert.False(health.Value<bool>("host_connected"));
            Assert.Equal(ClientSandboxGuidance.Message, health.Value<string>("error"));
            Assert.Equal("CLIENT_SANDBOXED", health["target"].Value<string>("code"));
            AssertSandbox(health["sandbox"], dir);

            var (switchError, sw) = await call("revit_switch_target", new { version = "pid:4242" });
            Assert.True(switchError);
            Assert.False(sw.Value<bool>("ok"));
            Assert.False(sw.Value<bool>("success"));
            Assert.Equal(ClientSandboxGuidance.Message, sw.Value<string>("error"));
            Assert.Equal("CLIENT_SANDBOXED", sw["target"].Value<string>("code"));
            Assert.Equal(JTokenType.Null, sw["target"]["recommended_next_tool"].Type);
            Assert.Equal(JTokenType.Null, sw["recommended_next_tool"].Type);
            AssertSandbox(sw["target"]["sandbox"], dir);
        });
    }

    [Fact]
    public async Task FC_TGT_07_not_sandboxed_keeps_no_target_unchanged()
    {
        await WithServer(ClientSandboxResult.NotDetected, liveTarget: false, async (call, dir) =>
        {
            var (_, list) = await call("revit_list_available_targets");
            Assert.Equal(0, list.Value<int>("count"));
            Assert.StartsWith("No revit-*.json descriptors found.", list.Value<string>("note"));
            Assert.Null(list["sandbox"]);

            var (toolError, tool) = await call("revit_get_current_view_info");
            Assert.True(toolError);
            Assert.StartsWith("NO_TARGET:", tool.Value<string>("error"));
            Assert.Equal("NO_TARGET", tool["target"].Value<string>("code"));
            Assert.Equal("revit_list_available_targets", tool["target"].Value<string>("recommended_next_tool"));
            Assert.Null(tool["target"]["sandbox"]);

            var (_, health) = await call("revit_health");
            Assert.StartsWith("NO_TARGET:", health.Value<string>("error"));
            Assert.Null(health["sandbox"]);

            var (switchError, sw) = await call("revit_switch_target", new { version = "pid:4242" });
            Assert.True(switchError);
            Assert.Equal("NO_TARGET", sw["target"].Value<string>("code"));
            Assert.Equal("revit_list_available_targets", sw["target"].Value<string>("recommended_next_tool"));
            Assert.Equal("revit_list_available_targets", sw.Value<string>("recommended_next_tool"));
            Assert.Null(sw["target"]["sandbox"]);
        });
    }

    [Fact]
    public async Task FC_TGT_07_sandboxed_with_a_live_target_changes_only_the_health_sandbox_block()
    {
        await WithServer(Sandboxed, liveTarget: true, async (call, dir) =>
        {
            var (listError, list) = await call("revit_list_available_targets");
            Assert.False(listError);
            Assert.Equal(1, list.Value<int>("count"));
            Assert.StartsWith("Pin an instance with revit_switch_target", list.Value<string>("note"));
            Assert.Null(list["sandbox"]);

            // The fake target has no listener: the normal connect failure, not CLIENT_SANDBOXED.
            var (toolError, tool) = await call("revit_get_current_view_info");
            Assert.True(toolError);
            Assert.Equal("TARGET_UNAVAILABLE", tool["target"].Value<string>("code"));
            Assert.StartsWith("TARGET_UNAVAILABLE:", tool.Value<string>("error"));
            Assert.Null(tool["target"]["sandbox"]);

            var (_, health) = await call("revit_health");
            Assert.StartsWith("TARGET_UNAVAILABLE:", health.Value<string>("error"));
            AssertSandbox(health["sandbox"], dir);
        });
    }

    private static void AssertSandbox(JToken sandbox, string dir)
    {
        Assert.NotNull(sandbox);
        Assert.True(sandbox.Value<bool>("detected"));
        Assert.Equal("appcontainer", sandbox.Value<string>("kind"));
        Assert.Equal(new[] { "token_is_appcontainer", "local_app_data_under_packages_ac" }, sandbox["evidence"].Values<string>());
        Assert.Equal(dir, sandbox.Value<string>("data_dir"));
    }

    private sealed class FakeProbe : IProcessProbe
    {
        public ProcessProbeResult Probe(int pid) => pid == FakePid
            ? new ProcessProbeResult(ProcessState.Running, FakeStart, "Autodesk Revit 2024 - [Test.rvt]")
            : new ProcessProbeResult(ProcessState.NotFound, null, null);
    }

    private delegate Task<(bool IsError, JObject Body)> Call(string tool, object arguments = null);

    private static async Task WithServer(ClientSandboxResult sandbox, bool liveTarget, Func<Call, string, Task> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "rvt-client-sandbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        if (liveTarget)
        {
            File.WriteAllText(Path.Combine(root, $"revit-2024-{FakePid}.json"), new JObject
            {
                ["schema_version"] = 3,
                ["host_app"] = "revit",
                ["host_year"] = 2024,
                ["target_id"] = $"revit-2024-{FakePid}",
                ["pid"] = FakePid,
                ["process_start_utc"] = FakeStart.ToString("o"),
                ["transport"] = "pipe",
                ["pipe_name"] = "RvtMcp-test-absent-" + Guid.NewGuid().ToString("N"),
                ["auth_token"] = "test-token",
                ["capabilities"] = new JArray()
            }.ToString());
        }
        var originalDir = ServerAuthToken.DiscoveryDirOverride;
        var originalConfig = ServerState.Config;
        var originalSend = ToolGateway.SendOverride;
        var originalSandbox = ClientSandbox.OverrideForTests;
        var name = "rvt-client-sandbox-" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var input = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var output = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(input.WaitForConnectionAsync(timeout.Token), output.ConnectAsync(timeout.Token));
        try
        {
            ServerAuthToken.DiscoveryDirOverride = root;
            ClientSandbox.OverrideForTests = sandbox;
            RevitTargetBinding.ResetForTests(new FakeProbe());
            ToolGateway.ResetForTests();
            ToolGateway.SendOverride = null;
            var config = new RvtMcpConfig { Toolsets = new List<string> { "query", "meta" } };
            ServerState.Config = config;
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            var mcp = builder.Services.AddMcpServer(Program.ConfigureMcpServerOptions).WithStreamServerTransport(input, input);
            mcp = RuntimeToolFilter.Register(mcp, config, new SessionContext(false, root));
            Program.RegisterToolsets(mcp, ToolsetFilter.Resolve(config), config);
            using var host = builder.Build();
            await host.StartAsync(timeout.Token);
            using var reader = new StreamReader(output, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(output, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            try
            {
                await Request(writer, reader, 1, "initialize", new
                { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "client-sandbox-test", version = "1" } });
                await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
                var id = 1;
                await body(async (tool, arguments) =>
                {
                    var reply = await Request(writer, reader, ++id, "tools/call", new { name = tool, arguments = arguments ?? new { } });
                    return (reply["result"].Value<bool?>("isError") == true,
                        JObject.Parse(reply["result"]["content"][0].Value<string>("text")));
                }, root);
            }
            finally { await host.StopAsync(timeout.Token); }
        }
        finally
        {
            ServerAuthToken.DiscoveryDirOverride = originalDir;
            ServerState.Config = originalConfig;
            ToolGateway.SendOverride = originalSend;
            ClientSandbox.OverrideForTests = originalSandbox;
            ToolGateway.ResetForTests();
            RevitTargetBinding.ResetForTests();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static async Task<JObject> Request(StreamWriter writer, StreamReader reader, int id, string method, object parameters)
    {
        await writer.WriteLineAsync(JsonConvert.SerializeObject(new { jsonrpc = "2.0", id, method, @params = parameters }));
        while (true)
        {
            var reply = JObject.Parse(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            if (reply.Value<int?>("id") != id) continue;
            Assert.Null(reply["error"]);
            return reply;
        }
    }
}
