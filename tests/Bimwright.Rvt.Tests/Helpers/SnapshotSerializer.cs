using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bimwright.Rvt.Tests.Helpers
{
    public static class SnapshotSerializer
    {
        public static string HashDescription(string text)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty));
            var hex = BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            return $"sha256:{hex}";
        }

        public static string Serialize(int toolCount, IEnumerable<object> tools)
        {
            var sorted = tools
                .Select(JObject.FromObject)
                .OrderBy(t => t["name"]?.ToString(), StringComparer.Ordinal)
                .Select(SortSchemaProperties)
                .ToArray();

            var root = new JObject
            {
                ["generated_by"] = "ToolsListSnapshotTests",
                ["tool_count"] = toolCount,
                ["tools"] = new JArray(sorted)
            };

            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented
            };
            return JsonConvert.SerializeObject(root, settings);
        }

        private static JObject SortSchemaProperties(JObject tool)
        {
            if (tool["inputSchema"] is JObject schema)
            {
                tool["inputSchema"] = SortObjectKeys(schema);
            }
            return tool;
        }

        private static JToken SortObjectKeys(JToken token)
        {
            if (token is JObject obj)
            {
                var sorted = new JObject();
                foreach (var prop in obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    sorted[prop.Name] = SortObjectKeys(prop.Value);
                }
                return sorted;
            }
            if (token is JArray arr)
            {
                return new JArray(arr.Select(SortObjectKeys));
            }
            return token;
        }
    }
}
