using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Scripting;
using RvtMcp.Server.Bake;

namespace RvtMcp.Server
{
    internal static class SendCodeCore
    {
        internal static readonly string[] EnhancedArguments = { "data", "modules", "compile_only", "result_contract", "usage_context" };
        internal static Func<BakeDb> StoreFactory = () => new BakeDb(new BakePaths());

        internal static void ValidateOriginalRequest(JsonNode parameters)
        {
            if (parameters == null) return;
            using var document = JsonDocument.Parse(parameters.ToJsonString(), new JsonDocumentOptions { MaxDepth = ScriptLimits.JsonDepth + 4 });
            void Visit(JsonElement value)
            {
                if (value.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in value.EnumerateObject())
                    {
                        if (!names.Add(property.Name))
                            throw new ScriptContractException(ScriptErrorCodes.InputInvalid, "Duplicate script request properties are not allowed.");
                        Visit(property.Value);
                    }
                }
                else if (value.ValueKind == JsonValueKind.Array)
                    foreach (var item in value.EnumerateArray()) Visit(item);
            }
            Visit(document.RootElement);
        }

        internal static ScriptInput ReadArguments(IDictionary<string, JsonElement> args)
        {
            string Text(string key, string fallback = null)
            {
                if (!args.TryGetValue(key, out var value)) return fallback;
                if (value.ValueKind != JsonValueKind.String)
                    throw new ScriptContractException(ScriptErrorCodes.InputInvalid, "Script argument requires a string: " + key);
                return value.GetString();
            }
            var data = args.TryGetValue("data", out var raw) ? ScriptData.FromJson(raw.GetRawText()) : ScriptData.Absent;
            var roots = Array.Empty<string>();
            if (args.TryGetValue("modules", out var modules) && modules.ValueKind != JsonValueKind.Null)
            {
                if (modules.ValueKind != JsonValueKind.Array || modules.EnumerateArray().Any(m => m.ValueKind != JsonValueKind.String))
                    throw new ScriptContractException(ScriptErrorCodes.InputInvalid, "Modules must be a string array.");
                roots = modules.EnumerateArray().Select(m => m.GetString()).ToArray();
            }
            var compileOnly = false;
            if (args.TryGetValue("compile_only", out var compile))
            {
                if (compile.ValueKind != JsonValueKind.True && compile.ValueKind != JsonValueKind.False)
                    throw new ScriptContractException(ScriptErrorCodes.InputInvalid, "compile_only must be a boolean.");
                compileOnly = compile.GetBoolean();
            }
            return new ScriptInput(Text("code"), data, roots, compileOnly, Text("result_contract", ScriptModes.Legacy), Text("usage_context", ScriptModes.Unknown));
        }

        internal static async Task<string> SendAsync(ScriptInput input, IReadOnlyList<ScriptModuleRevision> modules = null)
        {
            if (!input.RequiresCapability) return await ToolGateway.CallAsync("send_code_to_revit", new { code = input.Code });
            try
            {
                if (modules == null && input.Modules.Count != 0)
                {
                    using var db = StoreFactory();
                    modules = new CodeModuleStore(db).Resolve(input.Modules).Modules;
                }
                var result = await SendResolvedAsync(input, modules);
                return result.ToString(Formatting.Indented);
            }
            catch (ScriptContractException ex) { return "Error: " + ex.ErrorCode + ": " + ex.Message; }
            catch (Exception ex) { return "Error: " + ex.Message; }
        }

        internal static Task<JObject> SendResolvedAsync(ScriptInput input, IReadOnlyList<ScriptModuleRevision> modules, ScriptRuntimeMetadata metadata = null)
        {
            var request = new ScriptWireRequest(input, modules);
            return ToolGateway.SendToRevit("send_code_to_revit", request.Parameters(), script: metadata ?? request.Metadata());
        }

        internal static string SafeLogArguments(ScriptInput input)
            => new JObject {
                ["code_hash"] = BakeRedactor.HashBody(input.Code), ["code_length"] = input.Code.Length,
                ["script_code_sha256"] = ScriptIdentity.CodeSha256(input.Code), ["data_hash"] = input.Data.Hash,
                ["data_bytes"] = input.Data.ByteCount, ["modules"] = new JArray(input.Modules),
                ["result_contract"] = input.ResultContract, ["usage_context"] = input.UsageContext
            }.ToString(Formatting.None);
    }
}
