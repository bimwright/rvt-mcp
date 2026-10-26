using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Scripting;

namespace RvtMcp.Server
{
    internal static class CodeModuleAudit
    {
        internal static bool IsTool(string tool) => tool == "save_code_module" || tool == "list_code_modules" || tool == "delete_code_module";
        internal static void ValidateArguments(string tool, IDictionary<string, JsonElement> arguments)
        {
            foreach (var item in arguments ?? new Dictionary<string, JsonElement>())
            {
                var value = item.Value;
                var valid = item.Key == "requires" ? value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String)
                    : item.Key == "include_code" ? value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False
                    : item.Key == "start_index" || item.Key == "limit" ? value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _)
                    : item.Key == "expected_hash" && tool == "save_code_module" ? value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.String
                    : new[] {"name", "code", "description", "expected_hash", "name_filter"}.Contains(item.Key) ? value.ValueKind == JsonValueKind.String : true;
                if (!valid) throw new ScriptContractException(ScriptErrorCodes.InputInvalid, "Invalid module argument type: " + item.Key);
            }
        }

        internal static string SafeArguments(string json)
        {
            var result = new JObject { ["module_arguments_redacted"] = true };
            try
            {
                var args = JObject.Parse(json ?? "{}"); var code = args["code"]?.Type == JTokenType.String ? args.Value<string>("code") : null;
                if (code != null) { result["code_hash"] = BakeRedactor.HashBody(code); result["code_length"] = code.Length; result["script_code_sha256"] = ScriptIdentity.CodeSha256(code); }
                if (args["name"]?.Type == JTokenType.String)
                { try { ScriptModuleRevision.ValidateName(args.Value<string>("name")); result["name"] = args["name"].DeepClone(); } catch { } }
                if (args["include_code"]?.Type == JTokenType.Boolean) result["include_code"] = args["include_code"].DeepClone();
            }
            catch { }
            return result.ToString(Formatting.None);
        }

        internal static string SafeResult(string json)
        {
            if (json == null) return null;
            try
            {
                var obj = JObject.Parse(json);
                if (obj["modules"] is JArray modules)
                    foreach (var row in modules.OfType<JObject>())
                        if (row.Remove("code")) row["code_omitted"] = true;
                return obj.ToString(Formatting.None);
            }
            catch { return "{\"module_result_omitted\":true}"; }
        }
    }
}
