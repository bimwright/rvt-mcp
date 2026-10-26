using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Newtonsoft.Json;

namespace RvtMcp.Scripting
{
    public static class ScriptLimits
    {
        public const int ProtocolVersion = 1;
        public const int IdentityVersion = 1;
        public const int DataBytes = 512 * 1024;
        public const int RawDataBytes = 1024 * 1024;
        public const int ModuleBytes = 64 * 1024;
        public const int ModuleCount = 32;
        public const int ResolvedModuleBytes = 256 * 1024;
        public const int DependencyDepth = 16;
        public const int JsonDepth = 64;
        public const int WireBytes = 1024 * 1024;
    }

    public static class ScriptErrorCodes
    {
        public const string CapabilityRequired = "SCRIPT_CAPABILITY_REQUIRED";
        public const string InputInvalid = "SCRIPT_INPUT_INVALID";
        public const string DataTooLarge = "SCRIPT_DATA_TOO_LARGE";
        public const string RequestTooLarge = "REQUEST_TOO_LARGE";
        public const string EntrypointMismatch = "SCRIPT_ENTRYPOINT_MISMATCH";
        public const string CompileFailed = "SCRIPT_COMPILE_FAILED";
        public const string TaskResultRequired = "TASK_RESULT_REQUIRED";
        public const string TaskFailed = "TASK_FAILED";
        public const string ModuleNotFound = "MODULE_NOT_FOUND";
        public const string ModuleCycle = "MODULE_CYCLE";
        public const string ModuleLimitExceeded = "MODULE_LIMIT_EXCEEDED";
        public const string ModuleConflict = "MODULE_CONFLICT";
        public const string ModuleChanged = "MODULE_CHANGED";
        public const string ModuleInUse = "MODULE_IN_USE";
        public const string ModuleStoreCorrupt = "MODULE_STORE_CORRUPT";
        public const string SourceNotStorable = "SCRIPT_SOURCE_NOT_STORABLE";
    }

    public sealed class ScriptContractException : ArgumentException
    {
        public string ErrorCode { get; }
        public ScriptContractException(string code, string message) : base(message) { ErrorCode = code; }
    }

    public static class ScriptModes
    {
        public const string Legacy = "legacy";
        public const string TaskV1 = "task_v1";
        public const string Project = "project";
        public const string Test = "test";
        public const string Unknown = "unknown";
        public const string App = "app";
        public const string AppData = "app_data";

        internal static void Check(string value, params string[] allowed)
        {
            if (!allowed.Contains(value, StringComparer.Ordinal))
                throw new ScriptContractException(ScriptErrorCodes.InputInvalid, "Unsupported script mode.");
        }
    }

    /// <summary>Raw JSON is retained so numeric precision is never lost through a JToken/double conversion.</summary>
    public sealed class ScriptData
    {
        public static readonly ScriptData Absent = new ScriptData(null);
        public bool IsProvided => Json != null;
        public string Json { get; }
        public string CanonicalJson { get; }
        public string Hash { get; }
        public int ByteCount { get; }

        private ScriptData(ScriptJsonDocument document)
        {
            Json = document?.CompactJson;
            CanonicalJson = document?.CanonicalJson;
            Hash = document == null ? null : ScriptIdentity.Sha256(CanonicalJson);
            ByteCount = document == null ? 0 : ScriptIdentity.Utf8Bytes(Json);
        }

        public static ScriptData FromJson(string json)
        {
            if (json == null) return Absent;
            // Bound work before JSON parsing or arbitrary-precision exponent normalization.
            if (json.Length > ScriptLimits.RawDataBytes || ScriptIdentity.Utf8Bytes(json) > ScriptLimits.RawDataBytes)
                throw new ScriptContractException(ScriptErrorCodes.DataTooLarge, "Raw script data exceeds its UTF-8 budget.");
            var document = ScriptJson.Parse(json, ScriptLimits.DataBytes);
            if (document.IsNull) return Absent;
            if (!document.IsObject)
                throw new ScriptContractException(ScriptErrorCodes.InputInvalid, "Script data requires a JSON object root.");
            return new ScriptData(document);
        }
    }

    public sealed class ScriptInput
    {
        public string Code { get; }
        public ScriptData Data { get; }
        public ReadOnlyCollection<string> Modules { get; }
        public bool CompileOnly { get; }
        public string ResultContract { get; }
        public string UsageContext { get; }
        public string EntrypointMode => Data.IsProvided ? ScriptModes.AppData : ScriptModes.App;
        public bool RequiresCapability => Data.IsProvided || Modules.Count != 0 || CompileOnly ||
                                          ResultContract != ScriptModes.Legacy || UsageContext != ScriptModes.Unknown;

        public ScriptInput(string code, ScriptData data = null, IEnumerable<string> modules = null,
            bool compileOnly = false, string resultContract = ScriptModes.Legacy, string usageContext = ScriptModes.Unknown)
        {
            if (string.IsNullOrWhiteSpace(code))
                throw new ScriptContractException(ScriptErrorCodes.InputInvalid, "Script code is required.");
            ScriptModes.Check(resultContract, ScriptModes.Legacy, ScriptModes.TaskV1);
            ScriptModes.Check(usageContext, ScriptModes.Project, ScriptModes.Test, ScriptModes.Unknown);
            var roots = (modules ?? Enumerable.Empty<string>()).ToArray();
            foreach (var name in roots) ScriptModuleRevision.ValidateName(name);
            if (roots.Distinct(StringComparer.Ordinal).Count() != roots.Length)
                throw new ScriptContractException(ScriptErrorCodes.InputInvalid, "Duplicate module roots are not allowed.");
            if (roots.Length > ScriptLimits.ModuleCount)
                throw new ScriptContractException(ScriptErrorCodes.ModuleLimitExceeded, "Too many module roots.");
            Code = code;
            Data = data ?? ScriptData.Absent;
            Modules = Array.AsReadOnly(roots);
            CompileOnly = compileOnly;
            ResultContract = resultContract;
            UsageContext = usageContext;
        }
    }

    /// <summary>Gateway-owned envelope fields; never merged into caller data.</summary>
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class ScriptRuntimeMetadata
    {
        public ScriptRuntimeMetadata(string code) { CodeSha256 = ScriptIdentity.CodeSha256(code); }

        [JsonProperty("script_protocol")] public int Protocol => ScriptLimits.ProtocolVersion;
        [JsonProperty("logical_call_id")] public string LogicalCallId { get; set; }
        [JsonProperty("entry_tool")] public string EntryTool { get; set; }
        [JsonProperty("execution_kind")] public string ExecutionKind { get; set; }
        [JsonProperty("usage_context")] public string UsageContext { get; set; }
        [JsonProperty("script_code_sha256")] public string CodeSha256 { get; }
        [JsonProperty("module_manifest_hash")] public string ModuleManifestHash { get; set; }
        [JsonProperty("data_hash")] public string DataHash { get; set; }
        [JsonProperty("parent_logical_call_id", NullValueHandling = NullValueHandling.Ignore)] public string ParentLogicalCallId { get; set; }
        [JsonProperty("script_stage", NullValueHandling = NullValueHandling.Ignore)] public string Stage { get; set; }
        [JsonProperty("compile_attempted", NullValueHandling = NullValueHandling.Ignore)] public bool? CompileAttempted { get; set; }
        [JsonProperty("invoke_started", NullValueHandling = NullValueHandling.Ignore)] public bool? InvokeStarted { get; set; }
    }
}
