using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;

namespace RvtMcp.Scripting
{
    /// <summary>Bounded script receipts and hash-only audit fields, shared by host and gateway.</summary>
    public static class ScriptTelemetry
    {
        private static readonly string[] ScriptFields = { "mode", "logical_call_id", "parent_logical_call_id", "entry_tool", "execution_kind",
            "usage_context", "context_basis", "script_code_sha256", "module_manifest_hash", "data_hash", "result_contract", "entrypoint_mode",
            "script_stage", "compile_attempted", "invoke_started", "compile_elapsed_ms", "invoke_elapsed_ms",
            "data_bytes", "agent_code_bytes", "wire_request_bytes", "telemetry_status" };

        public static JObject BoundScript(JObject source, bool compact = false)
        {
            if (source == null) return null;
            var result = new JObject();
            var keys = compact ? new[] { "mode", "logical_call_id", "script_stage", "compile_attempted", "invoke_started", "telemetry_status" } : ScriptFields;
            foreach (var key in keys)
                if (source[key] is JValue value)
                    result[key] = value.Type == JTokenType.String ? new JValue(Safe(value.Value<string>(), 128)) : value.DeepClone();
            return result;
        }

        public static JObject BoundTask(JObject source, bool compact = false)
        {
            if (source == null) return null;
            var result = new JObject { ["contract"] = Safe(source.Value<string>("contract"), 32), ["status"] = Safe(source.Value<string>("status"), 32) };
            if (source["error"] is JObject error)
            {
                var code = error.Value<string>("code");
                var limit = compact ? 64 : 128;
                result["error"] = new JObject { ["code"] = Safe(code, limit) };
                if ((code?.Length ?? 0) > limit || error.Value<bool?>("code_truncated") == true)
                    result["error"]["code_truncated"] = true;
                if (!compact && error["message"] != null) result["error"]["message"] = Safe(error.Value<string>("message"), 512);
            }
            return result;
        }

        public static string Safe(string text, int limit)
        {
            var safe = McpResponsePrivacy.RedactErrorForResponse(text);
            return safe != null && safe.Length > limit ? safe.Substring(0, limit) : safe;
        }

        public static JObject Create(JObject metadata, string parameters)
        {
            var result = BoundScript(metadata) ?? new JObject();
            JObject args;
            try { args = JObject.Parse(parameters ?? "{}"); } catch { args = new JObject(); }
            var compile = args["compile_only"]?.Type == JTokenType.Boolean && args.Value<bool>("compile_only");
            result["mode"] = compile ? "compile_only" : "execute";
            result["result_contract"] = SafeMode(args["result_contract"], ScriptModes.Legacy, ScriptModes.TaskV1) ?? ScriptModes.Legacy;
            result["context_basis"] = "declared";
            result["entrypoint_mode"] = args["data_json"] == null ? ScriptModes.App : ScriptModes.AppData;
            result["agent_code_bytes"] = ScriptIdentity.Utf8Bytes(args["code"]?.Type == JTokenType.String ? args.Value<string>("code") : "");
            result["data_bytes"] = ScriptIdentity.Utf8Bytes(args["data_json"]?.Type == JTokenType.String ? args.Value<string>("data_json") : "");
            return result;
        }

        public static void AttachPending(JObject envelope, JObject script)
        {
            if (script == null) return;
            var receipt = BoundScript(script);
            receipt["script_stage"] = envelope["_operation"]?.Value<string>("execution") == "completed" ? "preflight" : "pending";
            receipt.Remove("compile_attempted"); receipt.Remove("invoke_started");
            envelope["_script"] = receipt;
            var pending = envelope["_operation"]?.Value<string>("execution") != "completed";
            if (!pending) { receipt["compile_attempted"] = false; receipt["invoke_started"] = false; }
            envelope["_task"] = new JObject { ["contract"] = script.Value<string>("result_contract") ?? ScriptModes.Legacy,
                ["status"] = pending && script.Value<string>("result_contract") == ScriptModes.TaskV1 ? "unknown" : "not_reported" };
        }

        public static JObject LogFields(JObject envelope)
        {
            var result = BoundScript(envelope?["_script"] as JObject) ?? new JObject();
            var operation = envelope?["_operation"] as JObject;
            if (operation?["operationId"] != null) result["operation_id"] = operation["operationId"].DeepClone();
            if (envelope?["_task"] is JObject task)
            {
                result["task_status"] = task["status"]?.DeepClone();
                result["task_error_code"] = task["error"]?["code"]?.DeepClone();
                if (task["error"]?.Value<bool?>("code_truncated") == true) result["task_error_code_truncated"] = true;
            }
            return result;
        }

        public static JObject SafeArguments(string parameters, string code = null)
        {
            JObject args;
            try { args = JObject.Parse(parameters ?? "{}"); } catch { args = new JObject(); }
            code = code ?? (args["code"]?.Type == JTokenType.String ? args.Value<string>("code") : "") ?? "";
            var result = new JObject { ["code_hash"] = BakeRedactor.HashBody(code), ["code_length"] = code.Length };
            bool enhanced;
            try { enhanced = ScriptWireRequest.HasEnhancedOptions(args); } catch { enhanced = true; }
            if (!enhanced) return result;
            result["script_code_sha256"] = ScriptIdentity.CodeSha256(code);
            result["result_contract"] = SafeMode(args["result_contract"], ScriptModes.Legacy, ScriptModes.TaskV1);
            result["usage_context"] = SafeMode(args["usage_context"], ScriptModes.Project, ScriptModes.Test, ScriptModes.Unknown);
            if (args["compile_only"]?.Type == JTokenType.Boolean) result["compile_only"] = args["compile_only"].DeepClone();
            if (args["modules"] is JArray roots) result["modules"] = new JArray(roots.Where(IsName).Take(ScriptLimits.ModuleCount));
            if (args["resolved_modules"] is JArray modules)
                result["module_manifest"] = new JArray(modules.OfType<JObject>().Where(m => IsName(m["name"]) && IsHash(m["hash"])).Take(ScriptLimits.ModuleCount).Select(m => new JObject {
                    ["name"] = m["name"]?.DeepClone(), ["hash"] = m["hash"]?.DeepClone() }));
            try
            {
                var data = ScriptData.FromJson(args.Value<string>("data_json"));
                result["data_hash"] = data.Hash; result["data_bytes"] = data.ByteCount;
            }
            catch { result["data_invalid"] = true; }
            return result;
        }

        private static string SafeMode(JToken token, params string[] allowed)
            => token?.Type == JTokenType.String && allowed.Contains(token.Value<string>(), StringComparer.Ordinal) ? token.Value<string>() : null;
        private static bool IsName(JToken token)
        {
            if (token?.Type != JTokenType.String) return false;
            try { ScriptModuleRevision.ValidateName(token.Value<string>()); return true; } catch { return false; }
        }
        private static bool IsHash(JToken token) => token?.Type == JTokenType.String && token.Value<string>().Length == 64
            && token.Value<string>().All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');

        public static JObject LogOperation(JObject source)
        {
            if (source == null) return null;
            var result = new JObject();
            foreach (var name in new[] { "operationId", "execution", "outcome", "mutationApplied", "complete", "requiresReview", "activeDocumentChanged" })
                if (source[name] is JValue value)
                    result[name] = value.Type == JTokenType.String ? new JValue(Safe(value.Value<string>(), 128)) : value.DeepClone();
            return result;
        }
    }
}
