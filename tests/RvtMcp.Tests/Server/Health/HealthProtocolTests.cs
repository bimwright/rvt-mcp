using System;
using System.Collections.Generic;
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
using RvtMcp.Server.Memory;
using Xunit;
using ServerAuthToken = RvtMcp.Server.AuthToken;

namespace RvtMcp.Tests;

[Collection("ServerStateConfig")]
public sealed class HealthProtocolTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fresh_handshake_catalog_and_no_host_health_keep_one_gateway_identity(bool readOnly)
    {
        var root = Path.Combine(Path.GetTempPath(), "rvt-health-protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var originalDir = ServerAuthToken.DiscoveryDirOverride;
        var originalConfig = ServerState.Config;
        var originalSend = ToolGateway.SendOverride;
        var name = "rvt-health-protocol-" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var input = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var output = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(input.WaitForConnectionAsync(timeout.Token), output.ConnectAsync(timeout.Token));
        try
        {
            ServerAuthToken.DiscoveryDirOverride = root;
            RevitTargetBinding.ResetForTests();
            ToolGateway.ResetForTests();
            ToolGateway.SendOverride = null;
            var config = new RvtMcpConfig { Toolsets = new List<string> { "meta" }, ReadOnly = readOnly };
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
                var initialized = await Request(writer, reader, 1, "initialize", new
                { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "health-contract-test", version = "1" } });
                await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
                var catalog = await Request(writer, reader, 2, "tools/list", new { });
                var tool = Assert.Single((JArray)catalog["result"]["tools"], t => t.Value<string>("name") == "revit_health");
                Assert.Equal("boolean", tool["inputSchema"]["properties"]["check_updates"].Value<string>("type"));
                Assert.DoesNotContain("check_updates", tool["inputSchema"]["required"]?.Values<string>() ?? Array.Empty<string>());
                Assert.True(tool["annotations"].Value<bool>("readOnlyHint"));
                Assert.False(tool["annotations"].Value<bool>("destructiveHint"));
                Assert.True(tool["annotations"].Value<bool>("idempotentHint"));
                Assert.True(tool["annotations"].Value<bool>("openWorldHint"));
                Assert.Contains("revit_health()", initialized["result"].Value<string>("instructions"));
                var called = await Request(writer, reader, 3, "tools/call", new { name = "revit_health", arguments = new { } });
                Assert.True(called["result"].Value<bool>("isError"));
                var health = JObject.Parse(called["result"]["content"][0].Value<string>("text"));
                Assert.False(health.Value<bool>("ok"));
                Assert.False(health.Value<bool>("host_connected"));
                Assert.StartsWith("NO_TARGET:", health.Value<string>("error"));
                Assert.Equal("rvt-mcp", health["setup_gateway"].Value<string>("product_id"));
                Assert.Equal("not_checked", health["update"].Value<string>("status"));
                Assert.Null(health["mcp"].Value<string>("plugin_version"));
                Assert.Equal("check_only", health["update_guidance"].Value<string>("mode"));
                Assert.DoesNotContain("install.ps1", health["update_guidance"].ToString());
                Assert.Equal(initialized["result"]["serverInfo"].Value<string>("version"), health["mcp"].Value<string>("server_informational_version"));
                var key = health["client_verification"].Value<string>("tools_list_metadata_key");
                Assert.True(JToken.DeepEquals(tool["_meta"][key], health["client_verification"]["expected_runtime"]));
                Assert.Equal("not_observable_by_server", health["client_verification"].Value<string>("status"));
            }
            finally { await host.StopAsync(timeout.Token); }
        }
        finally
        {
            ServerAuthToken.DiscoveryDirOverride = originalDir;
            ServerState.Config = originalConfig;
            ToolGateway.SendOverride = originalSend;
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
            var reply = JObject.Parse(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            if (reply.Value<int?>("id") != id) continue;
            Assert.Null(reply["error"]);
            return reply;
        }
    }
}
