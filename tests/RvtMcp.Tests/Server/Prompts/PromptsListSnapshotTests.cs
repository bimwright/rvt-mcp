using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Tests.Helpers;
using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>Golden snapshot of the prompt surface: names, description hashes, arguments.
    /// Same mechanism as ToolsListSnapshotTests — extend, don't fork.</summary>
    public class PromptsListSnapshotTests
    {
        private static readonly string GoldenPath = Path.Combine(
            Path.GetDirectoryName(typeof(PromptsListSnapshotTests).Assembly.Location)!,
            "..", "..", "..", "Golden", "prompts-list.json");

        [Fact]
        public void Prompts_list_matches_golden_snapshot()
        {
            var captured = CapturePromptsList();

            SnapshotSerializer.VerifyGolden(GoldenPath, captured, "PromptsListSnapshot");
        }

        private static string CapturePromptsList()
        {
            var serverAssembly = typeof(RvtMcp.Server.ToolsetFilter).Assembly;
            var promptType = serverAssembly.GetType("RvtMcp.Server.Prompts.RevitPrompts")!;

            var prompts = promptType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Select(m => new { Method = m, Attr = m.GetCustomAttribute<McpServerPromptAttribute>() })
                .Where(x => x.Attr != null)
                .Select(x => new
                {
                    name = x.Attr!.Name ?? x.Method.Name,
                    description_hash = SnapshotSerializer.HashDescription(
                        x.Method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty),
                    arguments = x.Method.GetParameters()
                        .Select(p => new
                        {
                            name = p.Name,
                            required = !p.HasDefaultValue,
                            @default = p.HasDefaultValue ? p.DefaultValue : null,
                            description_hash = SnapshotSerializer.HashDescription(
                                p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty)
                        })
                        .ToArray()
                })
                .OrderBy(p => p.name, StringComparer.Ordinal)
                .Select(JObject.FromObject)
                .ToArray();

            var root = new JObject
            {
                ["generated_by"] = "PromptsListSnapshotTests",
                ["prompt_count"] = prompts.Length,
                ["prompts"] = new JArray(prompts)
            };
            return JsonConvert.SerializeObject(root, Formatting.Indented);
        }
    }
}
