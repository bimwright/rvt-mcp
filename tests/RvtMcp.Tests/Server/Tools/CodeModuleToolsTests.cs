using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ModelContextProtocol.Server;
using RvtMcp.Plugin;
using RvtMcp.Scripting;
using RvtMcp.Server;
using RvtMcp.Server.Bake;
using RvtMcp.Server.Memory;
using Xunit;

namespace RvtMcp.Tests
{
    // Synthetic host receipts exercise the persistence boundary, not native Revit acceptance.
    [Collection("ServerStateConfig")]
    public sealed class CodeModuleToolsTests : IDisposable
    {
        private readonly CodeModuleStoreTests _fixture = new CodeModuleStoreTests();
        private readonly RvtMcpConfig _original = ServerState.Config;
        public CodeModuleToolsTests() { ServerState.Config = new RvtMcpConfig(); }
        public void Dispose() { ServerState.Config = _original; _fixture.Dispose(); }

        internal static JObject Receipt(ScriptInput input, IReadOnlyList<ScriptModuleRevision> modules, ScriptRuntimeMetadata metadata)
        {
            var wire = new ScriptWireRequest(input, modules);
            var meta = JObject.FromObject(metadata); wire.ValidateMetadata(meta);
            var compiled = SendCodeCompilerTests.Compile(input, modules.ToArray());
            var script = ScriptTelemetry.Create(meta, wire.Parameters().ToString(Formatting.None));
            script["script_stage"] = compiled.Success ? "compiled" : "compile_failed";
            script["compile_attempted"] = true; script["invoke_started"] = false;
            return new JObject { ["success"] = compiled.Success, ["compiled"] = compiled.Success,
                ["compile_only"] = true, ["executed"] = false,
                ["module_signatures"] = JObject.FromObject(compiled.ModuleSignatures),
                ["compile_host"] = ScriptCompileHost.Capture(2024), ["_script"] = script,
                ["_task"] = new JObject { ["contract"] = "legacy", ["status"] = "not_reported" },
                ["_operation"] = new JObject { ["operationId"] = Guid.NewGuid().ToString("N"), ["execution"] = "completed",
                    ["complete"] = true, ["outcome"] = "unknown", ["mutationApplied"] = null },
                ["_target"] = new JObject { ["host_year"] = 2024, ["pid"] = Process.GetCurrentProcess().Id } };
        }

        private CodeModuleToolCore Core(Action<JObject> alter = null) => new CodeModuleToolCore(_fixture.Store, (input, modules, metadata) => {
            var receipt = Receipt(input, modules, metadata); alter?.Invoke(receipt); return Task.FromResult(receipt);
        });
        private Task<JObject> Save(CodeModuleToolCore core, string name = "helper", string code = "public class Helper { public static int Value() => 7; }")
            => core.SaveAsync(name, code, "", null, null);

        [Fact]
        public async Task Save_records_compile_proof_and_distinct_parent_child_identities_without_invocation()
        {
            var result = await Save(Core()); var record = Assert.Single(_fixture.Store.List());
            Assert.True(result.Value<bool>("saved")); Assert.Equal(2024, result["validation"].Value<int>("host_year"));
            Assert.False(result["validation"].Value<bool>("native_verified"));
            Assert.Equal(record.Revision.CodeSha256, result["module"].Value<string>("code_sha256"));
            Assert.Equal(ScriptIdentity.Utf8Bytes(record.Revision.Code), result["module"].Value<int>("normalized_byte_count"));
            Assert.Contains("Helper.Value()", record.SignaturesJson);
            Assert.Contains(Environment.Version.ToString(), record.CompiledHostJson);
            var script = result["compile_receipt"]["_script"];
            Assert.Equal(result.Value<string>("logical_call_id"), script.Value<string>("parent_logical_call_id"));
            Assert.NotEqual(script.Value<string>("logical_call_id"), script.Value<string>("parent_logical_call_id"));
            Assert.Equal("module_compile", script.Value<string>("execution_kind"));
            Assert.Equal("test", script.Value<string>("usage_context"));
            Assert.Null(result["module"]["code"]);
        }

        [Theory]
        [InlineData("compiled", "false")]
        [InlineData("executed", "true")]
        [InlineData("compile_only", "false")]
        [InlineData("_script.compile_attempted", "false")]
        [InlineData("_script.invoke_started", "true")]
        [InlineData("_script.script_stage", "\"invoked\"")]
        [InlineData("_script.mode", "\"execute\"")]
        [InlineData("_script.parent_logical_call_id", "\"wrong\"")]
        [InlineData("_script.logical_call_id", "\"wrong\"")]
        [InlineData("_script.module_manifest_hash", "\"wrong\"")]
        [InlineData("_script.script_code_sha256", "\"wrong\"")]
        [InlineData("_script.entry_tool", "\"revit_send_code_to_revit\"")]
        [InlineData("_script.execution_kind", "\"script\"")]
        [InlineData("_script.usage_context", "\"project\"")]
        [InlineData("_script.result_contract", "\"task_v1\"")]
        [InlineData("_script.data_hash", "\"unexpected\"")]
        [InlineData("_operation.execution", "\"pending\"")]
        [InlineData("_operation.complete", "false")]
        [InlineData("_operation.operationId", "\"invalid\"")]
        [InlineData("_operation.mutationApplied", "true")]
        [InlineData("_operation.outcome", "\"committed\"")]
        [InlineData("compile_host.host_year", "2026")]
        [InlineData("compile_host.process_id", "-1")]
        [InlineData("compile_host.identity_basis", "\"target_framework\"")]
        [InlineData("compile_host.runtime_identity", "\"\"")]
        [InlineData("module_signatures.helper", "[17]")]
        [InlineData("_task.status", "\"passed\"")]
        public async Task Unverified_or_mismatched_receipts_never_persist_a_module(string path, string json)
        {
            var core = Core(r => r.SelectToken(path).Replace(JToken.Parse(json)));
            var error = await Assert.ThrowsAsync<ModuleValidationException>(() => Save(core));
            Assert.NotNull(error.Receipt); Assert.Empty(_fixture.Store.List());
            Assert.Equal(0, _fixture.Scalar("SELECT COUNT(*) FROM code_module_revisions"));
        }

        [Theory]
        [InlineData("_script")]
        [InlineData("_operation")]
        [InlineData("_target")]
        [InlineData("compile_host")]
        [InlineData("module_signatures")]
        [InlineData("_task")]
        public async Task Missing_native_proof_never_persists(string field)
        {
            await Assert.ThrowsAsync<ModuleValidationException>(() => Save(Core(r => r.Remove(field))));
            Assert.Empty(_fixture.Store.List());
        }

        [Fact]
        public async Task Syntax_failure_never_commits_and_compiler_does_not_run_initializers()
        {
            await Assert.ThrowsAsync<ModuleValidationException>(() => Save(Core(), code: "public class Broken {"));
            var marker = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_fixture.DbPath), "initializer-witness");
            var code = "public class Helper { static Helper() { System.IO.File.WriteAllText(@\"" + marker + "\", \"executed\"); } public static int Value() => 7; }";
            await Save(Core(), code: code); Assert.False(System.IO.File.Exists(marker));
        }

        [Fact]
        public async Task Dependency_change_during_native_wait_is_rejected_after_compile()
        {
            var dependency = await _fixture.Save("dependency");
            var core = new CodeModuleToolCore(_fixture.Store, async (input, modules, metadata) => {
                var receipt = Receipt(input, modules, metadata);
                await _fixture.Save("dependency", "public class Changed {}", expected: dependency.Record.Revision.Hash);
                return receipt;
            });
            var error = await Assert.ThrowsAsync<ScriptContractException>(() => core.SaveAsync("root", "class Root {}", "", new[] {"dependency"}, null));
            Assert.Equal(ScriptErrorCodes.ModuleConflict, error.ErrorCode); Assert.Single(_fixture.Store.List());
        }

        [Fact]
        public async Task Persistence_policy_precedes_host_dispatch()
        {
            var calls = 0;
            var core = new CodeModuleToolCore(_fixture.Store, (_,__,___) => { calls++; throw new Exception("Must not dispatch"); });
            var error = await Assert.ThrowsAsync<ScriptContractException>(() => Save(core, code: "class Helper { string password = \"private-password\"; }"));
            Assert.Equal(ScriptErrorCodes.SourceNotStorable, error.ErrorCode); Assert.Equal(0, calls); Assert.Empty(_fixture.Store.List());
        }

        [Fact]
        public async Task Noop_reports_current_validation_manifest_while_retaining_original_compile_metadata()
        {
            var dep = await _fixture.Save("dependency");
            const string code = "public class Root {}";
            var core = Core(); var first = await core.SaveAsync("root", code, "", new[] {"dependency"}, null);
            await _fixture.Save("dependency", "class Changed {}", expected: dep.Record.Revision.Hash);
            var second = await core.SaveAsync("root", code, "", new[] {"dependency"}, first["module"].Value<string>("hash"));
            Assert.True(second.Value<bool>("unchanged"));
            Assert.NotEqual(first["validation"].Value<string>("module_manifest_hash"), second["validation"].Value<string>("module_manifest_hash"));
            Assert.True(JToken.DeepEquals(first["module"]["compiled_host"], second["module"]["compiled_host"]));
        }

        [Fact]
        public async Task Send_code_resolves_stored_modules_into_an_immutable_wire_snapshot()
        {
            var revision = await _fixture.Save("helper");
            var previousFactory = SendCodeCore.StoreFactory; var previousSend = ToolGateway.SendOverride;
            SendCodeCore.StoreFactory = () => new BakeDb(_fixture.DbPath);
            JObject sent = null;
            ToolGateway.SendOverride = (_, args, __) => { sent = JObject.FromObject(args); return Task.FromResult(new JObject()); };
            try
            {
                await SendCodeCore.SendAsync(new ScriptInput("return null;", modules: new[] {"helper"}));
                Assert.Equal(revision.Record.Revision.Code, sent["resolved_modules"][0].Value<string>("code"));
                Assert.Equal(revision.Record.Revision.Hash, sent["resolved_modules"][0].Value<string>("hash"));
            }
            finally { SendCodeCore.StoreFactory = previousFactory; ToolGateway.SendOverride = previousSend; }
        }

        [Theory]
        [InlineData(typeof(CodeModuleReadTools), "ListCodeModules", "revit_list_code_modules", true, "")]
        [InlineData(typeof(CodeModuleWriteTools), "SaveCodeModule", "revit_save_code_module", false, "name,code")]
        [InlineData(typeof(CodeModuleWriteTools), "DeleteCodeModule", "revit_delete_code_module", false, "name,expected_hash")]
        public void Definitions_have_exact_MCP_schema_and_storage_annotations(Type type, string method, string name, bool readOnly, string required)
        {
            var info = type.GetMethod(method); var tool = McpServerTool.Create(info, (object)null).ProtocolTool;
            Assert.Equal(name, tool.Name);
            var annotation = info.GetCustomAttribute<McpServerToolAttribute>();
            Assert.Equal(readOnly, annotation.ReadOnly); Assert.Equal(!readOnly, annotation.Destructive);
            Assert.Equal(readOnly, annotation.Idempotent); Assert.False(annotation.OpenWorld);
            var schema = JObject.Parse(tool.InputSchema.GetRawText());
            Assert.Equal(required.Length == 0 ? Array.Empty<string>() : required.Split(','), (schema["required"] as JArray)?.Values<string>() ?? Array.Empty<string>());
            if (readOnly)
            {
                Assert.Equal("boolean", schema["properties"]["include_code"].Value<string>("type"));
                Assert.Equal("integer", schema["properties"]["limit"].Value<string>("type"));
            }
            else Assert.Contains("Revit Undo", tool.Description);
        }

        [Fact]
        public async Task List_pages_without_host_and_source_is_opt_in_with_dependency_status()
        {
            var dep = await _fixture.Save("dependency"); await _fixture.Save("root", requires: new[] {"dependency"});
            var core = new CodeModuleToolCore(_fixture.Store, (_,__,___) => throw new Exception("Must not contact Revit"));
            var page = core.List("", false, 0, 1);
            Assert.Equal(2, page.Value<int>("total")); Assert.True(page.Value<bool>("has_more")); Assert.Equal(1, page.Value<int>("next_start_index"));
            Assert.Null(page["modules"][0]["code"]);
            Assert.Equal("public class root {}", core.List("root", true, 0, 100)["modules"][0].Value<string>("code"));
            await _fixture.Save("dependency", "class Changed {}", expected: dep.Record.Revision.Hash);
            Assert.Equal("changed", core.List("root", false, 0, 100)["modules"][0].Value<string>("dependency_status"));
            Assert.Empty((JArray)core.List("", false, int.MaxValue, 500)["modules"]);
            Assert.False(core.List("", false, int.MaxValue, 500).Value<bool>("has_more"));
            _fixture.Sql("DELETE FROM code_modules WHERE name='dependency'");
            Assert.Equal("missing", core.List("root", false, 0, 1)["modules"][0].Value<string>("dependency_status"));
        }

        [Theory]
        [InlineData(-1, 100)] [InlineData(0, 0)] [InlineData(0, 501)]
        public void Invalid_pages_fail_explicitly(int start, int limit)
            => Assert.Throws<ScriptContractException>(() => Core().List("", false, start, limit));

        [Fact]
        public async Task Delete_requires_CAS_refuses_dependents_and_never_contacts_host()
        {
            var dep = await _fixture.Save("dependency"); var root = await _fixture.Save("root", requires: new[] {"dependency"});
            var core = new CodeModuleToolCore(_fixture.Store, (_,__,___) => throw new Exception("Must not contact Revit"));
            Assert.Equal(ScriptErrorCodes.ModuleInUse, Assert.Throws<ScriptContractException>(() => core.Delete("dependency", dep.Record.Revision.Hash)).ErrorCode);
            Assert.Throws<ScriptContractException>(() => core.Delete("root", null));
            Assert.True(core.Delete("root", root.Record.Revision.Hash).Value<bool>("deleted"));
            Assert.True(core.Delete("dependency", dep.Record.Revision.Hash).Value<bool>("deleted"));
            Assert.Empty(_fixture.Store.List()); Assert.Equal(2, _fixture.Scalar("SELECT COUNT(*) FROM code_module_revisions"));
        }

        [Theory]
        [InlineData(true, true)] [InlineData(false, false)]
        public async Task Write_policy_rejects_before_opening_store_but_allows_list(bool readOnly, bool enabled)
        {
            ServerState.Config.ReadOnly = readOnly; ServerState.Config.EnableSendCode = enabled;
            var previous = CodeModuleTools.StoreFactory; var opens = 0;
            CodeModuleTools.StoreFactory = () => { opens++; return new BakeDb(_fixture.DbPath); };
            try
            {
                Assert.False(JObject.Parse(await CodeModuleWriteTools.SaveCodeModule("a", "class A {}" )).Value<bool>("success"));
                Assert.False(JObject.Parse(CodeModuleWriteTools.DeleteCodeModule("a", new string('a',64))).Value<bool>("success"));
                Assert.Equal(0, opens);
                Assert.True(JObject.Parse(CodeModuleReadTools.ListCodeModules()).Value<bool>("success")); Assert.Equal(1, opens);
            }
            finally { CodeModuleTools.StoreFactory = previous; }
        }

        [Fact]
        public void Registration_is_staged_and_future_readonly_catalog_has_only_list()
        {
            var enabled = new HashSet<string> {"meta"}; var config = new RvtMcpConfig();
            Assert.Empty(CodeModuleToolRegistration.Resolve(enabled, config, false));
            Assert.Equal(2, CodeModuleToolRegistration.Resolve(enabled, config, true).Count());
            config.ReadOnly = true; Assert.Equal(typeof(CodeModuleReadTools), Assert.Single(CodeModuleToolRegistration.Resolve(enabled, config, true)));
            config.ReadOnly = false; config.EnableSendCode = false;
            Assert.Equal(typeof(CodeModuleReadTools), Assert.Single(CodeModuleToolRegistration.Resolve(enabled, config, true)));
            Assert.Empty(CodeModuleToolRegistration.Resolve(new HashSet<string> {"query"}, config, true));
            Assert.True(ToolReadPolicy.IsReadOnly("list_code_modules"));
        }

        [Theory]
        [InlineData(false, "test", false)]
        [InlineData(true, "project", false)]
        [InlineData(true, "test", true)]
        public void Module_compile_wire_requires_compile_only_test_context_and_distinct_parent(bool compileOnly, string usage, bool sameId)
        {
            var module = new ScriptModuleRevision("helper", "class Helper {}");
            var wire = new ScriptWireRequest(new ScriptInput("return null;", modules: new[] {"helper"}, compileOnly: compileOnly, usageContext: usage), new[] {module});
            var metadata = wire.Metadata(); metadata.ExecutionKind = "module_compile"; metadata.EntryTool = "revit_save_code_module";
            metadata.ParentLogicalCallId = sameId ? metadata.LogicalCallId : Guid.NewGuid().ToString("N");
            Assert.Throws<ScriptContractException>(() => wire.ValidateMetadata(JObject.FromObject(metadata)));
        }

        [Fact]
        public void Source_is_absent_from_journal_arguments_and_include_code_results()
        {
            const string source = "private-source-marker-7616";
            var args = new JObject { ["code"] = source, ["description"] = source, ["name"] = "helper" }.ToString();
            var save = JournalEntry.Create("save_code_module", args, true, 1);
            Assert.DoesNotContain(source, save.Params); Assert.Contains(ScriptIdentity.CodeSha256(source), save.Params);
            var list = JournalEntry.Create("list_code_modules", "{\"include_code\":true}", true, 1,
                resultJson: new JObject { ["modules"] = new JArray(new JObject { ["name"] = "helper", ["code"] = source }) }.ToString());
            Assert.DoesNotContain(source, list.Result); Assert.Contains("code_omitted", list.Result);
        }
    }
}
