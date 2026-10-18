using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    /// <summary>
    /// BW-ARGS-001 argument check for tools/call, run as the innermost step before the SDK binds arguments.
    /// A call missing a required parameter is answered with INVALID_ARGUMENT and never reaches the tool (A1).
    /// Undeclared top-level keys do not block the call: it runs and the result carries
    /// <c>_ignored_arguments</c> (A2, owner decision D1 = warn). Only key and parameter names are reported or
    /// logged for a rejected call, never values (A8/A9). Exceptions from binding or the tool body pass through
    /// unchanged (A4 deferred, A7).
    /// </summary>
    internal static class ToolArgumentGuard
    {
        internal const int MaxKeys = 32;
        internal const int MaxNameLength = 64;

        /// <summary>Runs <paramref name="next"/> unless required arguments are missing. RejectedArguments is the
        /// key-only payload that replaces the raw arguments in every log of a rejected call; null otherwise.</summary>
        internal static async ValueTask<(CallToolResult Result, string RejectedArguments)> InvokeAsync(
            RequestContext<CallToolRequestParams> request,
            McpRequestHandler<CallToolRequestParams, CallToolResult> next,
            CancellationToken ct)
        {
            // Unknown tool (A5) or a schema without declared properties: the SDK keeps its own behavior.
            if (request.MatchedPrimitive is not McpServerTool tool
                || !TryReadSchema(tool.ProtocolTool.InputSchema, out var properties, out var required, out var extraAllowed))
                return (await next(request, ct), null);

            var name = tool.ProtocolTool.Name;
            var arguments = request.Params?.Arguments;
            var received = arguments?.Keys.ToList() ?? new List<string>();
            var missing = required.Where(key => arguments == null || !arguments.ContainsKey(key)).ToList();
            var unknown = extraAllowed ? new List<string>() : received.Where(key => !properties.ContainsKey(key)).ToList();
            var nestedUnder = unknown.Where(key => arguments[key].ValueKind == JsonValueKind.Object
                && arguments[key].EnumerateObject().Any(p => properties.ContainsKey(p.Name))).ToList();

            if (missing.Count > 0)
            {
                var payload = new JObject
                {
                    ["rejected"] = true,
                    ["reason"] = "missing_required",
                    ["missing"] = Names(missing),
                    ["unknown"] = Names(unknown),
                    ["received_keys"] = Names(received)
                };
                return (Rejection(name, properties, required, missing, unknown, received, nestedUnder),
                    payload.ToString(Formatting.None));
            }

            var result = await next(request, ct);
            if (unknown.Count > 0)
                AttachIgnored(result, new JObject
                {
                    ["unknown"] = Unknown(unknown, properties),
                    ["note"] = Ignored(name, nestedUnder)
                });
            return (result, null);
        }

        private static CallToolResult Rejection(string tool, Dictionary<string, JsonElement> properties,
            List<string> required, List<string> missing, List<string> unknown, List<string> received, List<string> nestedUnder)
        {
            var quoted = string.Join(", ", missing.Take(MaxKeys).Select(key => "'" + Clip(key) + "'"));
            var error = "INVALID_ARGUMENT: Parameter validation failed: required "
                + (missing.Count == 1 ? $"field {quoted} is missing" : $"fields {quoted} are missing");
            var suggestions = new List<string> { $"Add {quoted} to the arguments of {tool}." };
            suggestions.AddRange(unknown.Take(MaxKeys).Where(key => Suggest(key, properties) != null)
                .Select(key => $"Rename '{Clip(key)}' to '{Suggest(key, properties)}'."));
            suggestions.AddRange(nestedUnder.Take(MaxKeys)
                .Select(key => $"Move the parameters nested under '{Clip(key)}' to the top level of arguments."));
            var body = new JObject
            {
                ["success"] = false,
                ["error"] = error,
                ["suggestion"] = string.Join(" ", suggestions),
                ["hint"] = "Argument names are case-sensitive. Received keys: "
                    + (received.Count == 0 ? "(none)" : string.Join(", ", received.Take(MaxKeys).Select(Clip))) + ".",
                ["invalid_arguments"] = new JObject
                {
                    ["missing"] = Names(missing),
                    ["unknown"] = Unknown(unknown, properties),
                    ["received"] = Names(received),
                    ["expected"] = new JArray(properties.Take(MaxKeys).Select(p => new JObject
                    {
                        ["name"] = Clip(p.Key),
                        ["type"] = TypeOf(p.Value),
                        ["required"] = required.Contains(p.Key)
                    }))
                }
            };
            return new CallToolResult
            {
                Content = new[] { new TextContentBlock { Text = body.ToString(Formatting.None) } },
                IsError = true
            };
        }

        private static string Ignored(string tool, List<string> nestedUnder)
        {
            var note = $"These keys are not parameters of {tool} and were ignored; the call ran without them. "
                + "Argument names are case-sensitive.";
            return nestedUnder.Count == 0 ? note
                : note + " Parameters appear to be nested under " + string.Join(", ", nestedUnder.Take(MaxKeys).Select(key => "'" + Clip(key) + "'")) + ".";
        }

        // A JSON result gets a field; a plain-text result gets a trailing line, so its "Error:" prefix still classifies it.
        private static void AttachIgnored(CallToolResult result, JObject ignored)
        {
            var block = result?.Content?.OfType<TextContentBlock>().FirstOrDefault();
            if (block == null) return;
            try
            {
                if (JToken.Parse(block.Text ?? "") is JObject data)
                {
                    data["_ignored_arguments"] = ignored;
                    block.Text = data.ToString(Formatting.None);
                    return;
                }
            }
            catch (JsonReaderException) { }
            block.Text = (block.Text ?? "") + "\n_ignored_arguments: " + ignored.ToString(Formatting.None);
        }

        private static JArray Unknown(List<string> unknown, Dictionary<string, JsonElement> properties)
            => new JArray(unknown.Take(MaxKeys).Select(key =>
            {
                var entry = new JObject { ["key"] = Clip(key) };
                var target = Suggest(key, properties);
                if (target != null) entry["did_you_mean"] = target;
                return entry;
            }));

        // Matches after removing '_'/'-' and ignoring case (Elevation -> elevation, element_id -> elementId).
        private static string Suggest(string key, Dictionary<string, JsonElement> properties)
        {
            var normalized = Normalize(key);
            var match = properties.Keys.FirstOrDefault(p => Normalize(p) == normalized);
            return match == null ? null : Clip(match);
        }

        private static string Normalize(string key) => key.Replace("_", "").Replace("-", "").ToLowerInvariant();

        private static JArray Names(IEnumerable<string> keys) => new JArray(keys.Take(MaxKeys).Select(Clip));

        private static string Clip(string key) => key.Length > MaxNameLength ? key.Substring(0, MaxNameLength) : key;

        private static string TypeOf(JsonElement property)
        {
            if (!property.TryGetProperty("type", out var type)) return "any";
            return type.ValueKind == JsonValueKind.Array
                ? string.Join("|", type.EnumerateArray().Select(t => t.GetString()))
                : type.GetString();
        }

        /// <summary>extraAllowed is true when the schema explicitly allows extra keys; those keys are then not unknown.</summary>
        private static bool TryReadSchema(JsonElement schema, out Dictionary<string, JsonElement> properties,
            out List<string> required, out bool extraAllowed)
        {
            properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            required = new List<string>();
            extraAllowed = false;
            if (schema.ValueKind != JsonValueKind.Object
                || !schema.TryGetProperty("properties", out var declared) || declared.ValueKind != JsonValueKind.Object)
                return false;
            foreach (var property in declared.EnumerateObject())
                properties[property.Name] = property.Value;
            if (schema.TryGetProperty("required", out var list) && list.ValueKind == JsonValueKind.Array)
                required.AddRange(list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()));
            if (schema.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind != JsonValueKind.False)
                extraAllowed = true;
            return true;
        }
    }
}
