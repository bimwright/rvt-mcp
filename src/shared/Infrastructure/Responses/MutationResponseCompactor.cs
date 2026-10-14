#nullable enable
using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// Converts an already-applied mutation's oversized detail into a small truthful
    /// summary. It never reports the completed mutation as failed.
    /// </summary>
    public static class MutationResponseCompactor
    {
        private const int MaxScalarChars = 2048;
        private const int MaxPreviewIds = 20;

        public static JObject Compact(object? data, int originalByteCount)
        {
            var source = ToObject(data);
            // The MCP layer may compact again after JSON escaping grows a plugin
            // summary. Keep the original outcome and metadata, not a summary of it.
            if (IsTrue(source, "response_compacted"))
                return (JObject)source.DeepClone();

            var summary = new JObject();
            var collectionCounts = new JObject();
            var idPreview = new JObject();

            foreach (var property in source.Properties())
            {
                if (property.Value is JArray array)
                {
                    collectionCounts[property.Name] = array.Count;
                    var ids = ExtractIds(array);
                    if (ids.Count > 0)
                        idPreview[property.Name] = ids;
                    continue;
                }

                if (property.Value is JObject objectValue)
                {
                    var compactObject = CompactScalarObject(objectValue);
                    if (compactObject.Count > 0)
                        summary[property.Name] = compactObject;
                    continue;
                }

                if (!(property.Value is JValue value))
                    continue;

                if (value.Type == JTokenType.String
                    && ((string?)value)?.Length > MaxScalarChars)
                {
                    var text = (string?)value ?? string.Empty;
                    summary[property.Name + "_char_count"] = text.Length;
                    summary[property.Name + "_preview"] = text.Substring(0, MaxScalarChars);
                }
                else
                {
                    summary[property.Name] = property.Value.DeepClone();
                }
            }

            var explicitOutcome = source["mutation_applied"];
            var mutationApplied = explicitOutcome?.Type == JTokenType.Boolean || explicitOutcome?.Type == JTokenType.Null
                ? explicitOutcome.DeepClone()
                : new JValue(!IsTrue(source, "dry_run")
                    && !IsTrue(source, "dryRun")
                    && !IsTrue(source, "rolledBack")
                    && !IsFalse(source, "success")
                    && !IsFalse(source, "ok"));

            return new JObject
            {
                ["success"] = true,
                ["mutation_applied"] = mutationApplied,
                ["response_compacted"] = true,
                ["original_byte_count"] = originalByteCount,
                ["summary"] = summary,
                ["collection_counts"] = collectionCounts,
                ["id_preview"] = idPreview,
                ["warning"] =
                    "The command completed successfully, but oversized per-item response detail was compacted. Inspect the summary before deciding whether another call is needed."
            };
        }

        private static bool IsTrue(JObject source, string propertyName)
        {
            return source[propertyName]?.Type == JTokenType.Boolean
                && source.Value<bool>(propertyName);
        }

        private static bool IsFalse(JObject source, string propertyName)
        {
            return source[propertyName]?.Type == JTokenType.Boolean
                && !source.Value<bool>(propertyName);
        }

        private static JObject ToObject(object? data)
        {
            if (data == null)
                return new JObject();
            if (data is JObject obj)
                return obj;
            if (data is JToken token)
                return token as JObject ?? new JObject { ["value"] = token.DeepClone() };

            try
            {
                var serialized = JsonConvert.SerializeObject(data);
                var parsed = JToken.Parse(serialized);
                return parsed as JObject ?? new JObject { ["value"] = parsed };
            }
            catch
            {
                return new JObject { ["value_type"] = data.GetType().FullName };
            }
        }

        private static JObject CompactScalarObject(JObject value)
        {
            var result = new JObject();
            foreach (var property in value.Properties())
            {
                if (!(property.Value is JValue scalar))
                    continue;

                if (scalar.Type == JTokenType.String
                    && ((string?)scalar)?.Length > MaxScalarChars)
                    continue;

                result[property.Name] = property.Value.DeepClone();
            }
            return result;
        }

        private static JArray ExtractIds(JArray array)
        {
            var result = new JArray();
            foreach (var item in array.Take(MaxPreviewIds))
            {
                if (item is JValue scalar && IsIdScalar(scalar))
                {
                    result.Add(scalar.DeepClone());
                    continue;
                }

                if (!(item is JObject obj))
                    continue;

                var id = obj.Properties().FirstOrDefault(p => IsIdProperty(p.Name))?.Value;
                if (id is JValue idValue && IsIdScalar(idValue))
                    result.Add(idValue.DeepClone());
            }
            return result;
        }

        private static bool IsIdProperty(string name)
        {
            return string.Equals(name, "id", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("Id", StringComparison.Ordinal)
                || name.EndsWith("_id", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsIdScalar(JValue value)
        {
            return value.Type == JTokenType.Integer || value.Type == JTokenType.String;
        }
    }
}
