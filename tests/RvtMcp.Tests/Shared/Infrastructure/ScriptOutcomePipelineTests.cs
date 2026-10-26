using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Failures;
using RvtMcp.Plugin;
using RvtMcp.Scripting;
using RvtMcp.Server;
using RvtMcp.Server.Memory;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("ServerStateConfig")]
    public class ScriptOutcomePipelineTests
    {
        internal static ScriptExecutionState State(string contract = ScriptModes.TaskV1, bool compileOnly = false)
        {
            var input = new ScriptInput("return null;", compileOnly: compileOnly, resultContract: contract, usageContext: ScriptModes.Test);
            var request = new ScriptWireRequest(input);
            return new ScriptExecutionState(JObject.FromObject(request.Metadata()), request.Parameters().ToString(Formatting.None));
        }

        internal static JObject Envelope(string native = "committed", bool success = true) => new JObject {
            ["success"] = success, ["data"] = new JObject { ["executed"] = true }, ["error"] = success ? null : "COMPILE_OR_RUNTIME_ERROR",
            ["_operation"] = new JObject { ["operationId"] = "0123456789abcdef0123456789abcdef", ["execution"] = "completed",
                ["outcome"] = native, ["complete"] = true, ["mutationApplied"] = native == "committed" || native == "partial" } };

        public static IEnumerable<object[]> AuditCases() => ScriptBaselineFixture.OutcomePairs().Select(p => new object[] {p.Value<string>("id")!});

        [Theory]
        [InlineData(true)] [InlineData(false)]
        public void Phase_timings_are_host_observations_not_forged_or_inferred_zero(bool compileOnly)
        {
            var input = new ScriptInput("return null;", compileOnly: compileOnly, resultContract: ScriptModes.TaskV1);
            var request = new ScriptWireRequest(input);
            var metadata = JObject.FromObject(request.Metadata()); metadata["compile_elapsed_ms"] = 999; metadata["invoke_elapsed_ms"] = 999;
            var state = new ScriptExecutionState(metadata, request.Parameters().ToString());
            Assert.Null(state.Script["compile_elapsed_ms"]); Assert.Null(state.Script["invoke_elapsed_ms"]);
            state.Compiling(); state.CompileFinished();
            if (compileOnly) state.Compiled();
            else { state.Invoking(); state.Complete(McpTaskResult.Fail("VERIFY_FAILED", "Check")); }
            var envelope = Envelope(); state.Apply(envelope);
            Assert.True(envelope["_script"].Value<double>("compile_elapsed_ms") >= 0);
            if (compileOnly) Assert.Null(envelope["_script"]["invoke_elapsed_ms"]);
            else
            {
                Assert.True(envelope["_script"].Value<double>("invoke_elapsed_ms") >= 0);
                Assert.Equal("failed", envelope["_task"].Value<string>("status"));
                Assert.Equal("committed", envelope["_operation"].Value<string>("outcome"));
            }
        }

        [Theory]
        [MemberData(nameof(AuditCases))]
        public void Both_audit_forms_run_the_real_contract_and_keep_native_receipts(string id)
        {
            var pair = ScriptBaselineFixture.OutcomePairs().Single(p => p.Value<string>("id") == id);
            foreach (var contract in new[] {ScriptModes.Legacy, ScriptModes.TaskV1})
            {
                var sample = pair[contract]!;
                var input = new ScriptInput(sample.Value<string>("code")!, resultContract: contract, usageContext: ScriptModes.Test);
                var request = new ScriptWireRequest(input);
                var state = new ScriptExecutionState(JObject.FromObject(request.Metadata()), request.Parameters().ToString());
                state.Compiling(); state.Invoking();
                var returned = SendCodeCompilerTests.Run(SendCodeCompilerTests.Compile(input));
                var detail = state.Complete(returned);
                var envelope = Envelope("unknown");
                envelope["_operation"] = pair["native_operation"]!.DeepClone();
                envelope["data"]!["result"] = detail == null ? JValue.CreateNull() : JToken.FromObject(detail);
                var before = envelope["_operation"]!.DeepClone();
                state.Apply(envelope);
                Assert.Equal(contract == ScriptModes.Legacy, envelope.Value<bool>("success"));
                Assert.Equal(contract == ScriptModes.Legacy ? "not_reported" : "failed", envelope["_task"]!.Value<string>("status"));
                Assert.True(JToken.DeepEquals(before, envelope["_operation"]));
                if (contract == ScriptModes.Legacy) Assert.NotNull(envelope["task_contract_hint"]);
                else
                {
                    Assert.IsType<McpTaskResult>(returned);
                    Assert.True(JToken.DeepEquals(sample["details"], envelope["data"]!["result"]));
                    Assert.Equal(sample.Value<string>("code_value") ?? sample.Value<string>("error_code"), envelope["_task"]!["error"]!.Value<string>("code"));
                }
            }
        }

        [Theory]
        [InlineData("committed", true)] [InlineData("unknown", true)]
        [InlineData("rolled_back", false)] [InlineData("partial", false)] [InlineData("cancelled", false)]
        [InlineData("failed", false)] [InlineData("not_started", false)]
        public void Task_pass_obeys_native_denials_without_rewriting_native_facts(string native, bool success)
        {
            var state = State(); state.Compiling(); state.Invoking(); state.Complete(McpTaskResult.Pass(7));
            var envelope = Envelope(native); var operation = envelope["_operation"]!.DeepClone(); state.Apply(envelope);
            Assert.Equal(success, envelope.Value<bool>("success"));
            Assert.Equal("passed", envelope["_task"]!["status"]);
            Assert.True(JToken.DeepEquals(operation, envelope["_operation"]));
        }

        [Theory]
        [InlineData("passed", true)] [InlineData("ok", true)] [InlineData("success", true)]
        [InlineData("nested", false)] [InlineData("string", false)] [InlineData("number", false)]
        public void Legacy_hint_only_matches_a_top_level_boolean_false(string name, bool hint)
        {
            object value = name == "nested" ? new JObject { ["child"] = new JObject { ["passed"] = false } }
                : name == "string" ? new JObject { ["passed"] = "false" }
                : name == "number" ? new JObject { ["passed"] = 0 } : new JObject { [name] = false };
            var envelope = Envelope(); var before = envelope.ToString(); ScriptExecutionState.AttachLegacyHint(envelope, value);
            Assert.Equal(hint, envelope["task_contract_hint"] != null);
            envelope.Remove("task_contract_hint"); Assert.Equal(before, envelope.ToString());
            if (hint)
            {
                ScriptExecutionState.AttachLegacyHint(envelope, value);
                envelope["success"] = false; envelope["error"] = "PRIMARY_FAILURE"; envelope["data"] = new string('x', 100000);
                var config = new RvtMcpConfig {EnableResponseGuard = true, MaxResponseBytes = 1024, ResponseBudgetBytes = 1024,
                    ResponseWarnBytes = 1024, ResponseStrongWarnBytes = 1024};
                var guarded = ResponseEnvelopeGuard.Apply("send_code_to_revit", "{}", envelope, config);
                Assert.Equal("PRIMARY_FAILURE", guarded["error"]); Assert.Null(guarded["task_contract_hint"]);
            }
        }

        [Fact]
        public void Wrong_type_is_required_and_compiler_runtime_errors_keep_their_primary_error()
        {
            var state = State(); state.Compiling(); state.Invoking(); state.Complete(new {passed = false});
            var envelope = Envelope(); state.Apply(envelope);
            Assert.False(envelope.Value<bool>("success")); Assert.Equal("TASK_RESULT_REQUIRED", envelope["_task"]!["error"]!["code"]);
            Assert.Null(envelope["task_contract_hint"]);
            foreach (var invoke in new[] {false, true})
            {
                state = State(); state.Compiling(); if (invoke) state.Invoking(); state.Failed();
                envelope = Envelope("not_started", false); state.Apply(envelope);
                Assert.Equal("COMPILE_OR_RUNTIME_ERROR", envelope["error"]);
                Assert.Equal("not_reported", envelope["_task"]!["status"]);
                Assert.Equal(invoke, envelope["_script"]!.Value<bool>("invoke_started"));
                Assert.Equal(invoke ? "runtime_failed" : "compile_failed", envelope["_script"]!["script_stage"]);
            }
            state = State(compileOnly: true); state.Compiling(); state.Compiled(); envelope = Envelope("not_started"); state.Apply(envelope);
            Assert.True(envelope.Value<bool>("success")); Assert.Equal("not_reported", envelope["_task"]!["status"]);
            Assert.False(envelope["_script"]!.Value<bool>("invoke_started"));
        }

        [Theory]
        [InlineData(true)] [InlineData(false)]
        public void Task_receipts_survive_native_and_mcp_size_guards(bool passed)
        {
            var root = Path.Combine(Path.GetTempPath(), "rvt-script-spill-" + Guid.NewGuid().ToString("N"));
            try
            {
                var state = State(); state.Compiling(); state.Invoking(); state.Complete(passed ? McpTaskResult.Pass() : McpTaskResult.Fail("VERIFY_FAILED", "Verification failed"));
                var envelope = Envelope(passed ? "committed" : "partial");
                envelope["_target"] = new JObject { ["target_id"] = "revit-2024-47724", ["pid"] = 47724, ["host_year"] = 2024,
                    ["document_id"] = "123456789abcdef0123456789abcdef0" };
                if (!passed) envelope["_failure"] = new JObject { ["schemaVersion"] = 1, ["operationId"] = envelope["_operation"]!["operationId"]!.DeepClone(),
                    ["action"] = "partial", ["mutationApplied"] = true, ["complete"] = true, ["gate"] = "open", ["persistence"] = "recorded", ["requiresReview"] = true };
                envelope["data"]!["result"] = new string('x', 100000); state.Apply(envelope);
                var config = new RvtMcpConfig { EnableResponseGuard = true, ResponseBudgetBytes = 4096, ResponseWarnBytes = 2048, ResponseStrongWarnBytes = 3072, MaxResponseBytes = 5120 };
                var writer = new ResponseSpillWriter(root);
                var guarded = ResponseEnvelopeGuard.Apply("send_code_to_revit", "{}", envelope, config, writer);
                Assert.Equal(passed ? "passed" : "failed", guarded["_task"]!["status"]);
                Assert.Equal(envelope["_script"]!["logical_call_id"], guarded["_script"]!["logical_call_id"]);
                var reply = ToolGateway.InterpretResponse(guarded);
                config.MaxResponseBytes = 1024; config.ResponseBudgetBytes = 1024;
                config.ResponseWarnBytes = 1024; config.ResponseStrongWarnBytes = 1024;
                var session = new SessionContext(true, Path.Combine(root, "journal"));
                var result = RuntimeToolFilter.Apply(new CallToolRequestParams {Name = "revit_send_code_to_revit"},
                    new CallToolResult {Content = new[] {new TextContentBlock {Text = reply.ToString()}}}, config, session, writer: writer);
                var compact = JObject.Parse(((TextContentBlock)result.Content[0]).Text);
                Assert.Equal(!passed, result.IsError == true);
                Assert.Equal(passed ? "passed" : "failed", compact["_task"]!["status"]);
                Assert.Equal(envelope["_operation"]!["outcome"], compact["_operation"]!["outcome"]);
                Assert.Equal(envelope["_script"]!["logical_call_id"], compact["_script"]!["logical_call_id"]);
                Assert.True(Encoding.UTF8.GetByteCount(RuntimeToolFilter.Serialize(result)) <= 768);
                Assert.Equal(47724, compact["_target"]!.Value<int>("pid"));
                Assert.Equal("123456789abcdef0123456789abcdef0", compact["_target"]!["document_id"]);
                if (!passed) { Assert.Equal("open", compact["_failure"]!["gate"]); Assert.Equal("recorded", compact["_failure"]!["persistence"]); }
                var logged = Assert.Single(session.Journal.ReadDay(DateTime.UtcNow.ToString("yyyy-MM-dd")));
                Assert.True(logged.Script!.Value<bool>("compile_attempted"));
                Assert.Equal(passed ? "passed" : "failed", logged.Task!["status"]);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Fact]
        public void Durable_final_result_keeps_task_error_code_and_identity_without_user_details()
        {
            var root = Path.Combine(Path.GetTempPath(), "rvt-script-ledger-" + Guid.NewGuid().ToString("N"));
            try
            {
                var state = State(); state.Compiling(); state.Invoking(); state.Complete(McpTaskResult.Fail("VERIFY_FAILED", "Check protected value", new {token = "PRIVATE-DATA"}));
                var envelope = Envelope(); state.Apply(envelope);
                envelope["data"] = new JObject { ["private"] = "PRIVATE-DATA" };
                envelope["_changes"] = new JObject { ["padding"] = new string('x', 9000) };
                var store = new FailureLedgerStore(Path.Combine(root, "ledger.db"));
                var operation = new FailureOperation {Execution = "completed", Outcome = "committed", Complete = true, FinalResult = envelope};
                Assert.True(store.SaveOperation(operation).Recorded);
                var final = store.GetOperation(operation.OperationId).FinalResult;
                Assert.False(final.Value<bool>("success")); Assert.Equal("VERIFY_FAILED", final["_task"]!["error"]!["code"]);
                Assert.Equal("failed", final["_task"]!["status"]);
                Assert.Equal(envelope["_script"]!["logical_call_id"], final["_script"]!["logical_call_id"]);
                Assert.Equal("committed", final["_operation"]!["outcome"]);
                Assert.DoesNotContain("PRIVATE-DATA", final.ToString()); Assert.True(Encoding.UTF8.GetByteCount(final.ToString(Formatting.None)) <= 4096);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Fact]
        public async Task Timeout_and_duplicate_final_frames_do_not_finalize_or_count_twice()
        {
            var script = State().Script; var count = 0;
            var pending = new GatewayPendingRequest("op", 1, script: script, finalizeScript: _ => count++);
            pending.Timeout(); var timeout = JObject.Parse(await pending.Caller.Task);
            Assert.Equal("unknown", timeout["_task"]!["status"]); Assert.False(pending.Execution.Task.IsCompleted); Assert.Equal(0, count);
            var state = State(); state.Compiling(); state.Invoking(); state.Complete(McpTaskResult.Fail("VERIFY_FAILED", "Check"));
            var final = Envelope(); state.Apply(final); pending.Route(final); pending.Route(final);
            Assert.Equal(1, count); var terminal = JObject.Parse(await pending.Execution.Task);
            Assert.Equal("failed", terminal["_task"]!["status"]); Assert.False(terminal.Value<bool>("success"));
            Assert.Equal("unknown", JObject.Parse(await pending.Caller.Task)["_task"]!["status"]);
            var previous = new PreviousOperationTracker(); previous.Remember("op", "revit_send_code_to_revit", pending.Execution.Task);
            var next = new JObject(); previous.Attach(next);
            Assert.Equal("failed", next["_previousOperation"]![0]!["_task"]!["status"]);
            Assert.Equal(final["_script"]!["logical_call_id"], next["_previousOperation"]![0]!["_script"]!["logical_call_id"]);
        }

        [Fact]
        public void Bookkeeping_failure_does_not_change_native_or_task_outcome()
        {
            var state = State(); state.Compiling(); state.Invoking(); state.Complete(McpTaskResult.Pass());
            var final = Envelope(); state.Apply(final);
            var pending = new GatewayPendingRequest("op", 1, script: state.Script, finalizeScript: _ => throw new IOException("Unavailable"));
            pending.Route(final);
            Assert.True(final.Value<bool>("success")); Assert.Equal("committed", final["_operation"]!["outcome"]);
            Assert.Equal("passed", final["_task"]!["status"]); Assert.Equal("write_failed", final["_script"]!["telemetry_status"]);
        }

        [Fact]
        public void Failed_server_journal_keeps_a_successful_task_and_reports_telemetry_status()
        {
            var root = Path.Combine(Path.GetTempPath(), "rvt-script-journal-fault-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllText(root, "directory blocked");
                var state = State(); state.Compiling(); state.Invoking(); state.Complete(McpTaskResult.Pass());
                var envelope = Envelope(); state.Apply(envelope);
                var result = RuntimeToolFilter.Apply(new CallToolRequestParams {Name = "revit_send_code_to_revit"},
                    new CallToolResult {Content = new[] {new TextContentBlock {Text = ToolGateway.InterpretResponse(envelope).ToString()}}},
                    new RvtMcpConfig(), new SessionContext(true, root));
                var final = JObject.Parse(((TextContentBlock)result.Content[0]).Text);
                Assert.False(result.IsError == true); Assert.Equal("passed", final["_task"]!["status"]);
                Assert.Equal("committed", final["_operation"]!["outcome"]); Assert.Equal("write_failed", final["_script"]!["telemetry_status"]);
            }
            finally { if (File.Exists(root)) File.Delete(root); }
        }

        [Fact]
        public void Long_unicode_task_errors_still_fit_the_minimum_mcp_cap()
        {
            var state = State(); state.Compiling(); state.Invoking(); state.Complete(McpTaskResult.Fail(new string('漢', 256), new string('界', 500)));
            var envelope = Envelope("partial"); state.Apply(envelope);
            var config = new RvtMcpConfig {EnableResponseGuard = true, MaxResponseBytes = 1024, ResponseBudgetBytes = 1024,
                ResponseWarnBytes = 1024, ResponseStrongWarnBytes = 1024};
            var result = RuntimeToolFilter.Apply(new CallToolRequestParams {Name = "revit_send_code_to_revit"},
                new CallToolResult {Content = new[] {new TextContentBlock {Text = ToolGateway.InterpretResponse(envelope).ToString()}}}, config);
            var final = JObject.Parse(((TextContentBlock)result.Content[0]).Text);
            Assert.True(result.IsError == true); Assert.Equal("failed", final["_task"]!["status"]);
            Assert.True(final["_task"]!["error"]!.Value<bool>("code_truncated"));
            Assert.Equal("partial", final["_operation"]!["outcome"]);
            Assert.True(Encoding.UTF8.GetByteCount(RuntimeToolFilter.Serialize(result)) <= 768);
        }

        [Fact]
        public void Failed_spill_is_a_failed_output_with_task_pass_and_native_commit_preserved()
        {
            var root = Path.Combine(Path.GetTempPath(), "rvt-script-spill-fault-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllText(root, "directory blocked");
                var state = State(); state.Compiling(); state.Invoking(); state.Complete(McpTaskResult.Pass());
                var envelope = Envelope(); envelope["data"]!["result"] = new string('x', 100000); state.Apply(envelope);
                var config = new RvtMcpConfig {EnableResponseGuard = true, ResponseBudgetBytes = 4096, ResponseWarnBytes = 2048, ResponseStrongWarnBytes = 3072, MaxResponseBytes = 5120};
                var guarded = ResponseEnvelopeGuard.Apply("send_code_to_revit", "{}", envelope, config, new ResponseSpillWriter(root));
                Assert.False(guarded.Value<bool>("success")); Assert.Equal("passed", guarded["_task"]!["status"]);
                Assert.Equal("committed", guarded["_operation"]!["outcome"]);
                var outcome = CommandOutcome.Normalize("send_code_to_revit", guarded.Value<bool>("success"), null, guarded.Value<string>("error"), null);
                Assert.False(outcome.Success);
            }
            finally { if (File.Exists(root)) File.Delete(root); }
        }
    }
}
