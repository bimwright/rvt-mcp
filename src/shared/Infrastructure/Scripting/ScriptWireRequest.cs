using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Scripting
{
    public static class ScriptProtocol
    {
        public const string Capability = "send_code_v2";
        public static bool IsReady => true;
        public static JObject Describe() => new JObject { ["capability"] = Capability,
            ["version"] = ScriptLimits.ProtocolVersion, ["supported"] = IsReady };
    }

    public sealed class ScriptWireRequest
    {
        public ScriptInput Input { get; }
        public ReadOnlyCollection<ScriptModuleRevision> Modules { get; }

        public ScriptWireRequest(ScriptInput input, IEnumerable<ScriptModuleRevision> modules = null)
        {
            Input = input ?? throw new ArgumentNullException(nameof(input));
            Modules = Array.AsReadOnly((modules ?? Enumerable.Empty<ScriptModuleRevision>()).ToArray());
            if (Modules.Count > ScriptLimits.ModuleCount || Modules.Sum(m => (long)ScriptIdentity.Utf8Bytes(m.Code)) > ScriptLimits.ResolvedModuleBytes)
                throw new ScriptContractException(ScriptErrorCodes.ModuleLimitExceeded, "Resolved modules exceed their budget.");
            if (input.Modules.Any(name => !Modules.Any(m => m.Name == name)))
                throw new ScriptContractException(ScriptErrorCodes.ModuleNotFound, "No resolved snapshot is available for a requested module.");
        }

        public JObject Parameters()
        {
            var result = new JObject {
                ["code"] = Input.Code, ["script_protocol"] = ScriptLimits.ProtocolVersion,
                ["modules"] = new JArray(Input.Modules), ["compile_only"] = Input.CompileOnly,
                ["result_contract"] = Input.ResultContract, ["usage_context"] = Input.UsageContext,
                ["resolved_modules"] = new JArray(Modules.Select(m => new JObject {
                    ["name"] = m.Name, ["code"] = m.Code, ["hash"] = m.Hash, ["requires"] = new JArray(m.Requires) }))
            };
            // JSON text survives the legacy transport's JObject reader without rounding user numbers.
            if (Input.Data.IsProvided) result["data_json"] = Input.Data.Json;
            return result;
        }

        public ScriptRuntimeMetadata Metadata()
            => new ScriptRuntimeMetadata(Input.Code) {
                LogicalCallId = Guid.NewGuid().ToString("N"), EntryTool = "revit_send_code_to_revit",
                ExecutionKind = Input.CompileOnly ? "compile_only" : "script", UsageContext = Input.UsageContext,
                ModuleManifestHash = ScriptIdentity.ManifestHash(Modules.Select(m => new ScriptModuleManifestEntry(m.Name, m.Hash, m.Requires))),
                DataHash = Input.Data.Hash
            };

        public void ValidateMetadata(JObject metadata)
        {
            var expected = Metadata();
            var moduleCompile = metadata?.Value<string>("execution_kind") == "module_compile";
            var validRoute = moduleCompile
                ? Input.CompileOnly && !Input.Data.IsProvided && Input.Modules.Count == 1 && Input.ResultContract == ScriptModes.Legacy && Input.UsageContext == ScriptModes.Test
                    && metadata.Value<string>("entry_tool") == "revit_save_code_module"
                    && Guid.TryParse(metadata.Value<string>("parent_logical_call_id"), out var parent)
                    && Guid.TryParse(metadata.Value<string>("logical_call_id"), out var child) && parent != child
                : metadata?.Value<string>("entry_tool") == expected.EntryTool && metadata?.Value<string>("execution_kind") == expected.ExecutionKind;
            if (metadata == null || metadata.Value<int?>("script_protocol") != ScriptLimits.ProtocolVersion ||
                !Guid.TryParse(metadata.Value<string>("logical_call_id"), out _) ||
                metadata.Value<string>("script_code_sha256") != expected.CodeSha256 ||
                metadata.Value<string>("module_manifest_hash") != expected.ModuleManifestHash ||
                metadata.Value<string>("data_hash") != expected.DataHash ||
                !validRoute ||
                metadata.Value<string>("usage_context") != Input.UsageContext)
                throw new ScriptContractException(ScriptErrorCodes.InputInvalid, "Script runtime identity does not match its source/input snapshot.");
        }

        public static bool HasEnhancedOptions(JObject parameters)
            => parameters["script_protocol"] != null || parameters["data_json"] != null ||
               parameters["data"] != null && parameters["data"].Type != JTokenType.Null ||
               NonEmptyArrayOption(parameters["modules"]) || NonEmptyArrayOption(parameters["resolved_modules"]) ||
               parameters["compile_only"] != null && (parameters["compile_only"].Type != JTokenType.Boolean || parameters.Value<bool>("compile_only")) ||
               parameters["result_contract"] != null && parameters.Value<string>("result_contract") != ScriptModes.Legacy ||
               parameters["usage_context"] != null && parameters.Value<string>("usage_context") != ScriptModes.Unknown;

        private static bool NonEmptyArrayOption(JToken value)
            => value != null && value.Type != JTokenType.Null && (!(value is JArray array) || array.Count != 0);

        public static ScriptWireRequest Parse(string parameters)
        {
            var obj = JObject.Parse(parameters, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (obj["script_protocol"]?.Type != JTokenType.Integer || obj.Value<int?>("script_protocol") != ScriptLimits.ProtocolVersion)
                throw new ScriptContractException(ScriptErrorCodes.CapabilityRequired, "Script protocol version is required.");
            if (obj["code"]?.Type != JTokenType.String || obj["data"] != null ||
                obj["compile_only"] != null && obj["compile_only"].Type != JTokenType.Boolean ||
                obj["result_contract"] != null && obj["result_contract"].Type != JTokenType.String ||
                obj["usage_context"] != null && obj["usage_context"].Type != JTokenType.String ||
                obj["modules"] != null && (!(obj["modules"] is JArray rootNames) || rootNames.Any(n => n.Type != JTokenType.String)) ||
                obj["resolved_modules"] != null && !(obj["resolved_modules"] is JArray))
                throw new ScriptContractException(ScriptErrorCodes.InputInvalid, "Invalid internal script request types.");
            if (obj["data_json"] != null && obj["data_json"].Type != JTokenType.String)
                throw new ScriptContractException(ScriptErrorCodes.InputInvalid, "Internal script data must be JSON text.");
            var roots = (obj["modules"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>();
            var input = new ScriptInput(obj.Value<string>("code"), ScriptData.FromJson(obj.Value<string>("data_json")), roots,
                obj.Value<bool?>("compile_only") ?? false, obj.Value<string>("result_contract") ?? ScriptModes.Legacy,
                obj.Value<string>("usage_context") ?? ScriptModes.Unknown);
            var modules = new List<ScriptModuleRevision>();
            foreach (var row in obj["resolved_modules"] as JArray ?? new JArray())
            {
                if (!(row is JObject) || row["name"]?.Type != JTokenType.String || row["code"]?.Type != JTokenType.String ||
                    row["hash"]?.Type != JTokenType.String || !(row["requires"] is JArray names) || names.Any(n => n.Type != JTokenType.String))
                    throw new ScriptContractException(ScriptErrorCodes.InputInvalid, "Invalid resolved module snapshot.");
                var module = new ScriptModuleRevision(row.Value<string>("name"), row.Value<string>("code"),
                    (row["requires"] as JArray)?.Values<string>());
                if (module.Hash != row.Value<string>("hash"))
                    throw new ScriptContractException(ScriptErrorCodes.ModuleChanged, "Resolved module content does not match its pinned revision.");
                modules.Add(module);
            }
            return new ScriptWireRequest(input, modules);
        }
    }
}
