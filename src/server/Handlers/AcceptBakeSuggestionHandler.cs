using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using RvtMcp.Plugin;
using RvtMcp.Plugin.ToolBaker;
using RvtMcp.Server.Bake;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server.Handlers
{
    public static class AcceptBakeSuggestionHandler
    {
        private const int DailyCondensationCap = 5;
        private const string MissingAnthropicMessage =
            "Adaptive bake accept for send_code clusters requires ANTHROPIC_API_KEY. " +
            "Set the env var and restart the MCP server. Cluster B/C accepts work without an API key.";

        private static readonly Regex ToolNamePattern = new Regex("^[a-z][a-z0-9_]{2,63}$", RegexOptions.Compiled);

        public static string Handle(
            BakeDb db,
            string id,
            string name,
            string outputChoice = "mcp_only",
            string paramsSchema = null,
            Func<string, string> envLookup = null,
            ICodeCondenser codeCondenser = null,
            DateTimeOffset? now = null)
        {
            return HandleAsync(db, id, name, outputChoice, paramsSchema, envLookup, codeCondenser, now).GetAwaiter().GetResult();
        }

        public static async Task<string> HandleAsync(
            BakeDb db,
            string id,
            string name,
            string outputChoice = "mcp_only",
            string paramsSchema = null,
            Func<string, string> envLookup = null,
            ICodeCondenser codeCondenser = null,
            DateTimeOffset? now = null,
            Func<JObject, Task<JObject>> pluginApply = null)
        {
            if (db == null)
                throw new ArgumentNullException(nameof(db));

            var suggestion = db.GetSuggestion(id);
            if (suggestion == null)
                return Failure("not_found", "Bake suggestion was not found.");

            var clock = now ?? DateTimeOffset.UtcNow;
            var payload = ParsePayload(suggestion.PayloadJson);

            if (!ToolNamePattern.IsMatch(name ?? string.Empty))
                return RecordFailure(db, suggestion, "invalid_name", "Tool name must use snake_case and start with a letter.", clock);
            if (!IsAllowedOutput(payload, outputChoice))
                return RecordFailure(db, suggestion, "invalid_output_choice", "Output choice is not available for this suggestion.", clock);
            if (!TryResolveParamsSchema(paramsSchema, payload, out var resolvedSchema, out var schemaError))
                return RecordFailure(db, suggestion, "invalid_params_schema", schemaError, clock);
            if (RegistryContainsName(db, name))
                return RecordFailure(
                    db,
                    suggestion,
                    "duplicate_tool_name",
                    "A baked tool with this name already exists in the server registry.",
                    clock,
                    new JObject { ["tool_name"] = name });

            var prepared = PrepareSourceRequest(suggestion, payload, name, outputChoice, resolvedSchema, envLookup, codeCondenser, clock);
            if (!prepared.Ok)
                return RecordFailure(db, suggestion, prepared.ErrorCode, prepared.Message, clock, prepared.Diagnostics);

            if (pluginApply == null)
                return RecordFailure(
                    db,
                    suggestion,
                    "plugin_apply_unavailable",
                    "Plugin apply_bake transport is not configured for this accept call.",
                    clock,
                    prepared.Request);

            JObject applyResult;
            try
            {
                applyResult = await pluginApply(prepared.Request).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return RecordFailure(
                    db,
                    suggestion,
                    "plugin_apply_failed",
                    ex.Message,
                    clock,
                    new JObject { ["exception_type"] = ex.GetType().FullName, ["request"] = prepared.Request });
            }

            if (applyResult == null || applyResult.Value<bool?>("success") != true)
            {
                if (ToolGateway.IsPendingOrUnknown(applyResult))
                    return RecordPending(db, suggestion, applyResult, clock);

                var notStarted = string.Equals(
                    (applyResult?["_operation"] as JObject)?.Value<string>("outcome"),
                    "not_started",
                    StringComparison.Ordinal);
                var code = (string)applyResult?["error_code"]
                    ?? (notStarted ? "plugin_apply_blocked" : "plugin_apply_failed");
                var message = (string)applyResult?["message"]
                    ?? (string)applyResult?["error"]
                    ?? "Plugin apply_bake failed.";
                return RecordFailure(db, suggestion, code, message, clock, applyResult ?? new JObject());
            }

            return RecordSuccess(db, suggestion, prepared.Request, applyResult, clock);
        }

        private static PreparedRequest PrepareSourceRequest(
            BakeSuggestionRecord suggestion,
            JObject payload,
            string name,
            string outputChoice,
            string paramsSchema,
            Func<string, string> envLookup,
            ICodeCondenser codeCondenser,
            DateTimeOffset now)
        {
            switch (suggestion.Source)
            {
                case "preset":
                    return PreparedRequest.Success(BuildRequest(name, suggestion, payload, outputChoice, paramsSchema, BuildPresetWrapper(name, payload)));
                case "macro":
                    return PreparedRequest.Success(BuildRequest(name, suggestion, payload, outputChoice, paramsSchema, BuildMacroWrapper(name, payload)));
                case "send_code":
                    return PrepareSendCode(suggestion, payload, name, outputChoice, paramsSchema, envLookup, codeCondenser, now);
                default:
                    return PreparedRequest.Failure("unsupported_source", "Unsupported bake suggestion source.", new JObject { ["source"] = suggestion.Source });
            }
        }

        private static PreparedRequest PrepareSendCode(
            BakeSuggestionRecord suggestion,
            JObject payload,
            string name,
            string outputChoice,
            string paramsSchema,
            Func<string, string> envLookup,
            ICodeCondenser codeCondenser,
            DateTimeOffset now)
        {
            if (!string.IsNullOrWhiteSpace((string)payload["condensed_code"]))
            {
                var cachedSchema = (string)payload["condensed_params_schema"] ?? paramsSchema;
                return PreparedRequest.Success(BuildRequest(name, suggestion, payload, outputChoice, cachedSchema, (string)payload["condensed_code"]));
            }

            envLookup ??= Environment.GetEnvironmentVariable;
            if (string.IsNullOrWhiteSpace(envLookup("ANTHROPIC_API_KEY")))
                return PreparedRequest.Failure("missing_anthropic_api_key", MissingAnthropicMessage, new JObject());
            if (CondensationAttemptsToday(payload, now) >= DailyCondensationCap)
                return PreparedRequest.Failure("condensation_daily_cap_exceeded", "Adaptive bake send_code condensation is capped at 5 attempts per day.", new JObject());
            if (codeCondenser == null)
                return PreparedRequest.Failure("llm_condensation_unavailable", "No send_code condenser is configured for this server process.", new JObject());

            var samples = ExtractCondensationSamples(payload);
            if (samples.Length == 0)
                return PreparedRequest.Failure("missing_condensation_samples", "No redacted send_code samples are available for this suggestion.", new JObject());

            var condensed = codeCondenser.Condense(samples);
            if (condensed == null || string.IsNullOrWhiteSpace(condensed.Code) || string.IsNullOrWhiteSpace(condensed.ParamsSchema))
                return PreparedRequest.Failure("llm_condensation_failed", "The send_code condenser did not return code and a parameter schema.", new JObject());

            AppendArray(payload, "condensation_attempts", new JObject { ["attempted_at"] = now.ToString("o"), ["ok"] = true });
            payload["condensed_code"] = condensed.Code;
            payload["condensed_params_schema"] = condensed.ParamsSchema;
            suggestion.PayloadJson = payload.ToString(Formatting.None);

            return PreparedRequest.Success(BuildRequest(name, suggestion, payload, outputChoice, condensed.ParamsSchema, condensed.Code));
        }

        private static string[] ExtractCondensationSamples(JObject payload)
        {
            var samples = new List<string>();
            if (payload["code_cache_samples"] is JArray legacy)
                samples.AddRange(legacy.Values<string>());

            var clusterMaterial = (string)payload["sample"]?["cluster_material"];
            if (!string.IsNullOrWhiteSpace(clusterMaterial))
                samples.Add(clusterMaterial);

            return samples
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => BakeRedactor.RedactForBake(s, redactResultFields: true))
                .Distinct(StringComparer.Ordinal)
                .Take(3)
                .ToArray();
        }

        private static JObject BuildRequest(string name, BakeSuggestionRecord suggestion, JObject payload, string outputChoice, string paramsSchema, string sourceCode)
        {
            var request = new JObject
            {
                ["suggestion_id"] = suggestion.Id,
                ["tool_name"] = name,
                ["display_name"] = ToDisplayName(name),
                ["description"] = suggestion.Description ?? suggestion.Title,
                ["source"] = suggestion.Source,
                ["output_choice"] = outputChoice,
                ["params_schema"] = paramsSchema,
                ["source_code"] = sourceCode,
                ["created_from_suggestion_id"] = suggestion.Id
            };
            if (suggestion.Source == "preset")
            {
                request["handler_tool"] = (string)payload["tool"] ?? string.Empty;
                request["fixed_args"] = BuildFixedArgs(payload);
            }
            if (suggestion.Source == "macro")
            {
                request["sequence"] = payload["sequence"] ?? new JArray((string)payload["tool"] ?? string.Empty);
            }
            return request;
        }

        private static string ToDisplayName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Baked Tool";

            return string.Join(" ", name.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => char.ToUpperInvariant(part[0]) + (part.Length > 1 ? part.Substring(1) : string.Empty)));
        }

        private static bool RegistryContainsName(BakeDb db, string name)
        {
            return db.ReadRegistryRecords().Any(r => string.Equals(r.Name, name, StringComparison.Ordinal));
        }

        private static string BuildPresetWrapper(string name, JObject payload)
        {
            var tool = (string)payload["tool"] ?? string.Empty;
            return BakedToolRuntimeSource.BuildPreset(tool, BuildFixedArgs(payload));
        }

        private static string BuildMacroWrapper(string name, JObject payload)
        {
            var sequence = ((JArray)payload["sequence"] ?? new JArray((string)payload["tool"] ?? string.Empty))
                .Values<string>()
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();
            return BakedToolRuntimeSource.BuildMacro(sequence);
        }

        private static JObject BuildFixedArgs(JObject payload)
        {
            var sample = payload["sample"] as JObject;
            var kinds = sample?["parameter_kinds"] as JObject;
            var fixedArgs = new JObject();
            if (kinds == null)
                return fixedArgs;

            foreach (var property in kinds.Properties())
                fixedArgs[property.Name] = DefaultForKind((string)property.Value);
            return fixedArgs;
        }

        private static JToken DefaultForKind(string kind)
        {
            switch (kind)
            {
                case "number":
                    return 0;
                case "bool":
                    return false;
                case "array":
                    return new JArray();
                case "object":
                    return new JObject();
                default:
                    return string.Empty;
            }
        }

        private static bool TryResolveParamsSchema(string overrideSchema, JObject payload, out string schema, out string error)
        {
            if (!string.IsNullOrWhiteSpace(overrideSchema))
            {
                try
                {
                    var obj = JObject.Parse(overrideSchema);
                    if ((string)obj["type"] != "object")
                    {
                        schema = null;
                        error = "Parameter schema must be a JSON object schema.";
                        return false;
                    }

                    schema = obj.ToString(Formatting.None);
                    error = null;
                    return true;
                }
                catch (JsonException ex)
                {
                    schema = null;
                    error = ex.Message;
                    return false;
                }
            }

            schema = BuildSchemaFromPayload(payload).ToString(Formatting.None);
            error = null;
            return true;
        }

        private static JObject BuildSchemaFromPayload(JObject payload)
        {
            var kinds = payload["sample"]?["parameter_kinds"] as JObject;
            var properties = new JObject();
            if (kinds != null)
            {
                foreach (var property in kinds.Properties())
                    properties[property.Name] = new JObject { ["type"] = ToJsonSchemaType((string)property.Value) };
            }

            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties
            };
        }

        private static string ToJsonSchemaType(string kind)
        {
            switch (kind)
            {
                case "number":
                    return "number";
                case "bool":
                    return "boolean";
                case "array":
                    return "array";
                case "object":
                    return "object";
                default:
                    return "string";
            }
        }

        private static bool IsAllowedOutput(JObject payload, string outputChoice)
        {
            var choices = payload["output_choices"] as JArray ?? new JArray("mcp_only", "ribbon_plus_mcp");
            return choices.Values<string>().Any(c => string.Equals(c, outputChoice ?? "mcp_only", StringComparison.Ordinal));
        }

        private static int CondensationAttemptsToday(JObject payload, DateTimeOffset now)
        {
            var today = now.UtcDateTime.Date;
            return ((JArray)payload["condensation_attempts"] ?? new JArray())
                .Count(a => DateTimeOffset.TryParse((string)a["attempted_at"], out var ts) && ts.UtcDateTime.Date == today);
        }

        private static string RecordFailure(
            BakeDb db,
            BakeSuggestionRecord suggestion,
            string code,
            string message,
            DateTimeOffset now,
            JToken diagnostics = null)
        {
            var payload = ParsePayload(suggestion.PayloadJson);
            AppendArray(payload, "accept_attempts", new JObject
            {
                ["attempted_at"] = now.ToString("o"),
                ["error_code"] = code,
                ["diagnostics"] = diagnostics ?? new JObject()
            });

            suggestion.PayloadJson = payload.ToString(Formatting.None);
            suggestion.State = BakeSuggestionStates.Open;
            suggestion.UpdatedAt = now.ToString("o");
            db.UpsertSuggestion(suggestion);
            RecordExistingToolAttempt(db, diagnostics, code, now);

            var response = new JObject
            {
                ["ok"] = false,
                ["error_code"] = code,
                ["message"] = message
            };
            if (diagnostics != null)
                response["prepared_request"] = diagnostics;
            // Safety receipts must stay top-level for the MCP filter; the copy under
            // prepared_request remains for audit detail.
            if (diagnostics is JObject diagnosticObject)
                SafetyResponseMetadata.Attach(response, diagnosticObject);
            return response.ToString(Formatting.None);
        }

        // A timed-out apply is not a failure: the plugin may still be writing the
        // tool. Bind the open suggestion to the operation ID so reconciliation
        // queries the pending work instead of issuing a second apply.
        private static string RecordPending(
            BakeDb db,
            BakeSuggestionRecord suggestion,
            JObject applyResult,
            DateTimeOffset now)
        {
            var operation = applyResult["_operation"] as JObject;
            var payload = ParsePayload(suggestion.PayloadJson);
            AppendArray(payload, "accept_attempts", new JObject
            {
                ["attempted_at"] = now.ToString("o"),
                ["pending"] = true,
                ["operation_id"] = operation?.Value<string>("operationId")
            });
            suggestion.PayloadJson = payload.ToString(Formatting.None);
            suggestion.UpdatedAt = now.ToString("o");
            db.UpsertSuggestion(suggestion);

            var response = new JObject
            {
                ["ok"] = false,
                ["error_code"] = "plugin_apply_pending",
                ["message"] = "The apply request did not reach a final result; the plugin may still complete it. "
                    + "Query revit_get_operation_status with this operationId before retrying — "
                    + "do not accept the same suggestion again.",
                ["operation_id"] = operation?.Value<string>("operationId")
            };
            SafetyResponseMetadata.Attach(response, applyResult);
            return response.ToString(Formatting.None);
        }

        private static void RecordExistingToolAttempt(BakeDb db, JToken diagnostics, string code, DateTimeOffset now)
        {
            var name = (string)diagnostics?["tool_name"] ?? (string)diagnostics?["name"];
            if (string.IsNullOrWhiteSpace(name))
                return;

            var existing = db.ReadRegistryRecords().FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));
            if (existing == null)
                return;

            JArray history;
            try
            {
                history = JArray.Parse(string.IsNullOrWhiteSpace(existing.VersionHistoryBlob) ? "[]" : existing.VersionHistoryBlob);
            }
            catch (JsonException)
            {
                history = new JArray();
            }

            history.Add(new JObject
            {
                ["event"] = "accept_attempt_failed",
                ["suggestion_error_code"] = code,
                ["attempted_at"] = now.ToString("o")
            });
            db.TryUpdateRegistryVersionHistory(name, history.ToString(Formatting.None));
        }

        private static string Failure(string code, string message)
        {
            return new JObject
            {
                ["ok"] = false,
                ["error_code"] = code,
                ["message"] = message
            }.ToString(Formatting.None);
        }

        private static string RecordSuccess(
            BakeDb db,
            BakeSuggestionRecord suggestion,
            JObject request,
            JObject applyResult,
            DateTimeOffset now)
        {
            var dllBase64 = (string)applyResult["dll_base64"];
            byte[] dllBytes = null;
            if (!string.IsNullOrWhiteSpace(dllBase64))
            {
                try { dllBytes = Convert.FromBase64String(dllBase64); }
                catch (FormatException) { dllBytes = null; }
            }

            var compatMap = new JObject
            {
                ["output_choice"] = (string)request["output_choice"] ?? "mcp_only",
                ["display_name"] = (string)applyResult["display_name"] ?? (string)request["display_name"],
                ["last_compiled_revit_version"] = (string)applyResult["revit_version"] ?? string.Empty
            };

            var record = new BakedToolRecord
            {
                Name = (string)applyResult["tool_name"] ?? (string)request["tool_name"],
                Description = (string)applyResult["description"] ?? (string)request["description"] ?? string.Empty,
                Source = (string)request["source"] ?? string.Empty,
                ParamsSchema = (string)applyResult["params_schema"] ?? (string)request["params_schema"] ?? "{}",
                CompatMap = compatMap.ToString(Formatting.None),
                DllBytes = dllBytes,
                SourceCode = (string)applyResult["source_code"] ?? (string)request["source_code"],
                CreatedFromSuggestionId = suggestion.Id,
                ReviewedByUser = true,
                CreatedAt = now.ToString("o"),
                FailureRate = 0,
                VersionHistoryBlob = new JArray(new JObject
                {
                    ["event"] = "accepted",
                    ["suggestion_id"] = suggestion.Id,
                    ["accepted_at"] = now.ToString("o"),
                    ["revit_version"] = (string)applyResult["revit_version"] ?? string.Empty
                }).ToString(Formatting.None)
            };

            if (!db.TryInsertRegistryRecord(record))
            {
                return RecordFailure(
                    db,
                    suggestion,
                    "registry_insert_failed",
                    "A baked tool with this name already exists in the server registry.",
                    now,
                    new JObject { ["tool_name"] = record.Name });
            }

            var payload = ParsePayload(suggestion.PayloadJson);
            AppendArray(payload, "accept_attempts", new JObject
            {
                ["attempted_at"] = now.ToString("o"),
                ["ok"] = true,
                ["tool_name"] = record.Name
            });
            suggestion.PayloadJson = payload.ToString(Formatting.None);
            suggestion.State = BakeSuggestionStates.Accepted;
            suggestion.UpdatedAt = now.ToString("o");
            db.UpsertSuggestion(suggestion);

            return new JObject
            {
                ["ok"] = true,
                ["tool_name"] = record.Name,
                ["state"] = BakeSuggestionStates.Accepted
            }.ToString(Formatting.None);
        }

        private static void AppendArray(JObject payload, string name, JObject item)
        {
            if (!(payload[name] is JArray array))
            {
                array = new JArray();
                payload[name] = array;
            }
            array.Add(item);
        }

        private static JObject ParsePayload(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new JObject();
            try
            {
                using var text = new StringReader(json);
                using var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None };
                return JObject.Load(reader);
            }
            catch (JsonException)
            {
                return new JObject();
            }
        }

        private sealed class PreparedRequest
        {
            public bool Ok { get; private set; }
            public string ErrorCode { get; private set; }
            public string Message { get; private set; }
            public JObject Request { get; private set; }
            public JObject Diagnostics { get; private set; }

            public static PreparedRequest Success(JObject request)
            {
                return new PreparedRequest { Ok = true, Request = request };
            }

            public static PreparedRequest Failure(string code, string message, JObject diagnostics)
            {
                return new PreparedRequest { Ok = false, ErrorCode = code, Message = message, Diagnostics = diagnostics };
            }
        }
    }

    public interface ICodeCondenser
    {
        CondensedBakeCode Condense(IReadOnlyList<string> redactedSamples);
    }

    public sealed class CondensedBakeCode
    {
        public string Code { get; set; }
        public string ParamsSchema { get; set; }
    }
}
