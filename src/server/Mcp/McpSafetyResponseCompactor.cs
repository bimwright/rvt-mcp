using System;
using System.Linq;
using System.Text;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    internal static class McpSafetyResponseCompactor
    {
        // Receipt-local bounds cannot account for MCP text escaping or a smaller user cap.
        // Shed queryable detail first; never exchange outcome/gate facts for a success summary.
        internal static CallToolResult Fit(JObject data, bool failed, int limit)
        {
            CallToolResult Result() => new CallToolResult
            {
                Content = new[] { new TextContentBlock { Text = data.ToString(Formatting.None) } }, IsError = failed
            };
            bool Fits() => Encoding.UTF8.GetByteCount(RuntimeToolFilter.Serialize(Result())) <= limit;
            if (Fits()) return Result();

            if (data["_task"] is JObject task) data["_task"] = RvtMcp.Scripting.ScriptTelemetry.BoundTask(task, compact: true);
            if (data["_script"] is JObject script) data["_script"] = RvtMcp.Scripting.ScriptTelemetry.BoundScript(script, compact: true);
            data.Remove("task_contract_hint");

            var failure = data["_failure"] as JObject;
            var operation = data["_operation"] as JObject;
            if (failure?["incidentIds"] is JArray ids)
            {
                while (ids.Count > 0 && !Fits())
                {
                    failure["incidentIdsOmitted"] = true;
                    ids.RemoveAt(ids.Count - 1);
                }
                if (ids.Count == 0) failure.Remove("incidentIds");
            }
            if (!Fits() && data["_report_captures"] is JArray captures)
            {
                data["_report_captures_truncated"] = true;
                while (captures.Count > 0 && !Fits()) captures.RemoveAt(captures.Count - 1);
                if (captures.Count == 0) data.Remove("_report_captures");
            }
            foreach (var key in new[] { "_changes", "_history" })
            {
                if (Fits()) return Result();
                data.Remove(key);
            }
            if (!Fits() && data["_report_capture"] != null)
            {
                data.Remove("_report_capture"); data["_report_captures_truncated"] = true;
            }
            foreach (var key in new[] { "hostInstanceId", "schemaVersion", "messageCount", "observationCoverage", "modelScopeKey", "requiresReview", "warningPolicy", "summaryTruncated" })
            {
                if (Fits()) return Result();
                failure?.Remove(key);
                operation?.Remove(key);
            }
            if (!Fits() && operation?["operationId"] != null && JToken.DeepEquals(failure?["operationId"], operation["operationId"]))
                failure.Remove("operationId"); // The same issued ID remains on the operation receipt.
            if (!Fits()) failure?.Remove("mutationApplied"); // Native outcome stays on _operation.
            if (!Fits() && data["error"]?.Type == JTokenType.String)
            {
                var error = data.Value<string>("error");
                var separator = error.IndexOfAny(new[] { ':', '\r', '\n' });
                data["error"] = error.Substring(0, Math.Min(separator < 0 ? error.Length : separator, 128));
            }
            if (!Fits()) data.Remove("error");
            if (!Fits()) data.Remove("response_compacted"); // incidentIdsOmitted still explicitly signals omitted evidence.
            if (!Fits() && data["target"] is JObject target) target.Remove("summaryTruncated");
            if (!Fits() && data["_task"]?["error"] is JObject taskError && taskError["code"]?.Type == JTokenType.String)
            {
                var code = taskError.Value<string>("code") ?? "";
                while (!Fits() && code.Length > 1)
                {
                    code = code.Substring(0, code.Length / 2);
                    taskError["code"] = code; taskError["code_truncated"] = true;
                }
            }
            // Stage facts remain in the pre-compaction audit. At the smallest client cap the wire keeps mode/identity/status.
            if (data["_script"] is JObject scriptReceipt)
            {
                foreach (var field in new[] { "script_stage", "compile_attempted", "invoke_started" })
                {
                    if (Fits()) return Result();
                    scriptReceipt.Remove(field);
                }
                if (!Fits() && data["_target"] is JObject actualTarget) actualTarget.Remove("target_id");
                if (!Fits() && JToken.DeepEquals(failure?["action"], operation?["outcome"])) failure?.Remove("action");
                if (!Fits() && data["_task"] is JObject taskReceipt) taskReceipt.Remove("contract");
                if (!Fits() && data["_task"]?["error"] != null)
                    data["_task"]["error"] = new JObject { ["omitted"] = true };
                if (!Fits() && data["_target"] is JObject targetStamp) targetStamp.Remove("host_year");
                if (!Fits() && data["_task"] is JObject minimalTask) minimalTask.Remove("error");
            }
            if (!Fits())
                throw new InvalidOperationException("The response cap cannot fit the operation safety receipt. Inspect the model; do not retry the operation.");
            return Result();
        }
    }
}
