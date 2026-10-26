using System;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using RvtMcp.Scripting;

namespace RvtMcp.Plugin
{
    /// <summary>Host-owned execution facts. Task normalization runs after native completion, never before it.</summary>
    internal sealed class ScriptExecutionState
    {
        internal JObject Script { get; }
        internal object Returned { get; private set; }
        internal bool CompileAttempted { get; private set; }
        internal bool InvokeStarted { get; private set; }
        internal string Stage { get; private set; } = "preflight";
        private Stopwatch _compileClock, _invokeClock;
        internal ScriptExecutionState(JObject metadata, string parameters)
        {
            Script = ScriptTelemetry.Create(metadata, parameters);
            Script.Remove("compile_elapsed_ms"); Script.Remove("invoke_elapsed_ms");
        }
        internal void Compiling() { CompileAttempted = true; Stage = "compile"; _compileClock = Stopwatch.StartNew(); }
        internal void CompileFinished() => Finish(_compileClock, "compile_elapsed_ms");
        internal void Invoking() { CompileFinished(); InvokeStarted = true; Stage = "invoke"; _invokeClock = Stopwatch.StartNew(); }
        private void Finish(Stopwatch clock, string field)
        {
            if (clock == null || !clock.IsRunning) return;
            clock.Stop(); Script[field] = Math.Round(clock.Elapsed.TotalMilliseconds, 3);
        }
        internal object Complete(object returned)
        {
            Finish(_invokeClock, "invoke_elapsed_ms"); Returned = returned; Stage = "completed";
            if (Script.Value<string>("result_contract") == ScriptModes.TaskV1 && returned is McpTaskResult task)
                return task.IsPassed ? task.Value : task.Details;
            return returned;
        }
        internal void Compiled() { CompileFinished(); Stage = "compiled"; }
        internal void Failed()
        {
            CompileFinished(); Finish(_invokeClock, "invoke_elapsed_ms");
            Stage = Stage == "completed" || Stage == "compiled" ? "output_failed"
                : InvokeStarted ? "runtime_failed" : CompileAttempted ? "compile_failed" : "preflight";
            Script["script_stage"] = Stage;
        }

        internal void Apply(JObject envelope)
        {
            Script["script_stage"] = Stage; Script["compile_attempted"] = CompileAttempted; Script["invoke_started"] = InvokeStarted;
            var operation = envelope["_operation"] as JObject;
            var contract = Script.Value<string>("result_contract");
            var outcome = ScriptTaskOutcome.Evaluate(contract, Script.Value<string>("mode") == "compile_only",
                envelope.Value<bool>("success"), operation?.Value<string>("execution") ?? "completed",
                operation?.Value<bool?>("complete") ?? true, operation?.Value<string>("outcome"), Returned);
            var task = new JObject { ["contract"] = contract, ["status"] = outcome.TaskStatus };
            if (outcome.ErrorCode != null)
            {
                task["error"] = new JObject { ["code"] = ScriptTelemetry.Safe(outcome.ErrorCode, 128),
                    ["message"] = ScriptTelemetry.Safe(outcome.TaskResult?.ErrorMessage ?? "Return McpTaskResult.Pass or McpTaskResult.Fail under task_v1.", 512) };
                if (outcome.ErrorCode.Length > 128) task["error"]["code_truncated"] = true;
            }
            envelope["_task"] = task;
            envelope["_script"] = ScriptTelemetry.BoundScript(Script);
            if (!outcome.Success)
            {
                envelope["success"] = false;
                if (envelope["error"] == null || envelope["error"] is JValue && string.IsNullOrEmpty(envelope.Value<string>("error")))
                    envelope["error"] = task["error"] is JObject error ? error.Value<string>("code") + ": " + error.Value<string>("message")
                        : "SCRIPT_NATIVE_OUTCOME: Native completion does not confirm script success; inspect _operation.";
            }
            if (contract == ScriptModes.Legacy) AttachLegacyHint(envelope, Returned);
        }

        internal static void AttachLegacyHint(JObject envelope, object returned)
        {
            JObject value;
            try { value = returned as JObject ?? (returned == null ? null : JObject.FromObject(returned)); } catch { return; }
            foreach (var name in new[] { "passed", "ok", "success" })
                if (value?[name]?.Type == JTokenType.Boolean && value.Value<bool>(name) == false)
                {
                    envelope["task_contract_hint"] = "The legacy result reports " + name + "=false. Use result_contract=task_v1 and McpTaskResult for explicit task outcomes.";
                    return;
                }
        }
    }
}
