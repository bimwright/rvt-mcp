using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Scripting;
using RvtMcp.Server.Bake;

namespace RvtMcp.Server
{
    internal sealed class ModuleValidationException : Exception
    {
        internal JObject Receipt { get; }
        internal ModuleValidationException(string error, JObject receipt) : base(error) { Receipt = receipt; }
    }

    internal sealed class CodeModuleToolCore
    {
        private readonly CodeModuleStore _store;
        private readonly Func<ScriptInput, IReadOnlyList<ScriptModuleRevision>, ScriptRuntimeMetadata, Task<JObject>> _send;
        internal CodeModuleToolCore(CodeModuleStore store,
            Func<ScriptInput, IReadOnlyList<ScriptModuleRevision>, ScriptRuntimeMetadata, Task<JObject>> send = null)
        { _store = store; _send = send ?? SendCodeCore.SendResolvedAsync; }

        internal async Task<JObject> SaveAsync(string name, string code, string description, string[] requires, string expectedHash, CancellationToken cancellation = default)
        {
            CheckWritePolicy("save_code_module");
            var logicalId = Guid.NewGuid().ToString("N"); JObject validationReceipt = null;
            var result = await _store.SaveAsync(name, code, description, requires, expectedHash, async (snapshot, token) => {
                token.ThrowIfCancellationRequested();
                var input = new ScriptInput("return null;", modules: snapshot.Roots, compileOnly: true, usageContext: ScriptModes.Test);
                var request = new ScriptWireRequest(input, snapshot.Modules); var metadata = request.Metadata();
                metadata.EntryTool = "revit_save_code_module"; metadata.ExecutionKind = "module_compile"; metadata.ParentLogicalCallId = logicalId;
                validationReceipt = await _send(input, snapshot.Modules, metadata).ConfigureAwait(false);
                return ValidateCompilation(validationReceipt, metadata, snapshot, name);
            }, cancellation).ConfigureAwait(false);
            var response = new JObject { ["success"] = true, ["saved"] = result.Changed, ["unchanged"] = !result.Changed,
                ["logical_call_id"] = logicalId, ["module"] = Describe(result.Record),
                ["validation"] = new JObject { ["compile"] = "passed", ["module_manifest_hash"] = result.ValidationManifestHash,
                    ["host_year"] = result.Validation.HostYear, ["runtime_identity"] = result.Validation.RuntimeIdentity,
                    ["native_verified"] = false, ["runtime_observed"] = false } };
            if (validationReceipt != null)
            {
                var receipt = new JObject(); SafetyResponseMetadata.Attach(receipt, validationReceipt);
                response["compile_receipt"] = receipt; ToolGateway.CarryOnce(validationReceipt, response);
            }
            return response;
        }

        internal JObject List(string nameFilter, bool includeCode, int startIndex, int limit)
        {
            var page = _store.ReadPage(nameFilter, startIndex, limit);
            var rows = new JArray(page.Items.Select(item => {
                var row = Describe(item.Record); row["dependency_status"] = item.DependencyStatus;
                if (includeCode) row["code"] = item.Record.Revision.Code;
                return row;
            }));
            var next = (long)startIndex + rows.Count;
            return new JObject { ["success"] = true, ["modules"] = rows, ["total"] = page.TotalCount,
                ["start_index"] = startIndex, ["limit"] = limit, ["returned"] = rows.Count,
                ["has_more"] = next < page.TotalCount, ["next_start_index"] = next < page.TotalCount ? new JValue(next) : JValue.CreateNull() };
        }

        internal JObject Delete(string name, string expectedHash)
        {
            CheckWritePolicy("delete_code_module"); _store.Delete(name, expectedHash);
            return new JObject { ["success"] = true, ["deleted"] = true, ["name"] = name };
        }

        internal static void CheckWritePolicy(string command)
        {
            var config = ServerState.Config ?? new RvtMcpConfig();
            var rejection = config.ReadOnlyOrDefault ? "READ_ONLY: module storage mutations are disabled for this session."
                : !config.EnableSendCodeOrDefault ? "SEND_CODE_DISABLED: module storage mutations are disabled for this session." : null;
            if (rejection != null) throw new InvalidOperationException(rejection);
        }

        internal static JObject Describe(CodeModuleRecord record) => new JObject {
            ["name"] = record.Revision.Name, ["hash"] = record.Revision.Hash, ["description"] = record.Description,
            ["code_sha256"] = record.Revision.CodeSha256, ["normalized_byte_count"] = ScriptIdentity.Utf8Bytes(record.Revision.Code),
            ["requires"] = new JArray(record.Revision.Requires), ["byte_count"] = ScriptIdentity.Utf8Bytes(record.Revision.Code),
            ["signatures"] = JArray.Parse(record.SignaturesJson), ["compiled_host"] = JObject.Parse(record.CompiledHostJson), ["updated_at"] = record.UpdatedAt };

        private static CodeModuleCompilation ValidateCompilation(JObject receipt, ScriptRuntimeMetadata metadata, CodeModuleSnapshot snapshot, string name)
        {
            ModuleValidationException Reject(string message) => new ModuleValidationException(message, receipt);
            if (receipt == null || ToolGateway.IsFailureOrPending(receipt))
                throw Reject(receipt?.Value<string>("error") ?? "SCRIPT_COMPILE_FAILED: native compilation was not confirmed; no module was saved.");
            var script = receipt["_script"] as JObject; var operation = receipt["_operation"] as JObject;
            var host = receipt["compile_host"] as JObject; var target = receipt["_target"] as JObject;
            var task = receipt["_task"] as JObject;
            var signatures = (receipt["module_signatures"] as JObject)?[name] as JArray;
            bool True(JToken token) => token?.Type == JTokenType.Boolean && token.Value<bool>();
            bool False(JToken token) => token?.Type == JTokenType.Boolean && !token.Value<bool>();
            string Text(JObject obj, string key) => obj?[key]?.Type == JTokenType.String ? obj.Value<string>(key) : null;
            int? Number(JObject obj, string key) => obj?[key]?.Type == JTokenType.Integer && int.TryParse(obj[key].ToString(), out var value) ? value : (int?)null;
            if (!True(receipt["compiled"]) || !True(receipt["compile_only"]) || !False(receipt["executed"])
                || !True(script?["compile_attempted"]) || !False(script?["invoke_started"])
                || Text(script,"script_stage") != "compiled" || Text(script,"mode") != "compile_only"
                || Text(script,"logical_call_id") != metadata.LogicalCallId || Text(script,"parent_logical_call_id") != metadata.ParentLogicalCallId
                || Text(script,"execution_kind") != "module_compile" || Text(script,"entry_tool") != metadata.EntryTool
                || Text(script,"script_code_sha256") != metadata.CodeSha256 || Text(script,"module_manifest_hash") != snapshot.ManifestHash
                || Text(script,"usage_context") != ScriptModes.Test || Text(script,"result_contract") != ScriptModes.Legacy
                || script["data_hash"] != null && script["data_hash"].Type != JTokenType.Null
                || Text(task,"contract") != ScriptModes.Legacy || Text(task,"status") != "not_reported"
                || Text(operation,"execution") != "completed" || !True(operation?["complete"]) || !Guid.TryParse(Text(operation,"operationId"), out _)
                || operation["mutationApplied"] == null || !(operation["mutationApplied"].Type == JTokenType.Null || False(operation["mutationApplied"]))
                || (Text(operation,"outcome") != "unknown" && Text(operation,"outcome") != "not_started")
                || Text(host,"identity_basis") != "loaded_runtime" || string.IsNullOrWhiteSpace(Text(host,"runtime_identity"))
                || Number(host,"host_year") is not int year || year < 2022 || year > 2027
                || Number(host,"process_id") is not int pid || pid <= 0
                || year != Number(target,"host_year") || pid != Number(target,"pid")
                || signatures == null || signatures.Count > 200 || signatures.Any(s => s.Type != JTokenType.String || s.Value<string>().Length > 512))
                throw Reject("SCRIPT_COMPILE_FAILED: the native compile-only receipt is incomplete or mismatched; no module was saved.");
            return new CodeModuleCompilation(host.Value<int>("host_year"), host.Value<string>("runtime_identity"), signatures.Values<string>());
        }
    }
}
