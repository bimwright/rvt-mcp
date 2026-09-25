using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>Spec §4.3: every revit_* tool name referenced inside a prompt body must
    /// exist in the tool catalog. Renaming a tool and forgetting its prompt fails here.</summary>
    public class PromptToolNameTests
    {
        [Fact]
        public void Every_revit_tool_name_in_prompt_bodies_exists_in_catalog()
        {
            var serverAssembly = typeof(RvtMcp.Server.ToolsetFilter).Assembly;
            var programType = serverAssembly.GetType("RvtMcp.Server.Program")!;
            var resolveToolTypes = programType.GetMethod(
                "ResolveRegisteredToolTypes", BindingFlags.NonPublic | BindingFlags.Static)!;

            var config = new RvtMcpConfig
            {
                EnableAdaptiveBake = true,
                Toolsets = new List<string> { "all" }
            };
            var enabled = RvtMcp.Server.ToolsetFilter.Resolve(config);

            var toolClasses = (Type[])resolveToolTypes.Invoke(null, new object[] { enabled, config })!;
            var toolNames = toolClasses
                .SelectMany(cls => cls.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
                .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
                .Where(a => a != null && a.Name != null)
                .Select(a => a!.Name!)
                .ToHashSet(System.StringComparer.Ordinal);

            var resourceNames = serverAssembly.GetManifestResourceNames()
                .Where(n => n.StartsWith("RvtMcp.Prompts.", System.StringComparison.Ordinal)
                            && n.EndsWith(".md", System.StringComparison.Ordinal))
                .ToArray();
            Assert.True(resourceNames.Length == 4,
                "expected 4 embedded prompt bodies, found: " + string.Join(", ", resourceNames));

            var failures = new List<string>();
            foreach (var resourceName in resourceNames)
            {
                using (var reader = new StreamReader(serverAssembly.GetManifestResourceStream(resourceName)!))
                {
                    var body = reader.ReadToEnd();
                    foreach (Match m in Regex.Matches(body, @"revit_[a-z0-9_]+"))
                    {
                        if (!toolNames.Contains(m.Value))
                            failures.Add($"{resourceName}: {m.Value}");
                    }
                }
            }

            Assert.True(failures.Count == 0,
                "prompt bodies reference unknown tools: " + string.Join("; ", failures));
        }
    }
}
