using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server;
using RvtMcp.Server.Memory;
using Xunit;
using ServerAuthToken = RvtMcp.Server.AuthToken;

namespace RvtMcp.Tests;

[Collection("ServerStateConfig")]
public sealed class HttpTransportProtocolTests
{
    [Fact]
    // SDK-RVT-HTTP: explicitly choose Stateless Streamable HTTP on 2.4.0 instead of relying on implicit defaults,
    // and ensure startup diagnostics do not claim legacy /sse.
    public void ConfigureHttpTransport_sets_explicit_stateless_mode_and_startup_message()
    {
        var opts = new HttpServerTransportOptions();
        Program.ConfigureHttpTransport(opts);

        Assert.Equal(HttpServerSessionMode.Stateless, opts.SessionMode);
        Assert.True(opts.Stateless);

        var startup = Program.FormatHttpStartupMessage("http://127.0.0.1:8200");
        Assert.Contains("Streamable HTTP (stateless)", startup, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:8200", startup, StringComparison.Ordinal);
        Assert.DoesNotContain("SSE", startup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    // SDK-RVT-HTTP: Streamable HTTP stateless transport serves MCP 2026-07-28 server/discover, tools/list, and tools/call
    // (updating ToolGateway.ClientName) on localhost, disables legacy /sse and standalone GET streams, and refuses non-localhost Hosts.
    public async Task Stateless_http_endpoint_serves_2026_07_28_and_blocks_non_localhost_and_legacy_sse()
    {
        var root = Path.Combine(Path.GetTempPath(), "rvt-http-protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var originalDir = ServerAuthToken.DiscoveryDirOverride;
        var originalConfig = ServerState.Config;
        var originalSend = ToolGateway.SendOverride;
        var originalSession = ToolGateway.Session;
        var originalClientName = ToolGateway.ClientName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        try
        {
            ServerAuthToken.DiscoveryDirOverride = root;
            RevitTargetBinding.ResetForTests();
            ToolGateway.ResetForTests();
            ToolGateway.SendOverride = null;
            ToolGateway.ClientName = null;

            var config = new RvtMcpConfig { Toolsets = new List<string> { "meta" }, ReadOnly = true };
            ServerState.Config = config;
            ToolGateway.Session = new SessionContext(false, root);

            await using var app = Program.BuildHttpApp(config, "http://127.0.0.1:0", registerMaintenance: false);
            await app.StartAsync(timeout.Token);
            try
            {
                var listenUrl = app.Urls.First();
                using var http = new HttpClient { BaseAddress = new Uri(listenUrl) };

                var modernMeta = new JObject
                {
                    ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                    ["io.modelcontextprotocol/clientCapabilities"] = new JObject(),
                    ["io.modelcontextprotocol/clientInfo"] = new JObject { ["name"] = "rvt-http-test", ["version"] = "1.0.0" }
                };

                var discover = await PostJsonRpcAsync(http, 1, "server/discover", new JObject { ["_meta"] = modernMeta }, timeout.Token);
                Assert.Null(discover["error"]);
                var versions = ((JArray)discover["result"]["supportedVersions"]).Values<string>().ToArray();
                Assert.Contains("2026-07-28", versions);
                Assert.Equal("rvt-mcp", discover["result"]?["_meta"]?["io.modelcontextprotocol/serverInfo"]?.Value<string>("name"));

                var listed = await PostJsonRpcAsync(http, 2, "tools/list", new JObject { ["_meta"] = modernMeta }, timeout.Token);
                Assert.Null(listed["error"]);
                var names = ((JArray)listed["result"]["tools"]).Select(t => t.Value<string>("name")).ToArray();
                Assert.Contains("revit_health", names);

                await using var client = await McpClient.CreateAsync(
                    new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(listenUrl) }),
                    new McpClientOptions
                    {
                        ProtocolVersion = "2026-07-28",
                        ClientInfo = new Implementation { Name = "rvt-http-client", Version = "1.0.0" }
                    },
                    cancellationToken: timeout.Token);
                Assert.Equal("2026-07-28", client.NegotiatedProtocolVersion);
                var healthResult = await client.CallToolAsync("revit_health", cancellationToken: timeout.Token);
                Assert.True(healthResult.IsError == true);
                Assert.Equal("rvt-http-client", ToolGateway.ClientName);

                using var legacySseGet = await http.GetAsync("/sse", timeout.Token);
                Assert.Equal(HttpStatusCode.NotFound, legacySseGet.StatusCode);

                using var standaloneStreamGet = await http.GetAsync("/", timeout.Token);
                Assert.Equal(HttpStatusCode.MethodNotAllowed, standaloneStreamGet.StatusCode);

                using var evilReq = new HttpRequestMessage(HttpMethod.Post, "/")
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
                evilReq.Headers.Host = "evil.example:8200";
                using var blocked = await http.SendAsync(evilReq, timeout.Token);
                Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
            }
            finally
            {
                await app.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            ServerAuthToken.DiscoveryDirOverride = originalDir;
            ServerState.Config = originalConfig;
            ToolGateway.SendOverride = originalSend;
            ToolGateway.Session = originalSession;
            ToolGateway.ClientName = originalClientName;
            ToolGateway.ResetForTests();
            RevitTargetBinding.ResetForTests();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static async Task<JObject> PostJsonRpcAsync(
        HttpClient http, int id, string method, JObject parameters, CancellationToken ct, string toolName = null)
    {
        var payload = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters
        }.ToString(Formatting.None);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", method);
        if (!string.IsNullOrEmpty(toolName))
            request.Headers.Add("Mcp-Name", toolName);

        using var response = await http.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        var mediaType = response.Content.Headers.ContentType?.MediaType;

        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in body.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (trimmed.StartsWith("data:", StringComparison.Ordinal))
                {
                    var json = trimmed.Substring("data:".Length).Trim();
                    if (!string.IsNullOrEmpty(json))
                        return JObject.Parse(json);
                }
            }
            throw new InvalidOperationException("No JSON data payload found in SSE response: " + body);
        }

        return JObject.Parse(body);
    }
}
