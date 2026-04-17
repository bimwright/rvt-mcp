using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bimwright.Plugin
{
    public class SchemaValidationResult
    {
        public bool IsValid { get; set; }
        public string Error { get; set; }
        public string Suggestion { get; set; }
        public string Hint { get; set; }

        public static SchemaValidationResult Ok() =>
            new SchemaValidationResult { IsValid = true };

        public static SchemaValidationResult Fail(string error, string suggestion, string hint) =>
            new SchemaValidationResult
            {
                IsValid = false,
                Error = error,
                Suggestion = suggestion,
                Hint = hint,
            };
    }

    /// <summary>
    /// S6 strict schema validator (aspect #5 §S6) — fails fast with error-as-teacher response.
    /// Handles the subset of JSON Schema actually produced by handler <c>ParametersSchema</c>:
    /// <c>type</c>, <c>required</c>, <c>enum</c>, and <c>array.items.type</c>.
    /// Avoids the commercial Newtonsoft.Json.Schema dependency so Apache-2.0 redistribution stays clean.
    /// </summary>
    public static class SchemaValidator
    {
        private const string DefaultHint = "See tool description Example section";

        public static SchemaValidationResult Validate(string schemaJson, string paramsJson)
        {
            // Empty schema = accept any params (matches handlers that declare "{}")
            if (string.IsNullOrWhiteSpace(schemaJson) || schemaJson.Trim() == "{}")
                return SchemaValidationResult.Ok();

            JObject schema;
            try { schema = JObject.Parse(schemaJson); }
            catch { return SchemaValidationResult.Ok(); } // malformed schema = fail-open (internal bug, not user's fault)

            JObject parameters;
            try
            {
                parameters = string.IsNullOrWhiteSpace(paramsJson)
                    ? new JObject()
                    : JObject.Parse(paramsJson);
            }
            catch (JsonException ex)
            {
                return SchemaValidationResult.Fail(
                    "Parameters must be a JSON object: " + ex.Message,
                    "Pass params as a JSON object: {\"field\": value, ...}",
                    DefaultHint);
            }

            foreach (var required in GetRequiredFields(schema))
            {
                if (parameters[required] == null)
                    return SchemaValidationResult.Fail(
                        $"Parameter validation failed: required field '{required}' is missing",
                        $"Add '{required}' to your params: {BuildHint(schema, required)}",
                        DefaultHint);
            }

            var properties = schema["properties"] as JObject;
            if (properties == null) return SchemaValidationResult.Ok();

            foreach (var prop in properties.Properties())
            {
                var name = prop.Name;
                var actual = parameters[name];
                if (actual == null) continue; // optional + absent = OK

                var propSchema = prop.Value as JObject;
                if (propSchema == null) continue;

                var expectedType = propSchema.Value<string>("type");
                if (!IsTypeMatch(expectedType, actual))
                    return SchemaValidationResult.Fail(
                        $"Parameter validation failed: field '{name}' must be {expectedType}, got {GetJsonType(actual)}",
                        $"Pass {name} as {expectedType}: {BuildHint(schema, name)}",
                        DefaultHint);

                var enumArr = propSchema["enum"] as JArray;
                if (enumArr != null && enumArr.Count > 0 && expectedType == "string")
                {
                    var value = actual.Value<string>();
                    var matched = false;
                    foreach (var item in enumArr)
                    {
                        if (string.Equals(item.Value<string>(), value, StringComparison.Ordinal))
                        {
                            matched = true;
                            break;
                        }
                    }
                    if (!matched)
                    {
                        var allowed = string.Join(", ", FormatEnum(enumArr));
                        return SchemaValidationResult.Fail(
                            $"Parameter validation failed: field '{name}' = '{value}' not in allowed values [{allowed}]",
                            $"Pick one of: [{allowed}]",
                            DefaultHint);
                    }
                }

                if (expectedType == "array" && actual.Type == JTokenType.Array)
                {
                    var items = propSchema["items"] as JObject;
                    var itemType = items?.Value<string>("type");
                    if (!string.IsNullOrEmpty(itemType))
                    {
                        var arr = (JArray)actual;
                        for (var i = 0; i < arr.Count; i++)
                        {
                            if (!IsTypeMatch(itemType, arr[i]))
                                return SchemaValidationResult.Fail(
                                    $"Parameter validation failed: field '{name}[{i}]' must be {itemType}, got {GetJsonType(arr[i])}",
                                    $"Pass {name} as array of {itemType}",
                                    DefaultHint);
                        }
                    }
                }
            }

            return SchemaValidationResult.Ok();
        }

        private static List<string> GetRequiredFields(JObject schema)
        {
            var result = new List<string>();
            var arr = schema["required"] as JArray;
            if (arr == null) return result;
            foreach (var t in arr)
            {
                var s = t.Value<string>();
                if (!string.IsNullOrEmpty(s)) result.Add(s);
            }
            return result;
        }

        private static bool IsTypeMatch(string expected, JToken token)
        {
            if (string.IsNullOrEmpty(expected)) return true;
            switch (expected)
            {
                case "string":  return token.Type == JTokenType.String;
                case "number":  return token.Type == JTokenType.Float || token.Type == JTokenType.Integer;
                case "integer": return token.Type == JTokenType.Integer;
                case "boolean": return token.Type == JTokenType.Boolean;
                case "array":   return token.Type == JTokenType.Array;
                case "object":  return token.Type == JTokenType.Object;
                case "null":    return token.Type == JTokenType.Null;
                default:        return true; // unknown schema type = accept (future-compat)
            }
        }

        private static string GetJsonType(JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.String:  return "string";
                case JTokenType.Float:   return "number";
                case JTokenType.Integer: return "integer";
                case JTokenType.Boolean: return "boolean";
                case JTokenType.Array:   return "array";
                case JTokenType.Object:  return "object";
                case JTokenType.Null:    return "null";
                default:                 return token.Type.ToString().ToLower();
            }
        }

        private static IEnumerable<string> FormatEnum(JArray enumArr)
        {
            foreach (var item in enumArr)
                yield return "\"" + item.Value<string>() + "\"";
        }

        private static string BuildHint(JObject schema, string fieldName)
        {
            var prop = (schema["properties"] as JObject)?[fieldName] as JObject;
            if (prop == null) return "{\"" + fieldName + "\": ...}";

            string example;
            switch (prop.Value<string>("type"))
            {
                case "string":  example = "\"value\""; break;
                case "number":  example = "3000"; break;
                case "integer": example = "42"; break;
                case "boolean": example = "true"; break;
                case "array":   example = "[...]"; break;
                case "object":  example = "{...}"; break;
                default:        example = "..."; break;
            }
            return "{\"" + fieldName + "\": " + example + "}";
        }
    }
}
