using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using RvtMcp.Plugin;
using RvtMcp.Tests.Helpers;
using ModelContextProtocol.Server;
using Xunit;

namespace RvtMcp.Tests
{
    public class ToolsListSnapshotTests
    {
        private static readonly string GoldenPath = Path.Combine(
            Path.GetDirectoryName(typeof(ToolsListSnapshotTests).Assembly.Location)!,
            "..", "..", "..", "Golden", "tools-list.json");

        private static readonly string AdaptiveGoldenPath = Path.Combine(
            Path.GetDirectoryName(typeof(ToolsListSnapshotTests).Assembly.Location)!,
            "..", "..", "..", "Golden", "tools-list-adaptive-bake.json");

        private static readonly string StructuralGoldenPath = Path.Combine(
            Path.GetDirectoryName(typeof(ToolsListSnapshotTests).Assembly.Location)!,
            "..", "..", "..", "Golden", "tools-list-structural.json");

        [Fact]
        public void Tools_list_matches_golden_snapshot()
        {
            var captured = CaptureToolsList(AllToolsetsConfig(enableAdaptiveBake: false));

            var update = Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1";
            var goldenExists = File.Exists(GoldenPath);

            if (update || !goldenExists)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(GoldenPath)!);
                File.WriteAllText(GoldenPath, captured);
                if (!goldenExists)
                {
                    Console.Error.WriteLine(
                        $"[ToolsListSnapshot] Golden file bootstrapped at {GoldenPath}. " +
                        "Please commit it.");
                }
                return;
            }

            var expected = File.ReadAllText(GoldenPath);
            Assert.Equal(expected.ReplaceLineEndings("\n"), captured.ReplaceLineEndings("\n"));
        }

        [Fact]
        public void Adaptive_bake_tools_list_matches_golden_snapshot()
        {
            var captured = CaptureToolsList(new RvtMcpConfig
            {
                EnableAdaptiveBake = true,
                Toolsets = new System.Collections.Generic.List<string> { "all" }
            });

            var update = Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1";
            var goldenExists = File.Exists(AdaptiveGoldenPath);

            if (update || !goldenExists)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AdaptiveGoldenPath)!);
                File.WriteAllText(AdaptiveGoldenPath, captured);
                if (!goldenExists)
                {
                    Console.Error.WriteLine(
                        $"[ToolsListSnapshot] Adaptive golden file bootstrapped at {AdaptiveGoldenPath}. " +
                        "Please commit it.");
                }
                return;
            }

            var expected = File.ReadAllText(AdaptiveGoldenPath);
            Assert.Equal(expected.ReplaceLineEndings("\n"), captured.ReplaceLineEndings("\n"));
        }

        [Fact]
        public void Structural_toolset_matches_golden_snapshot()
        {
            var captured = CaptureToolsList(new RvtMcpConfig
            {
                Toolsets = new System.Collections.Generic.List<string> { "structural" }
            });

            var update = Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1";
            var goldenExists = File.Exists(StructuralGoldenPath);

            if (update || !goldenExists)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StructuralGoldenPath)!);
                File.WriteAllText(StructuralGoldenPath, captured);
                if (!goldenExists)
                {
                    Console.Error.WriteLine(
                        $"[ToolsListSnapshot] Structural golden file bootstrapped at {StructuralGoldenPath}. " +
                        "Please commit it.");
                }
                return;
            }

            var expected = File.ReadAllText(StructuralGoldenPath);
            Assert.Equal(expected.ReplaceLineEndings("\n"), captured.ReplaceLineEndings("\n"));
        }

        [Fact]
        public void Default_tools_snapshot_does_not_expose_adaptive_bake_suggestions()
        {
            var captured = CaptureToolsList(AllToolsetsConfig(enableAdaptiveBake: false));

            Assert.DoesNotContain("\"name\": \"revit_list_bake_suggestions\"", captured);
            Assert.DoesNotContain("\"name\": \"revit_accept_bake_suggestion\"", captured);
            Assert.DoesNotContain("\"name\": \"revit_dismiss_bake_suggestion\"", captured);
        }

        [Fact]
        public void Default_toolsets_expose_send_code_without_adaptive_bake()
        {
            var captured = CaptureToolsList(new RvtMcpConfig());

            Assert.Contains("\"name\": \"revit_send_code_to_revit\"", captured);
            Assert.Contains("\"name\": \"revit_batch_execute\"", captured);
            Assert.DoesNotContain("\"name\": \"revit_list_baked_tools\"", captured);
            Assert.DoesNotContain("\"name\": \"revit_run_baked_tool\"", captured);
            Assert.DoesNotContain("\"name\": \"revit_clash_detection\"", captured);
            Assert.DoesNotContain("\"name\": \"revit_export_pdf\"", captured);
            Assert.DoesNotContain("\"name\": \"revit_list_bake_suggestions\"", captured);
            Assert.DoesNotContain("\"name\": \"revit_accept_bake_suggestion\"", captured);
            Assert.DoesNotContain("\"name\": \"revit_dismiss_bake_suggestion\"", captured);

            var count = (int)Newtonsoft.Json.Linq.JObject.Parse(captured)["tool_count"]!;
            Assert.Equal(41, count);
        }

        [Fact]
        public void Read_only_defaults_hide_send_code()
        {
            var captured = CaptureToolsList(new RvtMcpConfig { ReadOnly = true });
            Assert.DoesNotContain("\"name\": \"revit_send_code_to_revit\"", captured);
            Assert.Contains("\"name\": \"revit_list_available_targets\"", captured);
        }

        [Fact]
        public void Disable_toolbaker_hides_send_code_even_with_meta()
        {
            var captured = CaptureToolsList(new RvtMcpConfig { EnableToolbaker = false });
            Assert.DoesNotContain("\"name\": \"revit_send_code_to_revit\"", captured);
            Assert.Contains("\"name\": \"revit_batch_execute\"", captured);
        }

        [Fact]
        public void Approved_bulk_tools_expose_local_file_output_while_send_code_remains_automatic()
        {
            var expected = new[]
            {
                "revit_compute_room_finishes",
                "revit_export_room_data",
                "revit_get_material_takeoff",
                "revit_workflow_takeoff_report",
                "revit_batch_execute",
                "revit_export_shared_parameter_file",
                "revit_run_baked_tool",
                "revit_workflow_data_roundtrip"
            };
            var serverAssembly = typeof(RvtMcp.Server.ToolsetFilter).Assembly;
            var methods = serverAssembly.GetTypes()
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Select(method => new
                {
                    Method = method,
                    Tool = method.GetCustomAttribute<McpServerToolAttribute>(),
                    Description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty
                })
                .Where(item => item.Tool != null)
                .ToArray();

            foreach (var name in expected)
            {
                var tool = Assert.Single(methods, item => item.Tool!.Name == name);
                var output = Assert.Single(tool.Method.GetParameters(), parameter => parameter.Name == "output");
                Assert.Equal(typeof(string), output.ParameterType);
                Assert.Equal("inline", output.DefaultValue);
                Assert.Contains("local same-machine", tool.Description, StringComparison.OrdinalIgnoreCase);
            }

            var sendCode = Assert.Single(methods, item => item.Tool!.Name == "revit_send_code_to_revit");
            Assert.DoesNotContain(sendCode.Method.GetParameters(), parameter => parameter.Name == "output");
            Assert.Contains("auto-spill", sendCode.Description, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Server_instructions_stay_under_2048_utf8_bytes()
        {
            var programType = typeof(RvtMcp.Server.ToolsetFilter).Assembly.GetType("RvtMcp.Server.Program")!;
            var field = programType.GetField("ServerInstructionsText",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(field);
            var text = (string)(field!.GetRawConstantValue() ?? field.GetValue(null)!);
            var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
            Assert.True(bytes <= 2048, $"ServerInstructionsText is {bytes} UTF-8 bytes (Anthropic Tool Search cap is 2048).");
        }

        [Fact]
        public void Adaptive_bake_snapshot_exposes_exactly_three_suggestion_handlers()
        {
            var captured = CaptureToolsList(new RvtMcpConfig
            {
                EnableAdaptiveBake = true,
                Toolsets = new System.Collections.Generic.List<string> { "all" }
            });

            Assert.Contains("\"name\": \"revit_list_bake_suggestions\"", captured);
            Assert.Contains("\"name\": \"revit_accept_bake_suggestion\"", captured);
            Assert.Contains("\"name\": \"revit_dismiss_bake_suggestion\"", captured);
            Assert.Equal(3, new[]
            {
                "\"name\": \"revit_list_bake_suggestions\"",
                "\"name\": \"revit_accept_bake_suggestion\"",
                "\"name\": \"revit_dismiss_bake_suggestion\""
            }.Count(captured.Contains));
            Assert.DoesNotContain("\"name\": \"revit_bake_tool\"", captured);
        }

        [Fact]
        public void Generated_tools_snapshot_does_not_include_removed_bake_tool()
        {
            var captured = CaptureToolsList(AllToolsetsConfig(enableAdaptiveBake: false));

            Assert.DoesNotContain("\"name\": \"revit_bake_tool\"", captured);
        }

        private static string CaptureToolsList(RvtMcpConfig config)
        {
            // ToolsetFilter is a public type in Server — gives a stable handle to the
            // Server assembly without forcing `Program` to become public.
            var serverAssembly = typeof(RvtMcp.Server.ToolsetFilter).Assembly;
            var programType = serverAssembly.GetType("RvtMcp.Server.Program")!;
            var resolveToolTypes = programType.GetMethod("ResolveRegisteredToolTypes", BindingFlags.NonPublic | BindingFlags.Static)!;
            var enabled = RvtMcp.Server.ToolsetFilter.Resolve(config);

            var toolClasses = ((Type[])resolveToolTypes.Invoke(null, new object[] { enabled, config })!)
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToArray();

            var tools = toolClasses
                .SelectMany(cls => cls.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                    .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null))
                .Select(ToToolMetadata)
                .ToArray();

            return SnapshotSerializer.Serialize(tools.Length, tools);
        }

        private static RvtMcpConfig AllToolsetsConfig(bool enableAdaptiveBake)
        {
            return new RvtMcpConfig
            {
                EnableAdaptiveBake = enableAdaptiveBake,
                Toolsets = new System.Collections.Generic.List<string> { "all" }
            };
        }

        private static object ToToolMetadata(MethodInfo method)
        {
            var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>()!;
            var descAttr = method.GetCustomAttribute<DescriptionAttribute>();
            var description = descAttr?.Description ?? string.Empty;
            var name = toolAttr.Name ?? ToSnakeCase(method.Name);

            var parameters = method.GetParameters()
                .Select(p => new
                {
                    name = p.Name,
                    type = p.ParameterType.Name,
                    required = !p.HasDefaultValue,
                    description = p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty
                })
                .ToArray();

            return new
            {
                name,
                description_hash = SnapshotSerializer.HashDescription(description),
                inputSchema = new
                {
                    type = "object",
                    properties = parameters.ToDictionary(p => p.name!, p => new { type = p.type, description = p.description }),
                    required = parameters.Where(p => p.required).Select(p => p.name).ToArray()
                }
            };
        }

        private static string ToSnakeCase(string pascal)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < pascal.Length; i++)
            {
                if (i > 0 && char.IsUpper(pascal[i])) sb.Append('_');
                sb.Append(char.ToLowerInvariant(pascal[i]));
            }
            return sb.ToString();
        }
    }
}
