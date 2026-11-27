using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Bimwright.Targeting;
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
public sealed class McpTasksProtocolTests : IDisposable
{
    private const int FakePid = 515151;
    private static readonly DateTime FakeStart = new DateTime(2026, 11, 27, 1, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rvt-tasks-" + Guid.NewGuid().ToString("N"));
    private readonly string _oldDir = ServerAuthToken.DiscoveryDirOverride;
    private readonly RvtMcpConfig _oldConfig = ServerState.Config;
    private readonly RvtMcp.ToolCatalog.ToolCatalog _oldCatalog = ServerState.ToolCatalog;
    private readonly Func<string, object, int?, Task<JObject>> _oldSend = ToolGateway.SendOverride;
    private readonly SessionContext _oldSession = ToolGateway.Session;
    private readonly ChangeHistoryStore _oldHistory = ToolGateway.History;
    private readonly string _oldClientName = ToolGateway.ClientName;
    private readonly Func<FailureLedgerStore> _oldLedger = FailureTools.StoreFactory;
    private readonly FailureLedgerStore _ledger;

    public McpTasksProtocolTests()
    {
        Directory.CreateDirectory(_root);
        ServerAuthToken.DiscoveryDirOverride = _root;
        ToolGateway.ResetForTests();
        ToolGateway.SendOverride = null;
        ToolGateway.ClientName = null;
        ToolGateway.Session = new SessionContext(false, _root);
        ToolGateway.History = new ChangeHistoryStore(Path.Combine(_root, "history"));
        _ledger = new FailureLedgerStore(Path.Combine(_root, "ledger.db"));
        _ledger.Initialize();
        FailureTools.StoreFactory = () => new FailureLedgerStore(_ledger.DatabasePath, readOnly: true);
        RevitTargetBinding.ResetForTests(new FakeProbe());
    }

    public void Dispose()
    {
        ToolGateway.ResetForTests();
        RevitTargetBinding.ResetForTests();
        ServerAuthToken.DiscoveryDirOverride = _oldDir;
        ServerState.Config = _oldConfig;
        ServerState.ToolCatalog = _oldCatalog;
        ToolGateway.SendOverride = _oldSend;
        ToolGateway.Session = _oldSession;
        ToolGateway.History = _oldHistory;
        ToolGateway.ClientName = _oldClientName;
        FailureTools.StoreFactory = _oldLedger;
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private sealed class FakeProbe : IProcessProbe
    {
        public ProcessProbeResult Probe(int pid) => pid == FakePid
            ? new ProcessProbeResult(ProcessState.Running, FakeStart, "Test Revit 2024")
            : new ProcessProbeResult(ProcessState.NotFound, null, null);
    }

    [Theory]
    [InlineData("revit_clash_detection")]
    [InlineData("revit_batch_export_sheets")]
    [InlineData("revit_export_ifc")]
    [InlineData("revit_export_nwc")]
    // TASK-OPT-IN: All 4 designated long-run tools run as MCP Tasks when client opts in on 2026-07-28,
    // and execute synchronously when client omits tasks opt-in or negotiates legacy 2025-11-25.
    public async Task Designated_tools_opt_in_to_tasks_and_keep_sync_fallback_for_non_task_clients(string toolName)
    {
        var config = new RvtMcpConfig { Toolsets = new List<string> { "geometry", "export", "query", "meta" }, EnableChangeHistory = false };
        ServerState.Config = config;
        ServerState.ToolCatalog = ServerToolCatalogBuilder.Build(ToolsetFilter.Resolve(config), config);
        ToolGateway.SendOverride = (cmd, _, _) => Task.FromResult(new JObject { ["command"] = cmd, ["ok"] = true });

        var store = new OperationBackedTaskStore(
            pollIntervalMs: 25,
            ledgerFactory: () => new FailureLedgerStore(_ledger.DatabasePath, readOnly: true));

        // 1. Modern 2026-07-28 client opting in via CallToolAsTaskAsync gets a task for the 4 designated tools.
        await WithPipeHostAsync(config, store, async client =>
        {
            Assert.Equal("2026-07-28", client.NegotiatedProtocolVersion);
            var raw = await client.CallToolAsTaskAsync(
                new CallToolRequestParams { Name = toolName, Arguments = SampleArgs(toolName) },
                CancellationToken.None);
            Assert.True(raw.IsTask);
            Assert.NotNull(raw.TaskCreated);
            var completed = await WaitForCompletionAsync(client, raw.TaskCreated.TaskId);
            var callResult = System.Text.Json.JsonSerializer.Deserialize<CallToolResult>(
                completed.Result.GetRawText(), ModelContextProtocol.McpJsonUtilities.DefaultOptions);
            Assert.NotNull(callResult);
            Assert.False(callResult.IsError == true);
            Assert.Contains("true", ((TextContentBlock)callResult.Content[0]).Text);
            Assert.Equal("claude-code", ToolGateway.ClientName);
        }, protocolVersion: "2026-07-28");

        // 2. Same 2026-07-28 client calling WITHOUT tasks opt-in (CallToolAsync) receives synchronous result.
        await WithPipeHostAsync(config, store, async client =>
        {
            var sync = await client.CallToolAsync(toolName, RawObjectArgs(toolName), cancellationToken: CancellationToken.None);
            Assert.False(sync.IsError == true);
            Assert.Contains("true", ((TextContentBlock)sync.Content[0]).Text);
        }, protocolVersion: "2026-07-28");

        // 3. Legacy 2025-11-25 client receives ordinary synchronous result.
        await WithPipeHostAsync(config, store, async client =>
        {
            Assert.Equal("2025-11-25", client.NegotiatedProtocolVersion);
            var legacy = await client.CallToolAsync(toolName, RawObjectArgs(toolName), cancellationToken: CancellationToken.None);
            Assert.False(legacy.IsError == true);
            Assert.Contains("true", ((TextContentBlock)legacy.Content[0]).Text);
        }, protocolVersion: "2025-11-25");
    }

    [Fact]
    // TASK-OPT-IN: Tools outside the 4 designated tools remain strictly synchronous even when caller uses CallToolAsTaskAsync.
    public async Task Non_task_tools_stay_synchronous_even_with_task_opt_in()
    {
        var config = new RvtMcpConfig { Toolsets = new List<string> { "query", "meta" }, EnableChangeHistory = false };
        ServerState.Config = config;
        ToolGateway.SendOverride = (cmd, _, _) => Task.FromResult(new JObject { ["view_name"] = "Level 1" });
        var store = new OperationBackedTaskStore(
            pollIntervalMs: 25,
            ledgerFactory: () => new FailureLedgerStore(_ledger.DatabasePath, readOnly: true));

        await WithPipeHostAsync(config, store, async client =>
        {
            var raw = await client.CallToolAsTaskAsync(
                new CallToolRequestParams { Name = "revit_get_current_view_info", Arguments = new Dictionary<string, JsonElement>() },
                CancellationToken.None);
            Assert.False(raw.IsTask);
            Assert.NotNull(raw.Result);
            Assert.Contains("Level 1", ((TextContentBlock)raw.Result.Content[0]).Text);
        }, protocolVersion: "2026-07-28");
    }

    [Fact]
    // TASK-TIMING: server/discover advertises Tasks capability, and default store sets ttlMs >= 3,600,000 (1h)
    // and pollIntervalMs = 10,000 (10-30s guidance).
    public async Task Discover_and_create_task_publish_recommended_ttl_and_poll_interval()
    {
        var config = new RvtMcpConfig { Toolsets = new List<string> { "geometry", "meta" }, EnableChangeHistory = false };
        ServerState.Config = config;
        ToolGateway.SendOverride = (_, _, _) => Task.FromResult(new JObject { ["ok"] = true });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pipe = "rvt-tasks-wire-" + Guid.NewGuid().ToString("N");
        using var input = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var output = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(input.WaitForConnectionAsync(timeout.Token), output.ConnectAsync(timeout.Token));

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        var mcp = builder.Services.AddMcpServer(Program.ConfigureMcpServerOptions).WithStreamServerTransport(input, input);
        mcp = RuntimeToolFilter.Register(mcp, config, ToolGateway.Session);
        mcp = RevitTaskSupport.Register(mcp); // Default OperationBackedTaskStore
        Program.RegisterToolsets(mcp, ToolsetFilter.Resolve(config), config);
        using var host = builder.Build();
        await host.StartAsync(timeout.Token);

        using var reader = new StreamReader(output, Encoding.UTF8, false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(output, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        try
        {
            var meta = new JObject
            {
                ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                ["io.modelcontextprotocol/clientCapabilities"] = new JObject
                {
                    ["extensions"] = new JObject { [TasksProtocol.ExtensionId] = new JObject() }
                },
                ["io.modelcontextprotocol/clientInfo"] = new JObject { ["name"] = "task-timing-test", ["version"] = "1.0" }
            };

            var discover = await RawRequestAsync(writer, reader, 1, "server/discover", new JObject { ["_meta"] = meta });
            Assert.NotNull(discover["result"]?["capabilities"]?["extensions"]?[TasksProtocol.ExtensionId]);

            var createReply = await RawRequestAsync(writer, reader, 2, "tools/call", new JObject
            {
                ["name"] = "revit_clash_detection",
                ["arguments"] = JObject.FromObject(RawObjectArgs("revit_clash_detection")),
                ["_meta"] = meta
            });
            var result = (JObject)createReply["result"]!;
            Assert.Equal("task", result.Value<string>("resultType"));
            Assert.Equal(3600000L, result.Value<long>("ttlMs"));
            Assert.Equal(10000L, result.Value<long>("pollIntervalMs"));
            var taskId = result.Value<string>("taskId");
            Assert.True(FailureCapture.IsKey(taskId));
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    // TASK-CANCEL-HONEST (pre-dispatch): cancelling via MCP tasks/cancel before the wire write transitions to Cancelled,
    // prevents dispatch to Revit, and never surfaces an ObjectDisposedException.
    public async Task Pre_dispatch_cancel_over_mcp_transitions_to_cancelled_and_never_writes_to_host()
    {
        var store = new OperationBackedTaskStore(
            pollIntervalMs: 25,
            ledgerFactory: () => new FailureLedgerStore(_ledger.DatabasePath, readOnly: true));
        var created = await store.CreateTaskAsync(CancellationToken.None);
        RevitTaskContext.Current = null;
        Assert.Equal(McpTaskStatus.Working, created.Status);
        Assert.True(await store.SetCancelledAsync(created.TaskId, CancellationToken.None));
        var directState = await store.GetTaskAsync(created.TaskId, CancellationToken.None);
        Assert.Equal(McpTaskStatus.Cancelled, directState!.Status);
        Assert.Contains("nothing started", directState.StatusMessage, StringComparison.OrdinalIgnoreCase);

        // Now test full MCP protocol path blocked right before WriteRequest via BeforeWriteForTests.
        var config = new RvtMcpConfig { Toolsets = new List<string> { "geometry", "meta" }, EnableChangeHistory = false };
        ServerState.Config = config;
        var hostWrites = 0;
        using var host = new FakeWireHost((_, _) => Interlocked.Increment(ref hostWrites));
        WriteDiscoveryAndPin(host.Port);

        var reachedPreWrite = new ManualResetEventSlim();
        var releasePreWrite = new ManualResetEventSlim();
        ToolGateway.BeforeWriteForTests = () =>
        {
            reachedPreWrite.Set();
            releasePreWrite.Wait(TimeSpan.FromSeconds(5));
        };

        try
        {
            await WithPipeHostAsync(config, store, async client =>
            {
                var raw = await client.CallToolAsTaskAsync(
                    new CallToolRequestParams { Name = "revit_clash_detection", Arguments = SampleArgs("revit_clash_detection") },
                    CancellationToken.None);
                Assert.True(raw.IsTask);
                var taskId = raw.TaskCreated!.TaskId;

                Assert.True(await Task.Run(() => reachedPreWrite.Wait(TimeSpan.FromSeconds(5))));
                await client.CancelTaskAsync(taskId, CancellationToken.None);
                releasePreWrite.Set();

                // Wait briefly for the background task pipeline to finish cleanup cleanly without flipping Cancelled.
                await Task.Delay(100);
                var state = await client.GetTaskAsync(taskId, CancellationToken.None);
                var cancelled = Assert.IsType<CancelledTaskResult>(state);
                Assert.Contains("nothing started", cancelled.StatusMessage, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(0, Volatile.Read(ref hostWrites));
            }, protocolVersion: "2026-07-28");
        }
        finally
        {
            releasePreWrite.Set();
            ToolGateway.BeforeWriteForTests = null;
        }
    }

    [Fact]
    // TASK-CANCEL-HONEST (post-dispatch, FC-BUSY-04): once sent to Revit, tasks/cancel never marks the task Cancelled;
    // it remains Working with an honest warning citing operationId and revit_get_operation_status, then completes when host finishes.
    public async Task Post_dispatch_cancel_keeps_task_working_with_honest_warning_until_host_completes()
    {
        var config = new RvtMcpConfig { Toolsets = new List<string> { "geometry", "meta" }, EnableChangeHistory = false };
        ServerState.Config = config;
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHost = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        ToolGateway.SendOverride = async (_, _, _) =>
        {
            started.TrySetResult(true);
            return await releaseHost.Task;
        };

        var store = new OperationBackedTaskStore(
            pollIntervalMs: 25,
            ledgerFactory: () => new FailureLedgerStore(_ledger.DatabasePath, readOnly: true));

        await WithPipeHostAsync(config, store, async client =>
        {
            var raw = await client.CallToolAsTaskAsync(
                new CallToolRequestParams { Name = "revit_clash_detection", Arguments = SampleArgs("revit_clash_detection") },
                CancellationToken.None);
            Assert.True(raw.IsTask);
            var taskId = raw.TaskCreated!.TaskId;
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await client.CancelTaskAsync(taskId, CancellationToken.None);

            var afterCancel = await client.GetTaskAsync(taskId, CancellationToken.None);
            var working = Assert.IsType<WorkingTaskResult>(afterCancel);
            Assert.Equal(McpTaskStatus.Working, working.Status);
            Assert.Contains(taskId, working.StatusMessage);
            Assert.Contains("cannot stop", working.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("revit_get_operation_status", working.StatusMessage);

            releaseHost.TrySetResult(new JObject { ["clashCount"] = 2 });
            var completed = await WaitForCompletionAsync(client, taskId);
            Assert.Contains("after cancellation was requested", completed.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("2", completed.Result.GetRawText());
        }, protocolVersion: "2026-07-28");
    }

    [Fact]
    // TASK-ON-OPERATION: wire command carries operation_id == taskId; fake host dialog_wait keeps task Working
    // while both tasks/get and revit_get_operation_status reflect waiting_for_user until final completion frame arrives.
    public async Task Task_bridges_live_operation_dialog_wait_and_completes_on_final_frame()
    {
        var config = new RvtMcpConfig { Toolsets = new List<string> { "geometry", "meta" }, EnableChangeHistory = false };
        ServerState.Config = config;

        var dialogSent = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        JObject capturedRequest = null;

        using var host = new FakeWireHost((req, wire) =>
        {
            capturedRequest = req;
            var reqId = req.Value<string>("id");
            var opId = req.Value<string>("operation_id");
            wire.Send(new JObject
            {
                ["id"] = reqId,
                ["frame"] = "event",
                ["event"] = "dialog_wait",
                ["_operation"] = new JObject
                {
                    ["operationId"] = opId,
                    ["execution"] = "waiting_for_user",
                    ["outcome"] = "unknown",
                    ["complete"] = false,
                    ["dialog"] = new JObject { ["kind"] = "window", ["title"] = "Resolve Link Dialog" }
                }
            });
            dialogSent.TrySetResult(opId);
        });
        WriteDiscoveryAndPin(host.Port);

        var store = new OperationBackedTaskStore(
            pollIntervalMs: 25,
            ledgerFactory: () => new FailureLedgerStore(_ledger.DatabasePath, readOnly: true));

        await WithPipeHostAsync(config, store, async client =>
        {
            var raw = await client.CallToolAsTaskAsync(
                new CallToolRequestParams { Name = "revit_clash_detection", Arguments = SampleArgs("revit_clash_detection") },
                CancellationToken.None);
            Assert.True(raw.IsTask);
            var taskId = raw.TaskCreated!.TaskId;

            var wireOpId = await dialogSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(taskId, wireOpId);
            Assert.Equal(taskId, capturedRequest?.Value<string>("operation_id"));

            // Poll until gateway commits waiting_for_user to PreviousOperations.
            WorkingTaskResult working = null;
            for (var i = 0; i < 40; i++)
            {
                var state = await client.GetTaskAsync(taskId, CancellationToken.None);
                working = Assert.IsType<WorkingTaskResult>(state);
                if (working.StatusMessage != null && working.StatusMessage.Contains("Resolve Link Dialog", StringComparison.Ordinal))
                    break;
                await Task.Delay(25);
            }
            Assert.Contains("Waiting for user", working!.StatusMessage);
            Assert.Contains("Resolve Link Dialog", working.StatusMessage);

            var statusJson = await FailureTools.GetOperationStatus(taskId);
            var status = JObject.Parse(statusJson);
            Assert.True(status.Value<bool>("available"));
            Assert.Equal("waiting_for_user", status.Value<string>("execution"));

            // Send final completion frame from fake Revit host.
            host.Send(new JObject
            {
                ["id"] = capturedRequest!.Value<string>("id"),
                ["success"] = true,
                ["data"] = new JObject { ["clashCount"] = 0, ["status"] = "clean" },
                ["_operation"] = new JObject
                {
                    ["operationId"] = taskId,
                    ["execution"] = "completed",
                    ["outcome"] = "not_started",
                    ["complete"] = true,
                    ["mutationApplied"] = false
                }
            });

            var completed = await WaitForCompletionAsync(client, taskId);
            var callResult = System.Text.Json.JsonSerializer.Deserialize<CallToolResult>(
                completed.Result.GetRawText(), ModelContextProtocol.McpJsonUtilities.DefaultOptions);
            Assert.NotNull(callResult);
            Assert.False(callResult.IsError == true);
            Assert.Contains("\"clean\"", ((TextContentBlock)callResult.Content[0]).Text);
        }, protocolVersion: "2026-07-28");
    }

    [Fact]
    // TASK-RECOVER: a fresh gateway instance with no in-memory task state recovers running, committed (with or
    // without FinalResult), rolled_back, partial, and interrupted_unknown operations over MCP tasks/get and revit_get_operation_status.
    public async Task Fresh_store_recovers_operations_from_ledger_and_matches_operation_status_tool()
    {
        var model = new FailureModelIdentity
        {
            ModelKey = "recover-model",
            PhysicalKey = "phys-recover",
            Title = "Recover.rvt",
            LiveDocumentId = "doc-recover",
            HostInstanceId = "revit-2024-" + FakePid
        };
        var resolved = _ledger.ResolveModel(model);

        var runningId = Guid.NewGuid().ToString("N");
        var committedWithResultId = Guid.NewGuid().ToString("N");
        var committedNoResultId = Guid.NewGuid().ToString("N");
        var readSucceededId = Guid.NewGuid().ToString("N");
        var readFailedId = Guid.NewGuid().ToString("N");
        var rolledBackId = Guid.NewGuid().ToString("N");
        var interruptedId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;

        _ledger.SaveOperation(new FailureOperation
        {
            OperationId = runningId,
            HostInstanceId = model.HostInstanceId,
            Tool = "revit_export_ifc",
            RevitYear = 2024,
            ModelScopeKeys = new List<string> { resolved.ModelScopeKey },
            Execution = "running",
            Outcome = "unknown",
            Complete = false,
            StartedUtc = now.AddMinutes(-2),
            UpdatedUtc = now.AddMinutes(-1)
        });
        _ledger.SaveOperation(new FailureOperation
        {
            OperationId = committedWithResultId,
            HostInstanceId = model.HostInstanceId,
            Tool = "revit_batch_export_sheets",
            RevitYear = 2024,
            ModelScopeKeys = new List<string> { resolved.ModelScopeKey },
            Execution = "completed",
            Outcome = "committed",
            MutationApplied = true,
            Complete = true,
            FinalResult = new JObject { ["success"] = true },
            StartedUtc = now.AddMinutes(-5),
            UpdatedUtc = now.AddMinutes(-4)
        });
        _ledger.SaveOperation(new FailureOperation
        {
            OperationId = committedNoResultId,
            HostInstanceId = model.HostInstanceId,
            Tool = "revit_export_nwc",
            RevitYear = 2024,
            ModelScopeKeys = new List<string> { resolved.ModelScopeKey },
            Execution = "completed",
            Outcome = "committed",
            MutationApplied = true,
            Complete = true,
            StartedUtc = now.AddMinutes(-6),
            UpdatedUtc = now.AddMinutes(-5)
        });
        // Read-only commands persisted after a dialog/incident record Outcome="not_started" on clean success (FailureCompletion.Apply).
        _ledger.SaveOperation(new FailureOperation
        {
            OperationId = readSucceededId,
            HostInstanceId = model.HostInstanceId,
            Tool = "clash_detection",
            RevitYear = 2024,
            ModelScopeKeys = new List<string> { resolved.ModelScopeKey },
            Execution = "completed",
            Outcome = "not_started",
            MutationApplied = false,
            Complete = true,
            RequiresReview = false,
            FinalResult = new JObject { ["success"] = true },
            StartedUtc = now.AddMinutes(-6),
            UpdatedUtc = now.AddMinutes(-5)
        });
        _ledger.SaveOperation(new FailureOperation
        {
            OperationId = readFailedId,
            HostInstanceId = model.HostInstanceId,
            Tool = "clash_detection",
            RevitYear = 2024,
            ModelScopeKeys = new List<string> { resolved.ModelScopeKey },
            Execution = "completed",
            Outcome = "not_started",
            MutationApplied = false,
            Complete = true,
            RequiresReview = false,
            FinalResult = new JObject { ["success"] = false, ["error"] = "Clash failed." },
            StartedUtc = now.AddMinutes(-6),
            UpdatedUtc = now.AddMinutes(-5)
        });
        _ledger.SaveOperation(new FailureOperation
        {
            OperationId = rolledBackId,
            HostInstanceId = model.HostInstanceId,
            Tool = "revit_export_ifc",
            RevitYear = 2024,
            ModelScopeKeys = new List<string> { resolved.ModelScopeKey },
            Execution = "completed",
            Outcome = "rolled_back",
            MutationApplied = false,
            Complete = true,
            FinalResult = new JObject { ["success"] = false, ["error"] = "Export rolled back." },
            StartedUtc = now.AddMinutes(-7),
            UpdatedUtc = now.AddMinutes(-6)
        });
        _ledger.SaveOperation(new FailureOperation
        {
            OperationId = interruptedId,
            HostInstanceId = model.HostInstanceId,
            Tool = "revit_batch_export_sheets",
            RevitYear = 2024,
            ModelScopeKeys = new List<string> { resolved.ModelScopeKey },
            Execution = "interrupted_unknown",
            Outcome = "unknown",
            Complete = false,
            StartedUtc = now.AddMinutes(-8),
            UpdatedUtc = now.AddMinutes(-7)
        });

        var config = new RvtMcpConfig { Toolsets = new List<string> { "meta" }, EnableChangeHistory = false };
        ServerState.Config = config;
        var freshStore = new OperationBackedTaskStore(
            pollIntervalMs: 25,
            ledgerFactory: () => new FailureLedgerStore(_ledger.DatabasePath, readOnly: true),
            sessionStatusLookup: _ => null);

        await WithPipeHostAsync(config, freshStore, async client =>
        {
            var runningTask = Assert.IsType<WorkingTaskResult>(await client.GetTaskAsync(runningId, CancellationToken.None));
            Assert.Contains("recovered from ledger", runningTask.StatusMessage, StringComparison.OrdinalIgnoreCase);

            var committedWithResult = Assert.IsType<CompletedTaskResult>(await client.GetTaskAsync(committedWithResultId, CancellationToken.None));
            var committedWithResultCall = DeserializeCallResult(committedWithResult);
            Assert.False(committedWithResultCall.IsError == true);
            Assert.Contains(committedWithResultId, ((TextContentBlock)committedWithResultCall.Content[0]).Text);
            Assert.Contains("revit_batch_export_sheets", ((TextContentBlock)committedWithResultCall.Content[0]).Text);

            var committedNoResult = Assert.IsType<CompletedTaskResult>(await client.GetTaskAsync(committedNoResultId, CancellationToken.None));
            var committedNoResultCall = DeserializeCallResult(committedNoResult);
            Assert.False(committedNoResultCall.IsError == true);
            Assert.Contains("revit_export_nwc", ((TextContentBlock)committedNoResultCall.Content[0]).Text);

            var readSucceeded = Assert.IsType<CompletedTaskResult>(await client.GetTaskAsync(readSucceededId, CancellationToken.None));
            var readSucceededCall = DeserializeCallResult(readSucceeded);
            Assert.False(readSucceededCall.IsError == true);
            Assert.Contains("clash_detection", ((TextContentBlock)readSucceededCall.Content[0]).Text);

            var readFailed = Assert.IsType<CompletedTaskResult>(await client.GetTaskAsync(readFailedId, CancellationToken.None));
            var readFailedCall = DeserializeCallResult(readFailed);
            Assert.True(readFailedCall.IsError == true);
            Assert.Contains("Clash failed.", ((TextContentBlock)readFailedCall.Content[0]).Text);

            var rolledBack = Assert.IsType<CompletedTaskResult>(await client.GetTaskAsync(rolledBackId, CancellationToken.None));
            var rolledBackCall = DeserializeCallResult(rolledBack);
            Assert.True(rolledBackCall.IsError == true);
            Assert.Contains("rolled_back", ((TextContentBlock)rolledBackCall.Content[0]).Text);

            var interrupted = Assert.IsType<CompletedTaskResult>(await client.GetTaskAsync(interruptedId, CancellationToken.None));
            var interruptedCall = DeserializeCallResult(interrupted);
            Assert.True(interruptedCall.IsError == true);
            Assert.Contains("interrupted_unknown", ((TextContentBlock)interruptedCall.Content[0]).Text);
            Assert.Contains("revit_get_operation_status", ((TextContentBlock)interruptedCall.Content[0]).Text);
        }, protocolVersion: "2026-07-28");

        var opStatus = JObject.Parse(await FailureTools.GetOperationStatus(committedWithResultId));
        Assert.True(opStatus.Value<bool>("available"));
        Assert.Equal("completed", opStatus.Value<string>("execution"));
        Assert.Equal("committed", opStatus.Value<string>("outcome"));
    }

    [Fact]
    // TASK-ON-OPERATION: an export's durable ledger row ("running") must not shadow more specific live
    // session state ("timed_out_running" or early wire "waiting_for_user").
    public async Task Live_status_prefers_session_timed_out_running_and_dialog_wait_over_ledger_running()
    {
        var model = new FailureModelIdentity
        {
            ModelKey = "shadow-model",
            PhysicalKey = "phys-shadow",
            Title = "Shadow.rvt",
            LiveDocumentId = "doc-shadow",
            HostInstanceId = "revit-2024-" + FakePid
        };
        var resolved = _ledger.ResolveModel(model);
        JObject currentSession = null;

        var store = new OperationBackedTaskStore(
            pollIntervalMs: 25,
            ledgerFactory: () => new FailureLedgerStore(_ledger.DatabasePath, readOnly: true),
            sessionStatusLookup: _ => currentSession);

        var created = await store.CreateTaskAsync(CancellationToken.None);
        RevitTaskContext.Current = null;
        Assert.True(store.TryMarkDispatched(created.TaskId, "export_ifc", CancellationToken.None));

        var now = DateTimeOffset.UtcNow;
        _ledger.SaveOperation(new FailureOperation
        {
            OperationId = created.TaskId,
            HostInstanceId = model.HostInstanceId,
            Tool = "export_ifc",
            RevitYear = 2024,
            ModelScopeKeys = new List<string> { resolved.ModelScopeKey },
            Execution = "running",
            Outcome = "unknown",
            Complete = false,
            StartedUtc = now.AddMinutes(-2),
            UpdatedUtc = now.AddMinutes(-1)
        });

        currentSession = new JObject { ["execution"] = "timed_out_running" };
        var timedOut = await store.GetTaskAsync(created.TaskId, CancellationToken.None);
        Assert.Equal(McpTaskStatus.Working, timedOut!.Status);
        Assert.Contains("gateway wait timeout", timedOut.StatusMessage, StringComparison.OrdinalIgnoreCase);

        currentSession = new JObject
        {
            ["execution"] = "waiting_for_user",
            ["windowDialog"] = new JObject { ["title"] = "IFC Export Warning" }
        };
        var dialogWait = await store.GetTaskAsync(created.TaskId, CancellationToken.None);
        Assert.Equal(McpTaskStatus.Working, dialogWait!.Status);
        Assert.Contains("Waiting for user", dialogWait.StatusMessage);
        Assert.Contains("IFC Export Warning", dialogWait.StatusMessage);
    }

    [Fact]
    // TASK-CANCEL-HONEST (post-dispatch over FakeWireHost, level F, FC-BUSY-04): once written to the wire,
    // tasks/cancel keeps the task Working with an honest warning until the fake Revit host sends its final frame.
    public async Task Post_dispatch_cancel_over_fake_wire_host_keeps_working_until_final_frame()
    {
        var config = new RvtMcpConfig { Toolsets = new List<string> { "export", "meta" }, EnableChangeHistory = false };
        ServerState.Config = config;

        var wireReceived = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = new FakeWireHost((req, _) => wireReceived.TrySetResult(req));
        WriteDiscoveryAndPin(host.Port);

        var store = new OperationBackedTaskStore(
            pollIntervalMs: 25,
            ledgerFactory: () => new FailureLedgerStore(_ledger.DatabasePath, readOnly: true));

        await WithPipeHostAsync(config, store, async client =>
        {
            var raw = await client.CallToolAsTaskAsync(
                new CallToolRequestParams { Name = "revit_export_ifc", Arguments = SampleArgs("revit_export_ifc") },
                CancellationToken.None);
            Assert.True(raw.IsTask);
            var taskId = raw.TaskCreated!.TaskId;

            var req = await wireReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(taskId, req.Value<string>("operation_id"));

            await client.CancelTaskAsync(taskId, CancellationToken.None);

            var afterCancel = Assert.IsType<WorkingTaskResult>(await client.GetTaskAsync(taskId, CancellationToken.None));
            Assert.Contains("cannot stop", afterCancel.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("revit_get_operation_status", afterCancel.StatusMessage);

            host.Send(new JObject
            {
                ["id"] = req.Value<string>("id"),
                ["success"] = true,
                ["data"] = new JObject { ["exported"] = true },
                ["_operation"] = new JObject
                {
                    ["operationId"] = taskId,
                    ["execution"] = "completed",
                    ["outcome"] = "committed",
                    ["complete"] = true,
                    ["mutationApplied"] = true
                }
            });

            var completed = await WaitForCompletionAsync(client, taskId);
            Assert.Contains("after cancellation was requested", completed.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("exported", completed.Result.GetRawText());
        }, protocolVersion: "2026-07-28");
    }

    [Fact]
    // TASK-CANCEL-HONEST + TASK-RECOVER: cancelling a running operation recovered from the ledger on a fresh gateway
    // preserves the honest warning across subsequent tasks/get polls and after completion.
    public async Task Cancel_on_ledger_recovered_running_task_records_honest_warning()
    {
        var model = new FailureModelIdentity
        {
            ModelKey = "cancel-recover-model",
            PhysicalKey = "phys-cancel-recover",
            Title = "CancelRecover.rvt",
            LiveDocumentId = "doc-cancel-recover",
            HostInstanceId = "revit-2024-" + FakePid
        };
        var resolved = _ledger.ResolveModel(model);
        var taskId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;

        _ledger.SaveOperation(new FailureOperation
        {
            OperationId = taskId,
            HostInstanceId = model.HostInstanceId,
            Tool = "export_ifc",
            RevitYear = 2024,
            ModelScopeKeys = new List<string> { resolved.ModelScopeKey },
            Execution = "running",
            Outcome = "unknown",
            Complete = false,
            StartedUtc = now.AddMinutes(-2),
            UpdatedUtc = now.AddMinutes(-1)
        });

        var config = new RvtMcpConfig { Toolsets = new List<string> { "meta" }, EnableChangeHistory = false };
        ServerState.Config = config;
        var freshStore = new OperationBackedTaskStore(
            pollIntervalMs: 25,
            ledgerFactory: () => new FailureLedgerStore(_ledger.DatabasePath, readOnly: true),
            sessionStatusLookup: _ => null);

        await WithPipeHostAsync(config, freshStore, async client =>
        {
            await client.CancelTaskAsync(taskId, CancellationToken.None);

            var working = Assert.IsType<WorkingTaskResult>(await client.GetTaskAsync(taskId, CancellationToken.None));
            Assert.Contains("cannot stop", working.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("revit_get_operation_status", working.StatusMessage);

            _ledger.SaveOperation(new FailureOperation
            {
                OperationId = taskId,
                HostInstanceId = model.HostInstanceId,
                Tool = "export_ifc",
                RevitYear = 2024,
                ModelScopeKeys = new List<string> { resolved.ModelScopeKey },
                Execution = "completed",
                Outcome = "committed",
                MutationApplied = true,
                Complete = true,
                FinalResult = new JObject { ["success"] = true },
                StartedUtc = now.AddMinutes(-2),
                UpdatedUtc = DateTimeOffset.UtcNow
            });

            var completed = Assert.IsType<CompletedTaskResult>(await client.GetTaskAsync(taskId, CancellationToken.None));
            Assert.Contains("after cancellation was requested", completed.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }, protocolVersion: "2026-07-28");
    }

    [Fact]
    // TASK-TIMING: SweepExpired never evicts an active Working task, keeps completed results for the full TTL
    // measured from completion time (both in-memory and from ledger), and distinguishes error completions.
    public async Task Sweep_keeps_working_tasks_and_retains_completed_results_for_full_ttl_after_completion()
    {
        var clock = new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);
        var store = new OperationBackedTaskStore(
            timeToLive: TimeSpan.FromHours(1),
            pollIntervalMs: 25,
            ledgerFactory: () => new FailureLedgerStore(_ledger.DatabasePath, readOnly: true),
            sessionStatusLookup: _ => null,
            utcNow: () => clock);

        // 1. Long-running Working task survives past 70m and still completes cleanly.
        var longRun = await store.CreateTaskAsync(CancellationToken.None);
        RevitTaskContext.Current = null;
        clock = clock.AddMinutes(70);
        store.SweepExpired(clock);
        var stillWorking = await store.GetTaskAsync(longRun.TaskId, CancellationToken.None);
        Assert.NotNull(stillWorking);
        Assert.Equal(McpTaskStatus.Working, stillWorking!.Status);

        var errCall = System.Text.Json.JsonSerializer.SerializeToElement(
            new CallToolResult { IsError = true, Content = new List<ContentBlock> { new TextContentBlock { Text = "Error: failed" } } },
            ModelContextProtocol.McpJsonUtilities.DefaultOptions);
        await store.SetCompletedAsync(longRun.TaskId, errCall, CancellationToken.None);
        var errDone = await store.GetTaskAsync(longRun.TaskId, CancellationToken.None);
        Assert.Equal(McpTaskStatus.Completed, errDone!.Status);
        Assert.Contains("Completed with error", errDone.StatusMessage);

        // 2. Result completed 45m after creation stays available 50m after completion (95m after creation)
        //    and expires at >= 60m after completion.
        var t0 = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
        clock = t0;
        var timed = await store.CreateTaskAsync(CancellationToken.None);
        RevitTaskContext.Current = null;

        clock = t0.AddMinutes(45);
        var okCall = System.Text.Json.JsonSerializer.SerializeToElement(
            new CallToolResult { IsError = false, Content = new List<ContentBlock> { new TextContentBlock { Text = "{\"ok\":true}" } } },
            ModelContextProtocol.McpJsonUtilities.DefaultOptions);
        await store.SetCompletedAsync(timed.TaskId, okCall, CancellationToken.None);

        clock = t0.AddMinutes(95); // 50m after completion (< 1h TTL)
        store.SweepExpired(clock);
        Assert.NotNull(await store.GetTaskAsync(timed.TaskId, CancellationToken.None));

        clock = t0.AddMinutes(106); // 61m after completion (>= 1h TTL)
        store.SweepExpired(clock);
        Assert.Null(await store.GetTaskAsync(timed.TaskId, CancellationToken.None));

        // 3. Ledger recovery also measures TTL from UpdatedUtc (completion), not StartedUtc.
        var model = new FailureModelIdentity
        {
            ModelKey = "ttl-model",
            PhysicalKey = "phys-ttl",
            Title = "Ttl.rvt",
            LiveDocumentId = "doc-ttl",
            HostInstanceId = "revit-2024-" + FakePid
        };
        var resolved = _ledger.ResolveModel(model);
        var ledgerId = Guid.NewGuid().ToString("N");
        _ledger.SaveOperation(new FailureOperation
        {
            OperationId = ledgerId,
            HostInstanceId = model.HostInstanceId,
            Tool = "export_ifc",
            RevitYear = 2024,
            ModelScopeKeys = new List<string> { resolved.ModelScopeKey },
            Execution = "completed",
            Outcome = "committed",
            MutationApplied = true,
            Complete = true,
            FinalResult = new JObject { ["success"] = true },
            StartedUtc = t0,
            UpdatedUtc = t0.AddMinutes(45)
        });

        clock = t0.AddMinutes(95); // 50m after UpdatedUtc → still recoverable
        Assert.NotNull(await store.GetTaskAsync(ledgerId, CancellationToken.None));

        clock = t0.AddMinutes(106); // 61m after UpdatedUtc → expired
        Assert.Null(await store.GetTaskAsync(ledgerId, CancellationToken.None));
    }

    private static CallToolResult DeserializeCallResult(CompletedTaskResult completed)
        => System.Text.Json.JsonSerializer.Deserialize<CallToolResult>(
            completed.Result.GetRawText(), ModelContextProtocol.McpJsonUtilities.DefaultOptions)!;

    [Fact]
    // TASK-OPT-IN + SDK-RVT-HTTP: Stateless Streamable HTTP shares a singleton OperationBackedTaskStore across requests.
    public async Task Stateless_http_transport_supports_opt_in_tasks_across_polling_requests()
    {
        var config = new RvtMcpConfig { Toolsets = new List<string> { "geometry", "meta" }, EnableChangeHistory = false };
        ServerState.Config = config;
        ToolGateway.SendOverride = (cmd, _, _) => Task.FromResult(new JObject { ["command"] = cmd, ["clashCount"] = 0 });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var app = Program.BuildHttpApp(config, "http://127.0.0.1:0", registerMaintenance: false);
        await app.StartAsync(timeout.Token);
        try
        {
            var listenUrl = app.Urls.First();
            await using var client = await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(listenUrl) }),
                new McpClientOptions
                {
                    ProtocolVersion = "2026-07-28",
                    ClientInfo = new Implementation { Name = "rvt-http-task-client", Version = "1.0.0" }
                },
                cancellationToken: timeout.Token);

            var raw = await client.CallToolAsTaskAsync(
                new CallToolRequestParams { Name = "revit_clash_detection", Arguments = SampleArgs("revit_clash_detection") },
                timeout.Token);
            Assert.True(raw.IsTask);
            var completed = await WaitForCompletionAsync(client, raw.TaskCreated!.TaskId);
            Assert.Contains("clash_detection", completed.Result.GetRawText());
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    private async Task WithPipeHostAsync(
        RvtMcpConfig config,
        OperationBackedTaskStore store,
        Func<McpClient, Task> run,
        string protocolVersion)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pipe = "rvt-tasks-" + Guid.NewGuid().ToString("N");
        using var input = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var output = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(input.WaitForConnectionAsync(timeout.Token), output.ConnectAsync(timeout.Token));

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        var mcp = builder.Services.AddMcpServer(Program.ConfigureMcpServerOptions).WithStreamServerTransport(input, input);
        mcp = RuntimeToolFilter.Register(mcp, config, ToolGateway.Session);
        mcp = RevitTaskSupport.Register(mcp, store);
        Program.RegisterToolsets(mcp, ToolsetFilter.Resolve(config), config);
        using var host = builder.Build();
        await host.StartAsync(timeout.Token);
        try
        {
            await using var client = await McpClient.CreateAsync(
                new StreamClientTransport(output, output),
                new McpClientOptions
                {
                    ProtocolVersion = protocolVersion,
                    ClientInfo = new Implementation { Name = "claude-code", Version = "2.1.330" }
                },
                cancellationToken: timeout.Token);
            await run(client);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<CompletedTaskResult> WaitForCompletionAsync(McpClient client, string taskId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var state = await client.GetTaskAsync(taskId, timeout.Token);
            if (state is CompletedTaskResult done)
                return done;
            if (state is FailedTaskResult failed)
                throw new InvalidOperationException("Task failed unexpectedly: " + failed.Error.GetRawText());
            if (state is CancelledTaskResult)
                throw new InvalidOperationException("Task cancelled unexpectedly.");
            await Task.Delay(25, timeout.Token);
        }
    }

    private static async Task<JObject> RawRequestAsync(StreamWriter writer, StreamReader reader, int id, string method, object parameters)
    {
        await writer.WriteLineAsync(JsonConvert.SerializeObject(new { jsonrpc = "2.0", id, method, @params = parameters }));
        while (true)
        {
            var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var msg = JObject.Parse(line!);
            if (msg.Value<int?>("id") != id) continue;
            Assert.Null(msg["error"]);
            return msg;
        }
    }

    private void WriteDiscoveryAndPin(int port)
    {
        var descriptor = new JObject
        {
            ["schema_version"] = 3,
            ["host_app"] = "revit",
            ["host_year"] = 2024,
            ["pid"] = FakePid,
            ["process_start_utc"] = FakeStart.ToString("o"),
            ["target_id"] = "revit-2024-" + FakePid,
            ["transport"] = "tcp",
            ["port"] = port,
            ["auth_token"] = "test-token",
            ["capabilities"] = new JArray("tool_catalog", "controlled_failures_v1", "document_target_v1")
        };
        var path = Path.Combine(_root, "revit-2024-" + FakePid + ".json");
        File.WriteAllText(path, descriptor.ToString(Formatting.None));
        RevitTargetBinding.Switch(TargetSelector.ForPid(FakePid), RevitTargetBinding.Scan().Live);
        RevitTargetBinding.Binding.ConfirmDocument(RevitTargetBinding.CaptureGeneration(), "doc-" + FakePid);
    }

    private static Dictionary<string, object> RawObjectArgs(string toolName) => toolName switch
    {
        "revit_clash_detection" => new Dictionary<string, object> { ["categoriesA"] = new[] { "Walls" }, ["categoriesB"] = new[] { "Ducts" } },
        "revit_batch_export_sheets" => new Dictionary<string, object> { ["outputFolder"] = "C:\\Temp\\out", ["format"] = "pdf" },
        "revit_export_ifc" => new Dictionary<string, object> { ["outputFolder"] = "C:\\Temp\\out", ["fileName"] = "model" },
        "revit_export_nwc" => new Dictionary<string, object> { ["outputFolder"] = "C:\\Temp\\out", ["fileName"] = "model" },
        _ => throw new ArgumentOutOfRangeException(nameof(toolName))
    };

    private static Dictionary<string, JsonElement> SampleArgs(string toolName)
        => RawObjectArgs(toolName).ToDictionary(
            kvp => kvp.Key,
            kvp => System.Text.Json.JsonSerializer.SerializeToElement(kvp.Value));

    private sealed class FakeWireHost : IDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly object _gate = new object();
        private TcpClient _client;
        private StreamWriter _writer;
        private readonly Action<JObject, FakeWireHost> _onCommand;
        public int Port { get; }

        public FakeWireHost(Action<JObject, FakeWireHost> onCommand)
        {
            _onCommand = onCommand;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            new Thread(Serve) { IsBackground = true }.Start();
        }

        private void Serve()
        {
            try
            {
                while (true)
                {
                    using var client = _listener.AcceptTcpClient();
                    try
                    {
                        _client = client;
                        using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
                        using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
                        lock (_gate) _writer = writer;
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            var request = JObject.Parse(line);
                            if (request.Value<string>("command") == "set_tool_catalog")
                            {
                                Send(new JObject { ["id"] = request["id"], ["success"] = true, ["data"] = new JObject() });
                                continue;
                            }
                            _onCommand(request, this);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        public void Send(JObject response)
        {
            if (!(response["_target"] is JObject))
            {
                response["_target"] = new JObject
                {
                    ["target_id"] = "revit-2024-" + FakePid,
                    ["pid"] = FakePid,
                    ["host_year"] = 2024,
                    ["document_id"] = "doc-" + FakePid
                };
            }
            lock (_gate) _writer?.WriteLine(response.ToString(Formatting.None));
        }

        public void Dispose()
        {
            _listener.Stop();
            try { _client?.Close(); } catch { }
        }
    }
}
