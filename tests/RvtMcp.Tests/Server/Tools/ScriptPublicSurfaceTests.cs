using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Scripting;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("ServerStateConfig")]
    public sealed class ScriptPublicSurfaceTests
    {
        [Fact]
        public void Phase_one_actual_SDK_schemas_match_reviewed_golden()
        {
            var tools = Program.CreateRegisteredTools(ToolsetFilter.Resolve(new RvtMcpConfig()), new RvtMcpConfig())
                .Select(t => t.ProtocolTool).Where(t => t.Name == "revit_send_code_to_revit" || t.Name.EndsWith("_code_module") || t.Name == "revit_list_code_modules")
                .Select(t => new JObject { ["name"] = t.Name, ["description_hash"] = Helpers.SnapshotSerializer.HashDescription(t.Description),
                    ["inputSchema"] = JObject.Parse(t.InputSchema.GetRawText()), ["annotations"] = t.Annotations == null ? null : JObject.Parse(JsonSerializer.Serialize(t.Annotations, ModelContextProtocol.McpJsonUtilities.DefaultOptions)) }).ToArray();
            Assert.Equal(4, tools.Length);
            var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Golden", "script-tools-schema.json");
            Helpers.SnapshotSerializer.VerifyGolden(path, Helpers.SnapshotSerializer.Serialize(tools.Length, tools), "PhaseOneScriptSchemas");
        }

        [Fact]
        public void Actual_SDK_schema_has_all_six_parameters_and_only_code_is_required()
        {
            var tool = Program.CreateRegisteredTools(ToolsetFilter.Resolve(new RvtMcpConfig()), new RvtMcpConfig())
                .Single(t => t.ProtocolTool.Name == "revit_send_code_to_revit").ProtocolTool;
            var schema = JObject.Parse(tool.InputSchema.GetRawText());
            Assert.Equal(new[] {"code", "data", "modules", "compile_only", "result_contract", "usage_context"}, ((JObject)schema["properties"]).Properties().Select(p => p.Name));
            Assert.Equal(new[] {"code"}, ((JArray)schema["required"]).Values<string>());
            var dataType = schema["properties"]["data"]["type"];
            Assert.Contains("object", dataType is JArray types ? types.Values<string>() : new[] {dataType.Value<string>()});
            Assert.False(schema["properties"]["compile_only"].Value<bool>("default"));
            Assert.Equal("legacy", schema["properties"]["result_contract"].Value<string>("default"));
            Assert.Equal("unknown", schema["properties"]["usage_context"].Value<string>("default"));
            Assert.Equal("string", schema["properties"]["modules"]["items"].Value<string>("type"));
            Assert.Equal(new[] {"legacy", "task_v1"}, ((JArray)schema["properties"]["result_contract"]["enum"]).Values<string>());
            Assert.Null(tool.Annotations);
            Assert.True(tool.Description.Length <= 599, "Keep the former description length budget");
            foreach (var hint in new[] {"data", "coordinates", "modules", "task_v1", "McpTaskResult.Pass/Fail", "SafeFailuresPreprocessor"}) Assert.Contains(hint, tool.Description);
        }

        [Theory]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, false)]
        [InlineData(false, true, true)]
        public void Public_catalog_and_readonly_policy_match_runtime_permissions(bool readOnly, bool sendCode, bool adaptive)
        {
            var config = new RvtMcpConfig {ReadOnly = readOnly, EnableSendCode = sendCode, EnableAdaptiveBake = adaptive};
            var names = Program.CreateRegisteredTools(ToolsetFilter.Resolve(config), config).Select(t => t.ProtocolTool.Name).ToArray();
            Assert.Contains("revit_list_code_modules", names);
            var write = !readOnly && sendCode;
            foreach (var name in new[] {"revit_save_code_module", "revit_delete_code_module", "revit_send_code_to_revit"}) Assert.Equal(write, names.Contains(name));
            Assert.Contains("list_code_modules", ToolReadPolicy.ReadOnlyCommands);
            var catalog = ServerToolCatalogBuilder.Build(ToolsetFilter.Resolve(config), config);
            Assert.Equal("server_local", catalog.Tools.Single(t => t.McpName == "revit_list_code_modules").Timeout.PolicyKind);
            if (write) Assert.Equal("fixed", catalog.Tools.Single(t => t.McpName == "revit_save_code_module").Timeout.PolicyKind);
        }

        [Fact]
        public async Task Default_arguments_keep_legacy_wire_while_direct_data_preserves_large_numbers()
        {
            var original = ToolGateway.SendOverride; var calls = new List<JObject>();
            ToolGateway.SendOverride = (_, args, __) => { calls.Add(JObject.FromObject(args)); return Task.FromResult(new JObject { ["ok"] = true }); };
            try
            {
                await SendCodeTools.SendCodeToRevit("return 7;");
                await SendCodeTools.SendCodeToRevit("return 7;", modules: Array.Empty<string>());
                Assert.All(calls, args => Assert.Equal(new[] {"code"}, args.Properties().Select(p => p.Name)));
                var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"n\":9007199254740993,\"secret\":\"private-script-input\"}");
                await SendCodeTools.SendCodeToRevit("return data;", data, usage_context: "test");
                Assert.Contains("9007199254740993", calls.Last().Value<string>("data_json"));
                Assert.Equal("test", calls.Last().Value<string>("usage_context"));
            }
            finally { ToolGateway.SendOverride = original; }
        }

        [Theory]
        [InlineData("invalid", "test")]
        [InlineData("legacy", "invalid")]
        public async Task Invalid_modes_fail_before_dispatch(string contract, string usage)
        {
            var original = ToolGateway.SendOverride; var calls = 0;
            ToolGateway.SendOverride = (_,__,___) => { calls++; throw new Exception("must not dispatch"); };
            try
            {
                Assert.Contains(ScriptErrorCodes.InputInvalid, await SendCodeTools.SendCodeToRevit("return 7;", result_contract: contract, usage_context: usage));
                Assert.Equal(0, calls);
            }
            finally { ToolGateway.SendOverride = original; }
        }

        [Fact]
        public void Health_distinguishes_loaded_gateway_support_from_unknown_plugin_support()
        {
            var gateway = new JObject { ["process_id"] = 123, ["assembly_informational_version"] = "1.2.0" };
            var health = new JObject(); HealthRuntimeInfo.Enrich(health, gateway, false);
            Assert.True(health["gateway_script_protocol"].Value<bool>("supported"));
            Assert.Null(health["script_protocol"]);
            Assert.Equal(123, HealthRuntimeInfo.ClientRuntime(gateway).Value<int>("process_id"));
            HostHealthSnapshot.Initialize(typeof(ScriptPublicSurfaceTests).Assembly, 2024);
            var native = HostHealthSnapshot.Capture(0, false);
            Assert.True(native["script_protocol"].Value<bool>("supported"));
            Assert.Equal(1, native["script_protocol"].Value<int>("version"));
            Assert.False(native.Value<bool?>("ready_for_commands").HasValue);
        }
    }
}
