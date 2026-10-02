using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Plugin.Views.Toast;
using RvtMcp.Server;
using RvtMcp.Server.Memory;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("ServerStateConfig")]
    public class RuntimeControlsTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rvt-runtime-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            McpLogger.LocalAppDataOverride = null;
            SendCodeJournal.LocalAppDataOverride = null;
            McpLogger.Initialize();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [Fact]
        public void Defaults_and_cli_env_json_precedence_apply_to_runtime_options()
        {
            var config = new RvtMcpConfig();
            Assert.True(config.EnableSendCodeOrDefault);
            Assert.False(config.EnableCallLogOrDefault);
            Assert.True(config.EnableResponseGuardOrDefault);
            Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, "config.json");
            File.WriteAllText(path, "{\"enableSendCode\":false,\"enableCallLog\":true,\"enableResponseGuard\":false}");
            config = RvtMcpConfig.Load(new[] { "--disable-send-code", "--disable-call-log", "--enable-response-guard" },
                path, name => name == RvtMcpConfig.EnvEnableSendCode ? "1" : null);
            Assert.False(config.EnableSendCodeOrDefault);
            Assert.False(config.EnableCallLogOrDefault);
            Assert.True(config.EnableResponseGuardOrDefault);
            RvtMcpConfig.ApplyCliArgs(config, new[] { "--enable-send-code", "--enable-call-log", "--disable-response-guard", "--read-only" });
            Assert.False(config.EnableSendCodeOrDefault);
            Assert.True(config.EnableCallLogOrDefault);
            Assert.False(config.EnableResponseGuardOrDefault);
        }

        [Theory]
        [InlineData("--response-budget-bytes", "no")]
        [InlineData("--max-response-bytes", "0")]
        [InlineData("--response-warn-bytes", "-1")]
        public void Invalid_byte_flags_are_rejected(string flag, string value)
            => Assert.Throws<ArgumentException>(() => RvtMcpConfig.ApplyCliArgs(new RvtMcpConfig(), new[] { flag, value }));

        [Fact]
        public void Runtime_options_override_plugin_settings_without_persisting_or_changing_body_retention()
        {
            var plugin = new RvtMcpConfig { EnableCallLog = true, PersistSendCodeBodies = true };
            var server = new RvtMcpConfig { EnableCallLog = false, EnableSendCode = false, ReadOnly = true };
            var wire = JObject.FromObject(server.ToRuntimeOptions()).ToObject<RvtMcpConfig>();
            var resolved = plugin.WithRuntimeOptions(wire);
            Assert.False(resolved.EnableCallLogOrDefault);
            Assert.False(resolved.EnableSendCodeOrDefault);
            Assert.True(resolved.PersistSendCodeBodies);
            Assert.True(plugin.EnableCallLogOrDefault);
            Assert.Contains("READ_ONLY", ToolReadPolicy.Rejection("create_level", resolved.ReadOnlyOrDefault, resolved.EnableSendCodeOrDefault));
            Assert.Null(ToolReadPolicy.Rejection("list_schedules", resolved.ReadOnlyOrDefault, resolved.EnableSendCodeOrDefault));
            Assert.Contains("SEND_CODE_DISABLED", ToolReadPolicy.Rejection("send_code_to_revit", resolved.ReadOnlyOrDefault, resolved.EnableSendCodeOrDefault));
        }

        [Fact]
        public void Call_log_off_creates_no_files_and_does_not_append_existing_logs()
        {
            McpLogger.LocalAppDataOverride = _root;
            SendCodeJournal.LocalAppDataOverride = _root;
            McpLogger.Initialize();
            McpLogger.Log("get_current_view_info", "{}", true, 1, resultJson: "{}");
            var journal = new JournalLogger(journalDir: Path.Combine(_root, "journal"));
            journal.Log(JournalEntry.Create("query", "{}", true, 1));
            Assert.Empty(journal.ListDates());
            Assert.False(Directory.Exists(_root));
            Assert.False(SendCodeJournal.TryAppend(new RvtMcpConfig
            {
                PersistSendCodeBodies = true, PersistSendCodeBodiesUntil = DateTimeOffset.UtcNow.AddHours(1).ToString("o")
            }, "test", "return 1;", true, 1, null, "{}"));
            Assert.False(Directory.Exists(_root));
            McpLogger.Log("get_current_view_info", "{}", true, 1, enabled: true);
            var log = McpLogger.CurrentLogPath;
            var before = File.ReadAllBytes(log);
            McpLogger.Log("get_current_view_info", "{\"secret\":1}", true, 1, enabled: false);
            Assert.Equal(before, File.ReadAllBytes(log));
        }

        [Fact]
        public void Call_log_on_records_server_only_calls_and_redacts_code()
        {
            var session = new SessionContext(true, Path.Combine(_root, "journal"));
            RuntimeToolFilter.Apply(new CallToolRequestParams { Name = "revit_get_current_target" },
                Text("{\"target\":\"auto\"}"), new RvtMcpConfig { EnableCallLog = true }, session);
            Assert.Single(session.Journal.ListDates());
            Assert.Single(session.Journal.ReadDay(session.Journal.ListDates()[0]));
            session.RecordCall("send_code_to_revit", "{\"code\":\"return 123456789;\"}", true, 1);
            Assert.DoesNotContain("return 123456789", File.ReadAllText(Directory.GetFiles(Path.Combine(_root, "journal"))[0]));
        }

        [Fact]
        public void All_tools_have_explicit_hints_except_send_code_and_read_only_registration_matches_them()
        {
            var full = new RvtMcpConfig { Toolsets = new List<string> { "all" }, EnableAdaptiveBake = true };
            var methods = Program.ResolveRegisteredToolMethods(ToolsetFilter.Resolve(full), full).ToArray();
            Assert.Equal(234, methods.Length);
            foreach (var method in methods)
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                var names = method.CustomAttributes.Single(a => a.AttributeType == typeof(McpServerToolAttribute))
                    .NamedArguments.Select(a => a.MemberName).ToArray();
                var tool = McpServerTool.Create(method, (object)null).ProtocolTool;
                if (attr.Name == "revit_send_code_to_revit")
                {
                    Assert.Null(tool.Annotations);
                    Assert.DoesNotContain("ReadOnly", names);
                }
                else
                {
                    foreach (var hint in new[] { "ReadOnly", "Destructive", "Idempotent", "OpenWorld" }) Assert.Contains(hint, names);
                    Assert.NotNull(tool.Annotations);
                    var protocol = JObject.Parse(System.Text.Json.JsonSerializer.Serialize(tool, ModelContextProtocol.McpJsonUtilities.DefaultOptions));
                    foreach (var hint in new[] { "readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint" })
                        Assert.NotNull(protocol["annotations"]?[hint]);
                    Assert.Equal(attr.ReadOnly ? ToolActivityKind.Read : ToolActivityKind.Write,
                        ToolActivityClassifier.Classify(attr.Name.Substring(6)));
                    Assert.False(attr.OpenWorld);
                }
            }
            var expected = methods.Where(m => m.GetCustomAttribute<McpServerToolAttribute>().ReadOnly)
                .Select(m => m.GetCustomAttribute<McpServerToolAttribute>().Name).OrderBy(n => n).ToArray();
            full.ReadOnly = true;
            var actual = Program.ResolveRegisteredToolMethods(ToolsetFilter.Resolve(full), full)
                .Select(m => m.GetCustomAttribute<McpServerToolAttribute>().Name).OrderBy(n => n).ToArray();
            Assert.Equal(expected, actual);
            Assert.Equal(expected.Select(n => n.Substring(6)).OrderBy(n => n), ToolReadPolicy.ReadOnlyCommands.OrderBy(n => n));
            Assert.Contains("revit_list_schedules", actual);
            Assert.DoesNotContain("revit_capture_view_image", actual);
            Assert.DoesNotContain("revit_export_room_data", actual);
        }

        [Theory]
        [InlineData("revit_get_element_details", false)]
        [InlineData("revit_create_level", true)]
        public void Oversized_read_is_rejected_but_completed_write_is_compacted(string name, bool mutation)
        {
            var result = RuntimeToolFilter.Apply(new CallToolRequestParams { Name = name },
                Text(new JObject { ["count"] = 5, ["dry_run"] = false, ["detail"] = new string('界', 12000) }.ToString()), SmallLimits());
            Assert.Equal(!mutation, result.IsError == true);
            var text = ((TextContentBlock)result.Content[0]).Text;
            if (mutation) Assert.True(JObject.Parse(text).Value<bool>("mutation_applied"));
            else Assert.Contains("RESPONSE_TOO_LARGE", text);
            Assert.True(Encoding.UTF8.GetByteCount(RuntimeToolFilter.Serialize(result)) <= 8192);
        }

        [Theory]
        [InlineData("revit_send_code_to_revit")]
        [InlineData("revit_run_baked_tool")]
        public void Oversized_arbitrary_output_spills_once_with_unknown_mutation_outcome(string name)
        {
            var payload = new string('x', 30000);
            var result = RuntimeToolFilter.Apply(new CallToolRequestParams { Name = name },
                Text(new JObject { ["result"] = payload }.ToString()), SmallLimits(),
                writer: new ResponseSpillWriter(Path.Combine(_root, "spill")));
            var data = JObject.Parse(((TextContentBlock)result.Content[0]).Text);
            Assert.NotEqual(true, result.IsError);
            Assert.Equal(JTokenType.Null, data["mutation_applied"].Type);
            Assert.Equal(payload, File.ReadAllText(data.Value<string>("path")));
            Assert.Single(Directory.GetFiles(Path.Combine(_root, "spill")));
        }

        [Fact]
        public void Guard_off_keeps_optional_output_and_transport_cap_still_applies()
        {
            var config = SmallLimits();
            config.EnableResponseGuard = false;
            var result = RuntimeToolFilter.Apply(new CallToolRequestParams { Name = "revit_get_element_details" },
                Text(new string('x', 10000)), config);
            Assert.NotEqual(true, result.IsError);
            Assert.Equal(10000, ((TextContentBlock)result.Content[0]).Text.Length);
            result = RuntimeToolFilter.Apply(new CallToolRequestParams { Name = "revit_get_element_details" },
                Text(new string('x', 40000)), config);
            Assert.True(result.IsError);
            Assert.Contains("RESPONSE_TOO_LARGE", ((TextContentBlock)result.Content[0]).Text);
        }

        [Fact]
        public void Failed_and_dry_run_writes_never_claim_a_successful_mutation()
        {
            var request = new CallToolRequestParams { Name = "revit_create_level" };
            var failed = RuntimeToolFilter.Apply(request, Text("Error: " + new string('x', 30000)), SmallLimits());
            Assert.True(failed.IsError);
            var dry = RuntimeToolFilter.Apply(request, Text(new JObject { ["dry_run"] = true, ["detail"] = new string('x', 30000) }.ToString()), SmallLimits());
            Assert.False(JObject.Parse(((TextContentBlock)dry.Content[0]).Text).Value<bool>("mutation_applied"));
        }

        [Fact]
        public void Read_only_UI_tools_execute_without_the_old_group_level_block()
        {
            var before = ServerState.Config;
            try
            {
                ServerState.Config = new RvtMcpConfig { ReadOnly = true };
                Assert.Null(ServerState.BlockIfReadOnly("activate_view"));
                Assert.Null(ServerState.BlockIfReadOnly("show_element_in_view"));
                Assert.Contains("read_only_mode", ServerState.BlockIfReadOnly("set_view_crop"));
            }
            finally { ServerState.Config = before; }
        }

        [Fact]
        public void Inline_takeoff_does_not_claim_file_or_model_mutation_after_compaction()
        {
            var result = RuntimeToolFilter.Apply(new CallToolRequestParams { Name = "revit_get_material_takeoff" },
                Text(new JObject { ["rows"] = new string('x', 30000) }.ToString()), SmallLimits());
            Assert.True(result.IsError);
            Assert.Contains("RESPONSE_TOO_LARGE", ((TextContentBlock)result.Content[0]).Text);
        }

        [Fact]
        public void Already_spilled_unicode_preview_does_not_create_a_second_artifact()
        {
            var writer = new ResponseSpillWriter(Path.Combine(_root, "spill"));
            var payload = new string('界', 1800);
            var spill = writer.Write("send_code_to_revit", payload, ResponseSpillFormat.Text);
            spill.Envelope["mutation_applied"] = JValue.CreateNull();
            var result = RuntimeToolFilter.Apply(new CallToolRequestParams { Name = "revit_send_code_to_revit" },
                Text(spill.Envelope.ToString()), SmallLimits(), writer: writer);
            var data = JObject.Parse(((TextContentBlock)result.Content[0]).Text);
            Assert.Equal(spill.Path, data.Value<string>("path"));
            Assert.Equal(payload, File.ReadAllText(spill.Path));
            Assert.Single(Directory.GetFiles(Path.Combine(_root, "spill")));
            Assert.True(Encoding.UTF8.GetByteCount(RuntimeToolFilter.Serialize(result)) <= 8192);
        }

        [Theory]
        [InlineData("revit_get_element_details")]
        [InlineData("revit_create_level")]
        [InlineData("revit_send_code_to_revit")]
        public void Minimum_transport_cap_still_bounds_the_final_MCP_result(string name)
        {
            var result = RuntimeToolFilter.Apply(new CallToolRequestParams { Name = name },
                Text(new JObject { ["result"] = new string('界', 1800) }.ToString()),
                new RvtMcpConfig { ResponseWarnBytes = 1024, ResponseStrongWarnBytes = 1024, ResponseBudgetBytes = 1024, MaxResponseBytes = 1024 },
                writer: new ResponseSpillWriter(Path.Combine(_root, "spill")));
            Assert.True(Encoding.UTF8.GetByteCount(RuntimeToolFilter.Serialize(result)) <= 512);
        }

        private static RvtMcpConfig SmallLimits() => new RvtMcpConfig
        {
            ResponseWarnBytes = 2048, ResponseStrongWarnBytes = 4096, ResponseBudgetBytes = 8192, MaxResponseBytes = 32768
        };

        private static CallToolResult Text(string value) => new CallToolResult
        {
            Content = new[] { new TextContentBlock { Text = value } }
        };
    }
}
