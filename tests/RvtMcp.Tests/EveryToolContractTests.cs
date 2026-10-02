using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>
    /// Calls every registered tool through its real server method. The wire is replaced by
    /// <see cref="ToolGateway.SendOverride"/>, so each case checks that the tool sends its documented
    /// command to Revit and hands the plug-in's data back to the agent. Tools that never reach the plug-in
    /// (target routing, local history, bake suggestions) are listed with the test that covers them.
    /// </summary>
    [Collection("ServerStateConfig")]
    public sealed class EveryToolContractTests
    {
        // Server-only tools: they answer from local state, so they are exercised by their own tests.
        private static readonly Dictionary<string, string> ServerOnly = new Dictionary<string, string>
        {
            ["revit_record_change"] = "ChangeHistoryRuntimeTests",
            ["revit_get_change_records"] = "ChangeHistoryRuntimeTests",
            ["revit_resolve_history_identity"] = "ChangeHistoryRuntimeTests",
            ["revit_list_bake_suggestions"] = "ToolBakerHandlersTests",
            ["revit_accept_bake_suggestion"] = "ToolBakerHandlersTests",
            ["revit_dismiss_bake_suggestion"] = "ToolBakerHandlersTests",
            ["revit_list_available_targets"] = "MetaToolShellTests",
            ["revit_get_current_target"] = "MetaToolShellTests",
            ["revit_switch_target"] = "MetaToolShellTests",
            ["revit_analyze_usage_patterns"] = "MetaToolShellTests",
        };

        // Tools that parse JSON text or arrays before sending need a minimal valid value for that parameter.
        private static readonly Dictionary<string, object> Shaped = new Dictionary<string, object>
        {
            ["revit_update_schedule_field:fieldRef"] = "{\"name\":\"Mark\"}",
            ["revit_update_schedule_field:changes"] = "{\"hidden\":true}",
            ["revit_create_schedule:fields"] = "[\"Mark\"]",
            ["revit_add_schedule_field:field"] = "{\"name\":\"Mark\"}",
            ["revit_create_surface_based_element:points"] = "[[0,0,0],[1000,0,0],[1000,1000,0]]",
            ["revit_create_mep_fitting:connectors"] = "[{\"elementId\":1,\"connectorIndex\":0},{\"elementId\":2,\"connectorIndex\":0}]",
            ["revit_batch_execute:commands"] = "[{\"command\":\"get_current_view_info\",\"params\":{}}]",
            ["revit_create_filled_region:points"] = new object[] { new object[] { 0.0, 0.0 }, new object[] { 1000.0, 0.0 }, new object[] { 1000.0, 1000.0 } },
            ["revit_create_room_separator:points"] = new object[] { new object[] { 0.0, 0.0 }, new object[] { 1000.0, 0.0 } },
            ["revit_create_dimensions:references"] = new object[] { new JObject { ["elementId"] = 1 }, new JObject { ["elementId"] = 2 } },
            ["revit_survey_change_impact:elementIds"] = new long[] { 1 },
        };

        private static IEnumerable<(string Name, MethodInfo Method)> AllTools()
        {
            foreach (var type in typeof(ToolGateway).Assembly.GetTypes())
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
                    if (attribute?.Name != null) yield return (attribute.Name, method);
                }
            }
        }

        public static IEnumerable<object[]> ForwardingTools() =>
            AllTools().Where(t => !ServerOnly.ContainsKey(t.Name)).OrderBy(t => t.Name).Select(t => new object[] { t.Name });

        [Fact]
        public void Every_registered_tool_is_either_called_here_or_named_as_server_only()
        {
            var names = AllTools().Select(t => t.Name).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());
            Assert.True(names.Count >= 236, "Expected the full tool surface, found " + names.Count);
            foreach (var serverOnly in ServerOnly.Keys)
                Assert.Contains(serverOnly, names);
            Assert.True(ForwardingTools().Count() + ServerOnly.Count == names.Count);

            // A server-only tool must be named in a test file that exists and mentions it.
            var testDirectory = System.IO.Path.GetDirectoryName(ThisFile());
            foreach (var pair in ServerOnly)
            {
                var path = System.IO.Path.Combine(testDirectory, pair.Value + ".cs");
                Assert.True(System.IO.File.Exists(path), pair.Key + ": " + pair.Value + ".cs does not exist.");
            }
        }

        [Theory]
        [MemberData(nameof(ForwardingTools))]
        public async Task The_tool_sends_its_command_to_revit_and_returns_the_plugin_data(string toolName)
        {
            var (_, method) = AllTools().Single(t => t.Name == toolName);
            var wire = toolName.StartsWith("revit_", StringComparison.Ordinal) ? toolName.Substring(6) : toolName;
            var calls = new List<(string Command, object Args)>();
            var originalSend = ToolGateway.SendOverride;
            var originalConfig = ServerState.Config;
            ServerState.Config = new RvtMcpConfig();
            ToolGateway.SendOverride = (command, args, timeout) =>
            {
                calls.Add((command, args));
                return Task.FromResult(new JObject { ["tool_contract_probe"] = toolName, ["items"] = new JArray(1, 2) });
            };
            try
            {
                var result = await Invoke(method);

                Assert.False(string.IsNullOrWhiteSpace(result), toolName + " returned nothing.");
                Assert.True(calls.Count > 0,
                    toolName + " never reached the plug-in with the probe arguments. It returned: " + Shorten(result));
                Assert.Contains(calls, c => c.Command == wire || (wire == "batch_execute"));
                Assert.Contains("tool_contract_probe", result);
            }
            finally
            {
                ToolGateway.SendOverride = originalSend;
                ServerState.Config = originalConfig;
            }
        }

        private static async Task<string> Invoke(MethodInfo method)
        {
            var toolName = method.GetCustomAttribute<McpServerToolAttribute>().Name;
            var args = method.GetParameters()
                .Select(p => Shaped.TryGetValue(toolName + ":" + p.Name, out var shaped) ? shaped : Probe(p))
                .ToArray();
            var target = method.IsStatic ? null : Activator.CreateInstance(method.DeclaringType, true);
            var returned = method.Invoke(target, args);
            if (returned is Task<string> task) return await task;
            if (returned is Task plain) { await plain; return string.Empty; }
            return returned as string ?? returned?.ToString();
        }

        // Probe values stay inside each parameter's own contract: optional parameters keep their default,
        // identifiers are positive, JSON-text parameters carry a minimal valid array.
        private static object Probe(ParameterInfo parameter)
        {
            if (parameter.HasDefaultValue && parameter.DefaultValue != null && parameter.DefaultValue != DBNull.Value)
                return parameter.DefaultValue;
            var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
            if (parameter.HasDefaultValue && Nullable.GetUnderlyingType(parameter.ParameterType) != null && !parameter.Name.EndsWith("_mm", StringComparison.Ordinal))
                return null;
            var name = parameter.Name ?? string.Empty;
            if (type == typeof(string))
            {
                if (parameter.HasDefaultValue) return null;
                if (name.EndsWith("Json", StringComparison.OrdinalIgnoreCase) || name.EndsWith("ids", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("_json", StringComparison.OrdinalIgnoreCase))
                    return "[1]";
                if (name.IndexOf("path", StringComparison.OrdinalIgnoreCase) >= 0) return @"C:\probe\file.dat";
                return "probe";
            }
            if (type == typeof(bool)) return false;
            if (type == typeof(int)) return 1;
            if (type == typeof(long)) return 1L;
            if (type == typeof(double)) return 1.0;
            if (type.IsEnum) return Enum.GetValues(type).GetValue(0);
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

        private static string Shorten(string text) => text.Length <= 200 ? text : text.Substring(0, 200) + "...";
    }
}
