#nullable enable
#pragma warning disable MCPEXP005
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Skills;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server;
using RvtMcp.Server.Memory;
using RvtMcp.Server.Prompts;
using Xunit;
using ServerAuthToken = RvtMcp.Server.AuthToken;

namespace RvtMcp.Tests;

[Collection("ServerStateConfig")]
public sealed class McpSkillsAndWorkflowProtocolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rvt-skills-test-" + Guid.NewGuid().ToString("N"));
    private readonly RvtMcpConfig? _oldConfig = ServerState.Config;
    private readonly RvtMcp.ToolCatalog.ToolCatalog? _oldCatalog = ServerState.ToolCatalog;
    private readonly SessionContext? _oldSession = ToolGateway.Session;
    private readonly string? _oldDiscovery = ServerAuthToken.DiscoveryDirOverride;
    private readonly string? _oldDiagnosticRoot = DiagnosticLog.LocalAppDataOverride;
    private readonly string? _oldClientName = ToolGateway.ClientName;

    public void Dispose()
    {
        DiagnosticLog.Flush();
        DiagnosticLog.LocalAppDataOverride = _oldDiagnosticRoot;
        ServerAuthToken.DiscoveryDirOverride = _oldDiscovery;
        ServerState.Config = _oldConfig;
        ServerState.ToolCatalog = _oldCatalog;
        ToolGateway.Session = _oldSession;
        ToolGateway.ClientName = _oldClientName;
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }
    }

    // SKILL-REVIT-CHANGE, SKILL-BUNDLED, SKILL-FROM-PROMPTS: all 6 workflows served as verified Skills led by revit-change.
    [Fact]
    public async Task McpClient_lists_gets_reads_and_verifies_all_6_skills_with_revit_change_first()
    {
        var config = new RvtMcpConfig { Toolsets = new List<string> { "all" } };
        await WithStdioClientAsync(config, async (client, ct) =>
        {
            Assert.True(client.ServerCapabilities.SupportsSkills());

            var listed = await client.ListSkillsAsync(cancellationToken: ct);
            Assert.True(listed.Count <= 100);
            Assert.Equal(6, listed.Count);
            Assert.Equal(WorkflowCatalog.All.Select(w => w.SkillUri).ToArray(), listed.Select(s => s.Uri).ToArray());
            Assert.Equal("skill://revit-change/SKILL.md", listed[0].Uri);

            foreach (var entry in listed)
            {
                var def = WorkflowCatalog.All.Single(w => w.SkillUri == entry.Uri);
                var fetched = await client.GetSkillAsync(entry.Uri, cancellationToken: ct);
                Assert.Equal(entry.Uri, fetched.Uri);
                Assert.Equal(def.SkillName, fetched.Frontmatter["name"]?.GetValue<string>());
                Assert.Equal(def.Description, fetched.Frontmatter["description"]?.GetValue<string>());

                var files = Assert.IsAssignableFrom<IReadOnlyList<SkillResource>>(fetched.Resources.Entries);
                var skillFile = Assert.Single(files);
                Assert.Equal(fetched.Uri, skillFile.Uri);
                Assert.StartsWith("sha256:", skillFile.Digest, StringComparison.Ordinal);
                Assert.True(skillFile.Size <= 100 * 1024);
                Assert.True(files.Sum(f => f.Size) <= 400 * 1024);

                var read = await client.ReadSkillResourceAsync(fetched, fetched.Uri, cancellationToken: ct);
                var text = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents)).Text;
                Assert.Equal(Encoding.UTF8.GetByteCount(text), skillFile.Size);
                Assert.False(text.Contains('\r'));

                var (fmName, fmDesc, skillBody) = ParseMarkdownSkill(text);
                Assert.Equal(def.SkillName, fmName);
                Assert.Equal(def.Description, fmDesc);
                Assert.DoesNotContain("{mode}", skillBody);
                Assert.DoesNotContain("{change}", skillBody);
                Assert.DoesNotContain("{scope}", skillBody);
                Assert.DoesNotContain("{request}", skillBody);

                var toolCall = await client.CallToolAsync(
                    "revit_get_workflow",
                    new Dictionary<string, object?> { ["name"] = def.Id },
                    cancellationToken: ct);
                Assert.NotEqual(true, toolCall.IsError);
                var toolBody = ((TextContentBlock)toolCall.Content[0]).Text;
                Assert.Equal(toolBody, skillBody);
            }

            // Existing resource surface continues working alongside Skills resources.
            var sessionResource = await client.ReadResourceAsync("revit://session/context", cancellationToken: ct);
            var sessionText = Assert.IsType<TextResourceContents>(Assert.Single(sessionResource.Contents)).Text;
            Assert.Contains("session", sessionText, StringComparison.OrdinalIgnoreCase);
        });
    }

    // SKILL-WORKFLOW-TOOL, SKILL-FROM-PROMPTS, SKILL-FALLBACK, SKILL-ROLES, FC-ERR-01
    [Fact]
    public void Workflow_tool_lists_catalog_supports_aliases_and_rejects_unknown_names()
    {
        Directory.CreateDirectory(_root);
        DiagnosticLog.Flush();
        DiagnosticLog.LocalAppDataOverride = _root;
        ServerState.Config = new RvtMcpConfig { Toolsets = new List<string> { "all" } };

        var listed = JObject.Parse(WorkflowGuideTools.GetWorkflow());
        var workflows = Assert.IsType<JArray>(listed["workflows"]);
        Assert.Equal(6, workflows.Count);
        Assert.Equal("change", workflows[0].Value<string>("name"));
        Assert.Equal("skill://revit-change/SKILL.md", workflows[0].Value<string>("skill"));
        Assert.True(workflows.All(w => w.Value<bool>("available")));

        // Prompt descriptions stay identical to WorkflowCatalog (single shared source).
        var promptDescriptions = typeof(RevitPrompts)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => (
                Name: m.GetCustomAttribute<ModelContextProtocol.Server.McpServerPromptAttribute>()?.Name,
                Description: m.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description))
            .Where(x => x.Name != null)
            .ToDictionary(x => x.Name!, x => x.Description, StringComparer.Ordinal);
        foreach (var def in WorkflowCatalog.All)
            Assert.Equal(def.Description, promptDescriptions[def.PromptName]);

        var byId = WorkflowGuideTools.GetWorkflow("change");
        Assert.Contains("# Disciplined model change", byId);
        Assert.Contains("revit_survey_change_impact", byId);
        Assert.Equal(byId, WorkflowGuideTools.GetWorkflow("revit_change"));
        Assert.Equal(byId, WorkflowGuideTools.GetWorkflow("revit-change"));
        Assert.Equal(byId, WorkflowGuideTools.GetWorkflow("skill://revit-change/SKILL.md"));

        var unknown = JObject.Parse(WorkflowGuideTools.GetWorkflow("not_a_workflow"));
        Assert.False(unknown.Value<bool>("ok"));
        Assert.StartsWith("INVALID_ARGUMENT:", unknown.Value<string>("error"), StringComparison.Ordinal);
        Assert.Equal(6, ((JArray)unknown["available_workflows"]!).Count);
    }

    // SKILL-WORKFLOW-TOOL + SKILL-BUNDLED under default toolsets and --read-only
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Skills_and_workflow_tool_match_PromptBody_gates_across_toolsets_and_read_only(bool allToolsets, bool readOnly)
    {
        var config = new RvtMcpConfig
        {
            Toolsets = allToolsets ? new List<string> { "all" } : null,
            ReadOnly = readOnly
        };

        await WithStdioClientAsync(config, async (client, ct) =>
        {
            var listResult = await client.CallToolAsync("revit_get_workflow", cancellationToken: ct);
            Assert.NotEqual(true, listResult.IsError);
            var listJson = JObject.Parse(((TextContentBlock)listResult.Content[0]).Text);
            var availMap = listJson["workflows"]!.ToDictionary(w => w.Value<string>("name")!, w => w.Value<bool>("available"));

            Assert.True(availMap["change"]);
            Assert.True(availMap["getting_started"]);
            Assert.Equal(!readOnly, availMap["stairs"]);
            Assert.Equal(allToolsets && !readOnly, availMap["model_audit"]);
            Assert.Equal(allToolsets, availMap["pre_issue_check"]);
            Assert.Equal(allToolsets, availMap["drawing_layout"]);

            foreach (var def in WorkflowCatalog.All)
            {
                var skill = await client.GetSkillAsync(def.SkillUri, cancellationToken: ct);
                var read = await client.ReadSkillResourceAsync(skill, def.SkillUri, cancellationToken: ct);
                var skillText = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents)).Text;
                var (_, _, skillBody) = ParseMarkdownSkill(skillText);

                var call = await client.CallToolAsync(
                    "revit_get_workflow",
                    new Dictionary<string, object?> { ["name"] = def.Id },
                    cancellationToken: ct);
                var toolBody = ((TextContentBlock)call.Content[0]).Text;
                Assert.Equal(toolBody, skillBody);

                if (def.Id == "change" && readOnly)
                    Assert.Contains("Session: READ-ONLY:", toolBody);
                if (def.Id == "stairs" && readOnly)
                    Assert.Contains("--read-only", toolBody);
                if (def.Id == "model_audit" && !allToolsets)
                    Assert.Contains("--toolsets", toolBody);
                if (def.Id == "model_audit" && allToolsets && readOnly)
                    Assert.Contains("not exposed: revit_purge_unused", toolBody);
            }
        });
    }

    // SKILL-FALLBACK on pre-2026-07-28 client (`2025-11-25`)
    [Fact]
    public async Task Legacy_2025_11_25_client_receives_workflow_hint_and_can_call_revit_get_workflow()
    {
        Directory.CreateDirectory(_root);
        DiagnosticLog.LocalAppDataOverride = _root;
        ServerAuthToken.DiscoveryDirOverride = _root;
        var config = new RvtMcpConfig { Toolsets = new List<string> { "all" } };
        ServerState.Config = config;
        ServerState.ToolCatalog = ServerToolCatalogBuilder.Build(ToolsetFilter.Resolve(config), config);
        ToolGateway.Session = new SessionContext(false, _root);

        var pipeName = "rvt-skills-legacy-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var serverPipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var clientPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(serverPipe.WaitForConnectionAsync(cts.Token), clientPipe.ConnectAsync(cts.Token));

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        var mcp = builder.Services
            .AddMcpServer(Program.ConfigureMcpServerOptions)
            .WithStreamServerTransport(serverPipe, serverPipe);
        RegisterSurface(mcp, config);

        using var host = builder.Build();
        await host.StartAsync(cts.Token);
        using var reader = new StreamReader(clientPipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(clientPipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        try
        {
            var init = await SendRawAsync(writer, reader, 1, "initialize", new
            {
                protocolVersion = "2025-11-25",
                capabilities = new { },
                clientInfo = new { name = "legacy-skills-fallback-client", version = "1.0" }
            }, cts.Token);
            Assert.Equal("2025-11-25", init["result"]!.Value<string>("protocolVersion"));
            var instructions = init["result"]!.Value<string>("instructions")!;
            Assert.Contains("revit_get_workflow", instructions);
            Assert.True(Encoding.UTF8.GetByteCount(instructions) <= 2048);

            await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");

            var call = await SendRawAsync(writer, reader, 2, "tools/call", new
            {
                name = "revit_get_workflow",
                arguments = new { name = "change" }
            }, cts.Token);
            var text = call["result"]!["content"]![0]!.Value<string>("text")!;
            Assert.Contains("# Disciplined model change", text);
            Assert.Contains("revit_survey_change_impact", text);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    // Stateless HTTP (--http) parity for MCP Skills + revit_get_workflow
    [Fact]
    public async Task Http_stateless_transport_exposes_skills_and_revit_get_workflow()
    {
        Directory.CreateDirectory(_root);
        DiagnosticLog.LocalAppDataOverride = _root;
        ServerAuthToken.DiscoveryDirOverride = _root;
        var config = new RvtMcpConfig { Toolsets = new List<string> { "query", "meta" } };
        ServerState.Config = config;
        ServerState.ToolCatalog = ServerToolCatalogBuilder.Build(ToolsetFilter.Resolve(config), config);
        ToolGateway.Session = new SessionContext(false, _root);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var app = Program.BuildHttpApp(config, "http://127.0.0.1:0", registerMaintenance: false);
        await app.StartAsync(cts.Token);
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            await using var client = await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(address) }),
                new McpClientOptions { ClientInfo = new Implementation { Name = "http-skills-client", Version = "1.0" } },
                cancellationToken: cts.Token);

            Assert.True(client.ServerCapabilities.SupportsSkills());
            var listed = await client.ListSkillsAsync(cancellationToken: cts.Token);
            Assert.Equal(6, listed.Count);

            var changeSkill = await client.GetSkillAsync("skill://revit-change/SKILL.md", cancellationToken: cts.Token);
            var read = await client.ReadSkillResourceAsync(changeSkill, changeSkill.Uri, cancellationToken: cts.Token);
            var skillText = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents)).Text;
            var (_, _, skillBody) = ParseMarkdownSkill(skillText);

            var toolResult = await client.CallToolAsync(
                "revit_get_workflow",
                new Dictionary<string, object?> { ["name"] = "change" },
                cancellationToken: cts.Token);
            var toolBody = ((TextContentBlock)toolResult.Content[0]).Text;
            Assert.Equal(toolBody, skillBody);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    // SKILL-PROMPT-LOG: logs skills/get, skills/read, and revit_get_workflow without leaking secrets or touching real LOCALAPPDATA.
    [Fact]
    public async Task Workflow_and_skill_logging_uses_override_root_and_redacts_unknowns()
    {
        var config = new RvtMcpConfig { Toolsets = new List<string> { "all" } };
        await WithStdioClientAsync(config, async (client, ct) =>
        {
            await client.ListSkillsAsync(cancellationToken: ct);
            var change = await client.GetSkillAsync("skill://revit-change/SKILL.md", cancellationToken: ct);
            await client.ReadSkillResourceAsync(change, change.Uri, cancellationToken: ct);
            await client.ReadResourceAsync("revit://session/context", cancellationToken: ct);
            await Assert.ThrowsAnyAsync<Exception>(() =>
                client.GetSkillAsync("skill://DO_NOT_LOG_SENTINEL/SKILL.md", cancellationToken: ct).AsTask());

            await client.CallToolAsync("revit_get_workflow", cancellationToken: ct);
            await client.CallToolAsync("revit_get_workflow",
                new Dictionary<string, object?> { ["name"] = "change" }, cancellationToken: ct);
            var unknownWire = await client.CallToolAsync("revit_get_workflow",
                new Dictionary<string, object?> { ["name"] = "DO_NOT_LOG_SENTINEL" }, cancellationToken: ct);
            Assert.True(unknownWire.IsError);
            Assert.Contains("INVALID_ARGUMENT:", ((TextContentBlock)unknownWire.Content[0]).Text, StringComparison.Ordinal);

            Assert.True(DiagnosticLog.Flush());
            var logPath = Path.Combine(_root, "Bimwright", "rvt-mcp", "rvt-mcp.log");
            Assert.True(File.Exists(logPath));
            var log = File.ReadAllText(logPath);

            Assert.DoesNotContain("DO_NOT_LOG_SENTINEL", log);
            Assert.DoesNotContain("revit://session/context", log);
            Assert.Contains("[McpSession] session/capabilities transport=stdio protocol=2026-07-28 client=stdio-skills-client", log);
            Assert.Contains("[Skills] skills/list count=6 ", log);
            Assert.Contains("[Skills] skills/get uri=skill://revit-change/SKILL.md", log);
            Assert.Contains("[Skills] skills/get uri=(unknown)", log);
            Assert.Contains("[Skills] skills/read uri=skill://revit-change/SKILL.md", log);
            Assert.Contains("[Workflows] tools/revit_get_workflow name=(list)", log);
            Assert.Contains("[Workflows] tools/revit_get_workflow name=change", log);
            Assert.Contains("[Workflows] tools/revit_get_workflow name=(unknown)", log);
            Assert.Contains("client=stdio-skills-client", log);
        });
    }

    [Fact]
    public void Workflow_tool_is_server_local_in_catalog_and_read_only_in_policy()
    {
        var config = new RvtMcpConfig { Toolsets = new List<string> { "meta" } };
        var catalog = ServerToolCatalogBuilder.Build(ToolsetFilter.Resolve(config), config);
        var entry = Assert.Single(catalog.Tools, e => e.McpName == "revit_get_workflow");
        Assert.Equal("meta", entry.Toolset);
        Assert.Equal("server_local", entry.Timeout.PolicyKind);
        Assert.True(ToolReadPolicy.IsReadOnly("get_workflow"));
    }

    private async Task WithStdioClientAsync(RvtMcpConfig config, Func<McpClient, CancellationToken, Task> assertion)
    {
        Directory.CreateDirectory(_root);
        DiagnosticLog.Flush();
        DiagnosticLog.LocalAppDataOverride = _root;
        ServerAuthToken.DiscoveryDirOverride = _root;
        ServerState.Config = config;
        ServerState.ToolCatalog = ServerToolCatalogBuilder.Build(ToolsetFilter.Resolve(config), config);
        ToolGateway.Session = new SessionContext(false, _root);

        var pipeName = "rvt-skills-stdio-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var serverPipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var clientPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(serverPipe.WaitForConnectionAsync(cts.Token), clientPipe.ConnectAsync(cts.Token));

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        var mcp = builder.Services
            .AddMcpServer(Program.ConfigureMcpServerOptions)
            .WithStreamServerTransport(serverPipe, serverPipe);
        RegisterSurface(mcp, config);

        using var host = builder.Build();
        await host.StartAsync(cts.Token);
        try
        {
            await using var client = await McpClient.CreateAsync(
                new StreamClientTransport(clientPipe, clientPipe),
                new McpClientOptions { ClientInfo = new Implementation { Name = "stdio-skills-client", Version = "1.0" } },
                cancellationToken: cts.Token);
            await assertion(client, cts.Token);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    private void RegisterSurface(IMcpServerBuilder mcp, RvtMcpConfig config)
    {
        var method = typeof(Program).GetMethod("RegisterMcpSurface", BindingFlags.NonPublic | BindingFlags.Static)!;
        method.Invoke(null, new object[] { mcp, ToolsetFilter.Resolve(config), config });
    }

    private static (string Name, string Description, string Body) ParseMarkdownSkill(string markdown)
    {
        const string prefix = "---\n";
        Assert.StartsWith(prefix, markdown, StringComparison.Ordinal);
        var close = markdown.IndexOf("\n---\n", prefix.Length, StringComparison.Ordinal);
        Assert.True(close > prefix.Length, "Missing YAML frontmatter closing delimiter.");
        var yaml = markdown.Substring(prefix.Length, close - prefix.Length);
        string? name = null;
        string? description = null;
        foreach (var line in yaml.Split('\n'))
        {
            if (line.StartsWith("name:", StringComparison.Ordinal))
                name = line.Substring("name:".Length).Trim();
            else if (line.StartsWith("description:", StringComparison.Ordinal))
            {
                var raw = line.Substring("description:".Length).Trim();
                description = raw.StartsWith("\"", StringComparison.Ordinal)
                    ? JsonConvert.DeserializeObject<string>(raw)
                    : raw;
            }
        }
        Assert.False(string.IsNullOrEmpty(name));
        Assert.False(string.IsNullOrEmpty(description));
        var bodyStart = close + "\n---\n".Length;
        while (bodyStart < markdown.Length && markdown[bodyStart] == '\n') bodyStart++;
        return (name!, description!, markdown.Substring(bodyStart));
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
#pragma warning restore MCPEXP005
