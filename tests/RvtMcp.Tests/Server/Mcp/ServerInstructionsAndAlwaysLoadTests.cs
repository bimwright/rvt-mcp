using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
public sealed class ServerInstructionsAndAlwaysLoadTests
{
    private static readonly string[] AlwaysLoadKeys = { "anthropic/alwaysLoad", "antigravity/alwaysLoad" };
    private static readonly string[] ExpectedAlwaysLoadTools =
    {
        "revit_health", "revit_list_available_targets", "revit_report_task_result", "revit_switch_target"
    };

    private static readonly string[] SurveyHintTools =
    {
        // Bucket A — existing-element mutators / deleters (57)
        "revit_apply_keynote_to_element",
        "revit_wipe_empty_tags",
        "revit_delete_element",
        "revit_load_family_from_path",
        "revit_rename_family_type",
        "revit_replace_family_type",
        "revit_unload_family",
        "revit_create_mesh_shapes",
        "revit_apply_filter_to_view",
        "revit_clear_element_overrides",
        "revit_override_element_graphics",
        "revit_remove_filter_from_view",
        "revit_set_category_visibility",
        "revit_set_element_phase",
        "revit_set_filter_overrides",
        "revit_set_view_phase",
        "revit_acquire_coordinates_from_link",
        "revit_publish_coordinates_to_link",
        "revit_reload_link",
        "revit_set_project_base_point",
        "revit_unload_link",
        "revit_assign_material_to_element",
        "revit_set_material_appearance",
        "revit_set_material_identity",
        "revit_set_material_structural_asset",
        "revit_set_material_thermal_asset",
        "revit_connect_mep_elements",
        "revit_create_mep_fitting",
        "revit_set_system_classification",
        "revit_purge_unused",
        "revit_set_project_info",
        "revit_assign_elements_to_workset",
        "revit_change_element_type",
        "revit_color_elements",
        "revit_move_elements",
        "revit_operate_element",
        "revit_rotate_elements",
        "revit_set_element_parameter_values",
        "revit_set_type_parameter_values",
        "revit_set_wall_top",
        "revit_apply_view_template",
        "revit_delete_saved_selection",
        "revit_delete_view_template",
        "revit_save_selection",
        "revit_set_parameter_value_by_guid",
        "revit_apply_schedule_filter_sort",
        "revit_update_schedule_field",
        "revit_align_viewports",
        "revit_assign_revision_to_sheet",
        "revit_renumber_sheets",
        "revit_set_titleblock_parameters",
        "revit_set_structural_load",
        "revit_create_camera_view",
        "revit_set_view_crop",
        "revit_set_view_orientation",
        "revit_set_view_scale",
        "revit_set_view_style",
        // Bucket B — compound / programmable write dispatchers (8)
        "revit_batch_execute",
        "revit_send_code_to_revit",
        "revit_run_baked_tool",
        "revit_workflow_clash_review",
        "revit_workflow_data_roundtrip",
        "revit_workflow_naming_normalization",
        "revit_workflow_sheet_set",
        "revit_workflow_view_cleanup"
    };

    private static string InstructionsText() =>
        (string)typeof(Program).GetField("ServerInstructionsText",
            BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;

    private static Tool[] AllProtocolTools()
    {
        var config = new RvtMcpConfig { Toolsets = new List<string> { "all" }, EnableAdaptiveBake = true };
        return Program.CreateRegisteredTools(ToolsetFilter.Resolve(config), config)
            .Select(t => t.ProtocolTool).ToArray();
    }

    // INSTR-MAX-2KB, INSTR-ORDER, INSTR-SAFETY-BLOCK (FC-SRV-06 / BW-PERM-001 R-C), INSTR-SURVEY-HINT
    [Fact]
    public void Server_instructions_satisfy_order_safety_survey_and_2kb_cap()
    {
        var text = InstructionsText();
        var bytes = Encoding.UTF8.GetByteCount(text);
        Assert.True(bytes <= 2048, $"ServerInstructionsText is {bytes} UTF-8 bytes (INSTR-MAX-2KB).");
        Assert.DoesNotContain("revit_change prompt", text);

        var iProduct = text.IndexOf(Program.Product, StringComparison.Ordinal);
        var iSession = text.IndexOf(Program.Session, StringComparison.Ordinal);
        var iReporting = text.IndexOf(Program.Reporting, StringComparison.Ordinal);
        var iToolIndex = text.IndexOf(Program.ToolIndex, StringComparison.Ordinal);
        var iSafety = text.IndexOf(Program.SafetyAndPermissions, StringComparison.Ordinal);
        Assert.True(iProduct == 0 && iProduct < iSession && iSession < iReporting
            && iReporting < iToolIndex && iToolIndex < iSafety, "INSTR-ORDER section sequence mismatch.");

        var iTargets = Program.Session.IndexOf("revit_list_available_targets", StringComparison.Ordinal);
        var iSwitch = Program.Session.IndexOf("revit_switch_target", StringComparison.Ordinal);
        var iHealth = Program.Session.IndexOf("revit_health(check_updates=true)", StringComparison.Ordinal);
        var iSurvey = Program.Session.IndexOf("revit_survey_change_impact", StringComparison.Ordinal);
        Assert.True(iTargets >= 0 && iTargets < iSwitch && iSwitch < iHealth && iHealth < iSurvey,
            "INSTR-ORDER session startup order must be list_available_targets -> switch_target -> health -> survey.");
        Assert.Contains("read the result back", Program.Session, StringComparison.OrdinalIgnoreCase);

        Assert.EndsWith(Program.SafetyAndPermissions, text, StringComparison.Ordinal);
        Assert.StartsWith("Safety & permissions:", Program.SafetyAndPermissions, StringComparison.Ordinal);
        Assert.Contains("dry_run", Program.SafetyAndPermissions, StringComparison.Ordinal);
        Assert.Contains("not created in this session", Program.SafetyAndPermissions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("revit_batch_execute", Program.SafetyAndPermissions, StringComparison.Ordinal);
        Assert.Contains("revit_send_code_to_revit", Program.SafetyAndPermissions, StringComparison.Ordinal);
        Assert.Contains("full-trust C#", Program.SafetyAndPermissions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("project folder", Program.SafetyAndPermissions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Server_instructions_index_all_toolsets_and_reference_only_real_tools()
    {
        var text = InstructionsText();
        var allTools = AllProtocolTools().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var bulletBodies = string.Join("\n", Program.ToolIndex
            .Split('\n')
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
            .Select(line =>
            {
                var colon = line.IndexOf(':');
                return colon >= 0 ? line.Substring(colon + 1) : line;
            }));
        Assert.False(string.IsNullOrWhiteSpace(bulletBodies));
        foreach (var toolset in ToolsetFilter.KnownToolsets)
        {
            Assert.Matches($@"\b{Regex.Escape(toolset)}\b", bulletBodies);
        }

        foreach (Match match in Regex.Matches(text, @"\brevit_[a-z0-9_]+\b"))
        {
            Assert.True(allTools.Contains(match.Value),
                $"ServerInstructionsText references unknown tool '{match.Value}'.");
        }

        // Verify every "<toolset> (tool1, tool2)" example in ToolIndex resolves to real tools in that exact toolset.
        var config = new RvtMcpConfig { Toolsets = new List<string> { "all" }, EnableAdaptiveBake = true };
        var verifiedGroups = 0;
        foreach (Match group in Regex.Matches(bulletBodies, @"\b([a-z0-9_-]+)\s+\(([^)]+)\)"))
        {
            var toolset = group.Groups[1].Value;
            if (!ToolsetFilter.KnownToolsets.Contains(toolset))
                continue;
            verifiedGroups++;
            var toolsetTools = Program.CreateRegisteredTools(new HashSet<string>(StringComparer.Ordinal) { toolset }, config)
                .Select(t => t.ProtocolTool.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var raw in group.Groups[2].Value.Split(','))
            {
                var fullName = "revit_" + raw.Trim();
                Assert.True(toolsetTools.Contains(fullName),
                    $"ToolIndex lists '{raw.Trim()}' under toolset '{toolset}', but '{fullName}' is not registered in '{toolset}'.");
            }
        }
        Assert.True(verifiedGroups > 0, "Expected at least one verified toolset example group in ToolIndex.");
    }

    // INSTR-MOVE-TO-TOOL: no verbatim long sentence duplicated between ServerInstructions and tool descriptions.
    [Fact]
    public void Server_instructions_do_not_duplicate_tool_description_sentences()
    {
        var sentences = Regex.Split(InstructionsText(), @"(?<=[.!?])\s+|\n+")
            .Select(s => s.Trim())
            .Where(s => s.Length >= 40)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(sentences);

        var tools = AllProtocolTools();
        var duplicates = new List<string>();
        foreach (var tool in tools)
        {
            var desc = tool.Description ?? "";
            foreach (var sentence in sentences)
            {
                if (desc.Contains(sentence, StringComparison.Ordinal))
                    duplicates.Add($"{tool.Name}: \"{sentence}\"");
            }
        }
        Assert.True(duplicates.Count == 0,
            "Verbatim ServerInstructions sentences duplicated in tool descriptions: " + string.Join("; ", duplicates));
    }

    // TOOL-SURVEY-HINT (BW-MCP-UPGRADE-001)
    [Fact]
    public void Survey_hint_is_present_on_all_65_modifying_and_dispatch_tools()
    {
        Assert.Equal(65, SurveyHintTools.Length);
        var byName = AllProtocolTools().ToDictionary(t => t.Name, StringComparer.Ordinal);

        foreach (var name in SurveyHintTools)
        {
            Assert.True(byName.TryGetValue(name, out var tool), $"Missing tool '{name}'.");
            Assert.Contains("revit_survey_change_impact", tool!.Description ?? "", StringComparison.Ordinal);
            Assert.Contains("read back", tool.Description ?? "", StringComparison.OrdinalIgnoreCase);
        }

        var actualWithSurvey = byName.Values
            .Where(t => (t.Description ?? "").Contains("revit_survey_change_impact", StringComparison.Ordinal))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        var expected = SurveyHintTools.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actualWithSurvey);
    }

    // BW-PERM-001 R-D: all 7 touched destructive tools state impact and recovery in sentence 1
    [Theory]
    [InlineData("revit_batch_execute")]
    [InlineData("revit_run_baked_tool")]
    [InlineData("revit_save_checkpoint")]
    [InlineData("revit_unload_link")]
    [InlineData("revit_reload_link")]
    [InlineData("revit_publish_coordinates_to_link")]
    [InlineData("revit_workflow_data_roundtrip")]
    public void Touched_destructive_tools_state_recovery_in_first_sentence(string toolName)
    {
        var tool = Assert.Single(AllProtocolTools(), t => t.Name == toolName);
        var desc = tool.Description ?? "";
        var sentenceEnd = desc.IndexOf(". ", StringComparison.Ordinal);
        var firstSentence = sentenceEnd >= 0 ? desc.Substring(0, sentenceEnd + 1) : desc;
        var recoveryWords = new[] { "Undo", "back up", "backup", "restore", "cannot be undone" };
        Assert.True(recoveryWords.Any(w => firstSentence.Contains(w, StringComparison.OrdinalIgnoreCase)),
            $"{toolName} first sentence lacks recovery wording (R-D): {firstSentence}");
    }

    // INSTR-MOVE-TO-TOOL
    [Fact]
    public void Moved_instruction_details_live_in_owning_tool_descriptions()
    {
        var byName = AllProtocolTools().ToDictionary(t => t.Name, StringComparer.Ordinal);
        Assert.Contains("Subagents", byName["revit_switch_target"].Description!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unknown is not current", byName["revit_health"].Description!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("update_guidance", byName["revit_health"].Description!, StringComparison.Ordinal);
        Assert.Contains("revit_get_document_state", byName["revit_save_document"].Description!, StringComparison.Ordinal);
        Assert.Contains("revit_get_document_state", byName["revit_save_checkpoint"].Description!, StringComparison.Ordinal);
        Assert.Contains("revit_get_operation_status", byName["revit_batch_execute"].Description!, StringComparison.Ordinal);
        Assert.Contains("revit_get_operation_status", byName["revit_run_baked_tool"].Description!, StringComparison.Ordinal);
    }

    // TOOL-ALWAYSLOAD + INSTR-MAX-2KB + wire UTF-8 caps over JSON-RPC stream transport
    [Theory]
    [InlineData("", 4)]
    [InlineData("--toolsets all", 4)]
    [InlineData("--read-only", 3)]
    [InlineData("--toolsets all --read-only", 3)]
    [InlineData("--disable-send-code", 4)]
    [InlineData("--toolsets all --disable-toolbaker", 4)]
    [InlineData("--toolsets all --enable-adaptive-bake", 4)]
    [InlineData("--toolsets query", 0)]
    [InlineData("--toolsets meta", 4)]
    [InlineData("--toolsets modify", 0)]
    public async Task Wire_stream_exposes_instructions_caps_and_alwaysLoad_across_cli_modes(
        string cliLine, int expectedAlwaysLoadCount)
    {
        var args = string.IsNullOrWhiteSpace(cliLine)
            ? Array.Empty<string>()
            : cliLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var root = Path.Combine(Path.GetTempPath(), "rvt-phase1-stream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var originalDir = ServerAuthToken.DiscoveryDirOverride;
        var originalConfig = ServerState.Config;
        var pipeName = "rvt-phase1-stream-" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var input = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var output = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(input.WaitForConnectionAsync(timeout.Token), output.ConnectAsync(timeout.Token));
        try
        {
            ServerAuthToken.DiscoveryDirOverride = root;
            var config = RvtMcpConfig.Load(args, configFilePath: Path.Combine(root, "missing.json"), envLookup: _ => null);
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
                var init = await RequestAsync(writer, reader, 1, "initialize", new
                {
                    protocolVersion = "2025-11-25",
                    capabilities = new { },
                    clientInfo = new { name = "phase1-contract-test", version = "1" }
                });
                await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");

                var instructions = init["result"]!.Value<string>("instructions")!;
                Assert.Equal(InstructionsText(), instructions);
                Assert.True(Encoding.UTF8.GetByteCount(instructions) <= 2048);
                Assert.EndsWith(Program.SafetyAndPermissions, instructions, StringComparison.Ordinal);

                var catalog = await RequestAsync(writer, reader, 2, "tools/list", new { });
                var tools = (JArray)catalog["result"]!["tools"]!;
                Assert.NotEmpty(tools);
                var registered = tools.Select(t => t.Value<string>("name")!).ToHashSet(StringComparer.Ordinal);
                Assert.Contains("Use only registered tools", instructions, StringComparison.Ordinal);
                if (!registered.Contains("revit_switch_target"))
                    Assert.Contains("if absent, start reads with --target pid:<n>", instructions, StringComparison.Ordinal);
                if (!registered.Contains("revit_health"))
                    Assert.Contains("if available, call revit_health(check_updates=true)", instructions, StringComparison.Ordinal);
                if (!registered.Contains("revit_report_task_result"))
                    Assert.Contains("If available, call revit_report_task_result", instructions, StringComparison.Ordinal);
                if (!registered.Contains("revit_get_operation_status"))
                    Assert.Contains("before retrying timed-out writes; if absent, don't retry", instructions, StringComparison.Ordinal);
                if (!registered.Contains("revit_survey_change_impact") || !registered.Contains("revit_switch_target"))
                    Assert.Contains("Enable meta to verify or query to survey if missing", instructions, StringComparison.Ordinal);

                var alwaysLoadedByKey = AlwaysLoadKeys.ToDictionary(k => k, _ => new List<string>(), StringComparer.Ordinal);
                foreach (JObject tool in tools)
                {
                    var name = tool.Value<string>("name")!;
                    var desc = tool.Value<string>("description") ?? "";
                    Assert.True(desc.Length <= 4096 && Encoding.UTF8.GetByteCount(desc) <= 4096,
                        $"Wire description of '{name}' exceeds 4096 chars/UTF-8 bytes.");

                    if (tool["inputSchema"]?["properties"] is JObject props)
                    {
                        foreach (var prop in props.Properties())
                        {
                            var pdesc = prop.Value["description"]?.Value<string>();
                            if (pdesc != null)
                                Assert.True(Encoding.UTF8.GetByteCount(pdesc) <= 2048,
                                    $"Wire param description '{name}.{prop.Name}' exceeds 2048 UTF-8 bytes.");
                        }
                    }

                    var meta = tool["_meta"] as JObject;
                    foreach (var key in AlwaysLoadKeys)
                    {
                        if (meta?[key] is { Type: JTokenType.Boolean } flag && flag.Value<bool>())
                            alwaysLoadedByKey[key].Add(name);
                    }

                    if (name == "revit_health")
                    {
                        Assert.NotNull(meta);
                        foreach (var key in AlwaysLoadKeys)
                        {
                            Assert.Equal(JTokenType.Boolean, meta![key]?.Type);
                            Assert.True(meta[key]?.Value<bool>() == true);
                        }
                        Assert.NotNull(meta![HealthRuntimeInfo.ClientRuntimeMetadataKey]);
                    }
                    else if (name == "revit_send_code_to_revit")
                    {
                        Assert.Null(tool["annotations"]);
                        Assert.Null(meta);
                    }
                    else if (name == "revit_get_report_history")
                    {
                        foreach (var key in AlwaysLoadKeys)
                            Assert.Null(meta?[key]);
                    }
                }

                var expected = ExpectedAlwaysLoadTools
                    .Where(registered.Contains)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToArray();
                Assert.Equal(expectedAlwaysLoadCount, expected.Length);
                foreach (var key in AlwaysLoadKeys)
                    Assert.Equal(expected, alwaysLoadedByKey[key].OrderBy(n => n, StringComparer.Ordinal).ToArray());
            }
            finally { await host.StopAsync(timeout.Token); }
        }
        finally
        {
            ServerAuthToken.DiscoveryDirOverride = originalDir;
            ServerState.Config = originalConfig;
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    // TOOL-ALWAYSLOAD + INSTR-MAX-2KB + wire caps over HTTP transport parity (writable and --read-only)
    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 3)]
    public async Task Wire_http_transport_matches_instructions_and_alwaysLoad_surface(
        bool readOnly, int expectedAlwaysLoadCount)
    {
        var root = Path.Combine(Path.GetTempPath(), "rvt-phase1-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var originalConfig = ServerState.Config;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var config = new RvtMcpConfig { Toolsets = new List<string> { "meta" }, ReadOnly = readOnly };
            ServerState.Config = config;
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            var mcp = builder.Services.AddMcpServer(Program.ConfigureMcpServerOptions).WithHttpTransport(Program.ConfigureHttpTransport);
            mcp = RuntimeToolFilter.Register(mcp, config, new SessionContext(false, root));
            Program.RegisterToolsets(mcp, ToolsetFilter.Resolve(config), config);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var app = builder.Build();
            app.MapMcp();
            await app.StartAsync(deadline.Token);

            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            await using var client = await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(address) }),
                cancellationToken: deadline.Token);

            Assert.Equal(InstructionsText(), client.ServerInstructions);
            Assert.True(Encoding.UTF8.GetByteCount(client.ServerInstructions!) <= 2048);

            var tools = await client.ListToolsAsync(cancellationToken: deadline.Token);
            foreach (var tool in tools)
            {
                var desc = tool.ProtocolTool.Description ?? "";
                Assert.True(desc.Length <= 4096 && Encoding.UTF8.GetByteCount(desc) <= 4096,
                    $"HTTP wire description of '{tool.Name}' exceeds 4096 chars/UTF-8 bytes.");
                if (tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var props)
                    && props.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in props.EnumerateObject())
                    {
                        if (prop.Value.TryGetProperty("description", out var pd) && pd.ValueKind == JsonValueKind.String)
                            Assert.True(Encoding.UTF8.GetByteCount(pd.GetString() ?? "") <= 2048,
                                $"HTTP wire param '{tool.Name}.{prop.Name}' exceeds 2048 UTF-8 bytes.");
                    }
                }
            }

            var registered = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            var expected = ExpectedAlwaysLoadTools
                .Where(registered.Contains)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(expectedAlwaysLoadCount, expected.Length);
            foreach (var key in AlwaysLoadKeys)
            {
                var alwaysLoaded = tools
                    .Where(t => t.ProtocolTool.Meta?[key]?.GetValue<bool>() == true)
                    .Select(t => t.Name)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToArray();
                Assert.Equal(expected, alwaysLoaded);
            }

            var health = Assert.Single(tools, t => t.Name == "revit_health").ProtocolTool;
            Assert.NotNull(health.Meta?[HealthRuntimeInfo.ClientRuntimeMetadataKey]);

            if (!readOnly)
            {
                var sendCode = Assert.Single(tools, t => t.Name == "revit_send_code_to_revit").ProtocolTool;
                Assert.Null(sendCode.Annotations);
                Assert.Null(sendCode.Meta);
            }
            else
            {
                Assert.DoesNotContain("revit_switch_target", registered);
                Assert.DoesNotContain("revit_send_code_to_revit", registered);
            }

            await client.DisposeAsync();
            await app.StopAsync(deadline.Token);
        }
        finally
        {
            ServerState.Config = originalConfig;
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static async Task<JObject> RequestAsync(StreamWriter writer, StreamReader reader, int id, string method, object parameters)
    {
        await writer.WriteLineAsync(JsonConvert.SerializeObject(new { jsonrpc = "2.0", id, method, @params = parameters }));
        while (true)
        {
            var reply = JObject.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!);
            if (reply.Value<int?>("id") != id) continue;
            Assert.Null(reply["error"]);
            return reply;
        }
    }
}
