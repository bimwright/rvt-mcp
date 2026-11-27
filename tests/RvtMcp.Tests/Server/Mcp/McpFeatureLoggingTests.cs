#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Failures;
using RvtMcp.Plugin;
using RvtMcp.Server;
using RvtMcp.Server.Memory;
using Xunit;
using ServerAuthToken = RvtMcp.Server.AuthToken;

namespace RvtMcp.Tests;

[Collection("ServerStateConfig")]
public sealed class McpFeatureLoggingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rvt-feature-log-" + Guid.NewGuid().ToString("N"));
    private readonly string _oldDiag = DiagnosticLog.LocalAppDataOverride;
    private readonly string _oldDir = ServerAuthToken.DiscoveryDirOverride;
    private readonly RvtMcpConfig _oldConfig = ServerState.Config;
    private readonly RvtMcp.ToolCatalog.ToolCatalog _oldCatalog = ServerState.ToolCatalog;
    private readonly Func<string, object, int?, Task<JObject>> _oldSend = ToolGateway.SendOverride;
    private readonly SessionContext _oldSession = ToolGateway.Session;
    private readonly string _oldClientName = ToolGateway.ClientName;
    private readonly Func<FailureLedgerStore> _oldLedger = FailureTools.StoreFactory;

    public McpFeatureLoggingTests()
    {
        Directory.CreateDirectory(_root);
        DiagnosticLog.Flush();
        DiagnosticLog.LocalAppDataOverride = _root;
        ServerAuthToken.DiscoveryDirOverride = _root;
        ToolGateway.ResetForTests();
        ToolGateway.SendOverride = null;
        ToolGateway.ClientName = null;
        ToolGateway.Session = new SessionContext(false, _root);
    }

    public void Dispose()
    {
        DiagnosticLog.Flush();
        ToolGateway.ResetForTests();
        DiagnosticLog.LocalAppDataOverride = _oldDiag;
        ServerAuthToken.DiscoveryDirOverride = _oldDir;
        ServerState.Config = _oldConfig;
        ServerState.ToolCatalog = _oldCatalog;
        ToolGateway.SendOverride = _oldSend;
        ToolGateway.Session = _oldSession;
        ToolGateway.ClientName = _oldClientName;
        FailureTools.StoreFactory = _oldLedger;
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void Token_and_task_id_formatters_sanitize_whitespace_controls_and_invalid_keys()
    {
        Assert.Equal("(unknown)", McpFeatureLogging.FormatToken(null));
        Assert.Equal("(unknown)", McpFeatureLogging.FormatToken("   "));
        Assert.Equal("claude_code_v2", McpFeatureLogging.FormatToken("  claude\ncode\tv2  "));
        Assert.Equal(64, McpFeatureLogging.FormatToken(new string('a', 100)).Length);

        Assert.Equal("abc123_op-1", McpFeatureLogging.FormatTaskId("abc123_op-1"));
        Assert.Equal("(unknown)", McpFeatureLogging.FormatTaskId("secret with spaces"));
        Assert.Equal("(unknown)", McpFeatureLogging.FormatTaskId("bad/path"));

        Assert.True(McpFeatureLogging.IsJuly2026OrLater("2026-07-28"));
        Assert.True(McpFeatureLogging.IsJuly2026OrLater("2026-12-01"));
        Assert.False(McpFeatureLogging.IsJuly2026OrLater("2025-11-25"));
        Assert.False(McpFeatureLogging.IsJuly2026OrLater("(unknown)"));
    }

    [Fact]
    public async Task Stdio_2026_07_28_client_logs_capabilities_once_and_task_opt_in_lifecycle()
    {
        var config = new RvtMcpConfig { Toolsets = new List<string> { "all" } };
        ServerState.Config = config;
        ServerState.ToolCatalog = ServerToolCatalogBuilder.Build(ToolsetFilter.Resolve(config), config);
        ToolGateway.SendOverride = (cmd, _, _) => Task.FromResult(new JObject
        {
            ["ok"] = true,
            ["command"] = cmd,
            ["clash_count"] = 0
        });

        var pipeName = "rvt-flog-2026-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var serverPipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var clientPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(serverPipe.WaitForConnectionAsync(cts.Token), clientPipe.ConnectAsync(cts.Token));

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        var mcp = builder.Services
            .AddMcpServer(Program.ConfigureMcpServerOptions)
            .WithStreamServerTransport(serverPipe, serverPipe);
        Program.RegisterMcpSurfaceWithTransport(mcp, ToolsetFilter.Resolve(config), config, "stdio");

        using var host = builder.Build();
        await host.StartAsync(cts.Token);
        try
        {
            var clientOptions = new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "claude-code", Version = "2.1.331" },
                Capabilities = new ClientCapabilities
                {
                    Elicitation = new ElicitationCapability { Form = new FormElicitationCapability() },
                    Extensions = new Dictionary<string, object>
                    {
                        [McpFeatureLogging.TasksExtensionId] = new JsonObject(),
                        [McpFeatureLogging.SkillsExtensionId] = new JsonObject()
                    }
                }
            };
            await using var client = await McpClient.CreateAsync(
                new StreamClientTransport(clientPipe, clientPipe),
                clientOptions,
                cancellationToken: cts.Token);

            // Multiple calls from the same client/capability set must deduplicate [McpSession].
            await client.ListToolsAsync(cancellationToken: cts.Token);
            await client.ListToolsAsync(cancellationToken: cts.Token);

            // Eligible tool called via MCP Tasks (SEP-2663).
            var created = await client.CallToolAsTaskAsync(
                new CallToolRequestParams
                {
                    Name = "revit_clash_detection",
                    Arguments = new Dictionary<string, JsonElement>
                    {
                        ["categoriesA"] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { "Walls" }),
                        ["categoriesB"] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { "Ducts" })
                    }
                },
                cts.Token);
            Assert.True(created.IsTask);
            var taskId = created.TaskCreated!.TaskId;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            GetTaskResult status;
            while (true)
            {
                status = await client.GetTaskAsync(taskId, cancellationToken: cts.Token);
                if (status is CompletedTaskResult || status is FailedTaskResult || DateTime.UtcNow >= deadline)
                    break;
                await Task.Delay(25, cts.Token);
            }
            Assert.IsType<CompletedTaskResult>(status);

            Assert.True(DiagnosticLog.Flush());
            var log = ReadLog();

            var sessionLines = log.Split('\n').Where(l => l.Contains("[McpSession]", StringComparison.Ordinal)).ToArray();
            var sessionLine = Assert.Single(sessionLines);
            Assert.Contains("session/capabilities transport=stdio protocol=2026-07-28 client=claude-code tasks=1 skills=1 elicitation=1", sessionLine);

            Assert.Contains("[Tasks] tasks/eligible tool=revit_clash_detection mode=task protocol=2026-07-28 client=claude-code", log);
            Assert.Contains($"[Tasks] tasks/create task={taskId}", log);
            Assert.Contains($"[Tasks] tasks/get task={taskId} status=completed source=memory", log);
            Assert.Contains($"[Tasks] tasks/complete task={taskId} is_error=0 after_cancel=0", log);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Legacy_2025_11_25_client_logs_sync_mode_for_task_eligible_tool()
    {
        var config = new RvtMcpConfig { Toolsets = new List<string> { "all" } };
        ServerState.Config = config;
        ServerState.ToolCatalog = ServerToolCatalogBuilder.Build(ToolsetFilter.Resolve(config), config);
        ToolGateway.SendOverride = (cmd, _, _) => Task.FromResult(new JObject { ["ok"] = true, ["command"] = cmd });

        var pipeName = "rvt-flog-2025-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var serverPipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var clientPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(serverPipe.WaitForConnectionAsync(cts.Token), clientPipe.ConnectAsync(cts.Token));

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        var mcp = builder.Services
            .AddMcpServer(Program.ConfigureMcpServerOptions)
            .WithStreamServerTransport(serverPipe, serverPipe);
        Program.RegisterMcpSurfaceWithTransport(mcp, ToolsetFilter.Resolve(config), config, "stdio");

        using var host = builder.Build();
        await host.StartAsync(cts.Token);
        using var reader = new StreamReader(clientPipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(clientPipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        try
        {
            await SendRawAsync(writer, reader, 1, "initialize", new
            {
                protocolVersion = "2025-11-25",
                capabilities = new { },
                clientInfo = new { name = "legacy-desktop", version = "1.0" }
            }, cts.Token);
            await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await SendRawAsync(writer, reader, 2, "tools/call", new
            {
                name = "revit_clash_detection",
                arguments = new { categoriesA = new[] { "Walls" }, categoriesB = new[] { "Ducts" } }
            }, cts.Token);

            Assert.True(DiagnosticLog.Flush());
            var log = ReadLog();
            Assert.Contains("[McpSession] session/capabilities transport=stdio protocol=2025-11-25 client=legacy-desktop tasks=0 skills=0 elicitation=0", log);
            Assert.Contains("[Tasks] tasks/eligible tool=revit_clash_detection mode=sync protocol=2025-11-25 client=legacy-desktop", log);
            Assert.DoesNotContain("[Tasks] tasks/create", log);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Stateless_http_transport_logs_http_transport_and_distinct_clients()
    {
        var config = new RvtMcpConfig { Toolsets = new List<string> { "meta" } };
        ServerState.Config = config;
        ServerState.ToolCatalog = ServerToolCatalogBuilder.Build(ToolsetFilter.Resolve(config), config);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var app = Program.BuildHttpApp(config, "http://127.0.0.1:0", registerMaintenance: false);
        await app.StartAsync(cts.Token);
        try
        {
            var endpoint = new Uri(app.Urls.First());
            await using var clientA = await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions { Endpoint = endpoint }),
                new McpClientOptions { ClientInfo = new Implementation { Name = "http-agent-a", Version = "1.0" } },
                cancellationToken: cts.Token);
            await using var clientB = await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions { Endpoint = endpoint }),
                new McpClientOptions { ClientInfo = new Implementation { Name = "http-agent-b", Version = "1.0" } },
                cancellationToken: cts.Token);

            await clientA.CallToolAsync("revit_get_workflow", cancellationToken: cts.Token);
            await clientB.CallToolAsync("revit_get_workflow",
                new Dictionary<string, object?> { ["name"] = "change" }, cancellationToken: cts.Token);

            Assert.True(DiagnosticLog.Flush());
            var log = ReadLog();
            Assert.Contains("[McpSession] session/capabilities transport=http protocol=2026-07-28 client=http-agent-a", log);
            Assert.Contains("[McpSession] session/capabilities transport=http protocol=2026-07-28 client=http-agent-b", log);
            Assert.Contains("[Workflows] tools/revit_get_workflow name=(list) ", log);
            Assert.Contains("client=http-agent-a", log);
            Assert.Contains("[Workflows] tools/revit_get_workflow name=change ", log);
            Assert.Contains("client=http-agent-b", log);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Task_store_logs_dispatch_cancel_stages_recovery_and_redacts_invalid_task_ids()
    {
        var ledger = new FailureLedgerStore(Path.Combine(_root, "ledger.db"));
        ledger.Initialize();
        var model = new FailureModelIdentity
        {
            PhysicalKey = "phys-log-test",
            Title = "LogTest.rvt",
            LiveDocumentId = "doc-log-test",
            HostInstanceId = "revit-2024-99999"
        };
        var resolved = ledger.ResolveModel(model);
        var recoveredId = "recoveredop1234";
        var now = DateTimeOffset.UtcNow;
        ledger.SaveOperation(new FailureOperation
        {
            OperationId = recoveredId,
            HostInstanceId = model.HostInstanceId,
            Tool = "export_ifc",
            RevitYear = 2024,
            ModelScopeKeys = new List<string> { resolved.ModelScopeKey },
            Execution = "running",
            Outcome = "unknown",
            Complete = false,
            StartedUtc = now.AddMinutes(-1),
            UpdatedUtc = now
        });

        var store = new OperationBackedTaskStore(
            ledgerFactory: () => new FailureLedgerStore(ledger.DatabasePath, readOnly: true),
            sessionStatusLookup: _ => null);

        // Pre-dispatch cancel
        var preTask = await store.CreateTaskAsync();
        RevitTaskContext.Current = null;
        Assert.True(await store.SetCancelledAsync(preTask.TaskId));

        // Post-dispatch cancel + error completion
        var postTask = await store.CreateTaskAsync();
        RevitTaskContext.Current = null;
        Assert.True(store.TryMarkDispatched(postTask.TaskId, "export_ifc", CancellationToken.None));
        Assert.False(await store.SetCancelledAsync(postTask.TaskId));
        var errJson = System.Text.Json.JsonSerializer.SerializeToElement(new CallToolResult
        {
            IsError = true,
            Content = new List<ContentBlock> { new TextContentBlock { Text = "{\"success\":false}" } }
        }, ModelContextProtocol.McpJsonUtilities.DefaultOptions);
        await store.SetCompletedAsync(postTask.TaskId, errJson);
        Assert.False(await store.SetCancelledAsync(postTask.TaskId)); // terminal

        // Recovered cancel + get + invalid missing task id (must be redacted)
        Assert.False(await store.SetCancelledAsync(recoveredId));
        Assert.NotNull(await store.GetTaskAsync(recoveredId));
        Assert.Null(await store.GetTaskAsync("SECRET SENTINEL ID"));

        Assert.True(DiagnosticLog.Flush());
        var log = ReadLog();
        Assert.DoesNotContain("SECRET SENTINEL ID", log);
        Assert.Contains($"[Tasks] tasks/cancel task={preTask.TaskId} stage=pre_dispatch", log);
        Assert.Contains($"[Tasks] tasks/dispatch task={postTask.TaskId} tool=export_ifc readonly=0", log);
        Assert.Contains($"[Tasks] tasks/cancel task={postTask.TaskId} stage=post_dispatch", log);
        Assert.Contains($"[Tasks] tasks/complete task={postTask.TaskId} is_error=1 after_cancel=1", log);
        Assert.Contains($"[Tasks] tasks/cancel task={postTask.TaskId} stage=terminal", log);
        Assert.Contains($"[Tasks] tasks/cancel task={recoveredId} stage=recovered", log);
        Assert.Contains($"[Tasks] tasks/get task={recoveredId} status=working source=ledger", log);
        Assert.Contains("[Tasks] tasks/get task=(unknown) status=missing source=missing", log);
    }

    private string ReadLog()
    {
        var path = Path.Combine(_root, "Bimwright", "rvt-mcp", "rvt-mcp.log");
        Assert.True(File.Exists(path), "Expected DiagnosticLog file at " + path);
        return File.ReadAllText(path);
    }

    private static async Task<JObject> SendRawAsync(
        StreamWriter writer, StreamReader reader, int id, string method, object parameters, CancellationToken ct)
    {
        await writer.WriteLineAsync(JsonConvert.SerializeObject(new { jsonrpc = "2.0", id, method, @params = parameters }));
        while (true)
        {
            var reply = JObject.Parse((await reader.ReadLineAsync().WaitAsync(ct))!);
            if (reply.Value<int?>("id") != id) continue;
            Assert.Null(reply["error"]);
            return reply;
        }
    }
}
