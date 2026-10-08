using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>
    /// Pins the paths of tool wrappers that the main wrapper golden does not reach: bad input that
    /// must fail inside the wrapper's try, read-only guards that answer before the wire, and timeout
    /// validation. Golden/server-tool-wrapper-edges.json was recorded before the shells were changed.
    /// Set RVTMCP_UPDATE_GOLDEN=1 to rewrite the file after an intended change.
    /// </summary>
    [Collection("ServerStateConfig")]
    public sealed class ServerToolWrapperEdgeGoldenTests
    {
        private sealed class Case
        {
            public string Id;
            public string Tool;
            public bool ReadOnly;
            public Dictionary<string, object> Args = new Dictionary<string, object>();
        }

        private static Case Bad(string tool, string parameter, object value) =>
            new Case { Id = tool + ":bad_" + parameter, Tool = tool, Args = { [parameter] = value } };

        private static Case ReadOnly(string tool, params (string Name, object Value)[] args)
        {
            var c = new Case { Id = tool + ":read_only" + string.Concat(args.Select(a => "_" + a.Name + "=" + a.Value)), Tool = tool, ReadOnly = true };
            foreach (var (name, value) in args) c.Args[name] = value;
            return c;
        }

        private static Case Timeout(string tool, int seconds) =>
            new Case { Id = tool + ":timeout_" + seconds, Tool = tool, Args = { ["timeout_seconds"] = seconds } };

        private static IEnumerable<Case> Cases()
        {
            // Input parsed or normalized inside the wrapper's try.
            yield return Bad("revit_create_dimensions", "references", null);
            yield return Bad("revit_create_dimensions", "line", 42);
            yield return Bad("revit_create_filled_region", "points", null);
            yield return Bad("revit_create_room_separator", "points", null);
            yield return Bad("revit_create_surface_based_element", "points", "not json");
            yield return Bad("revit_delete_element", "elementIds", "not json");
            yield return Bad("revit_operate_element", "elementIds", "not json");
            yield return Bad("revit_duplicate_family_type", "typeParameterOverrides", "not json");
            yield return Bad("revit_find_elements_in_volume", "volume", 42);
            yield return Bad("revit_create_view_filter", "rules", "not json");
            yield return Bad("revit_create_mep_fitting", "connectors", "not json");
            yield return Bad("revit_create_schedule", "fields", "not json");
            yield return Bad("revit_create_schedule", "filters", "not json");
            yield return Bad("revit_create_schedule", "sortGroup", "not json");
            yield return Bad("revit_add_schedule_field", "field", "not json");
            yield return Bad("revit_update_schedule_field", "fieldRef", "not json");
            yield return Bad("revit_update_schedule_field", "changes", "not json");
            yield return Bad("revit_apply_schedule_filter_sort", "filters", "not json");
            yield return Bad("revit_apply_schedule_filter_sort", "sortGroup", "not json");
            yield return Bad("revit_apply_schedule_filter_sort", "settings", "not json");
            yield return Bad("revit_set_titleblock_parameters", "parameters", null);
            yield return Bad("revit_renumber_sheets", "items", 42);
            yield return Bad("revit_set_view_crop", "bounds_json", "not json");

            // Arguments with optional-value expressions.
            yield return new Case { Id = "revit_set_filter_overrides:colors", Tool = "revit_set_filter_overrides",
                Args = { ["projectionLineColor"] = "#FF0000", ["cutLineColor"] = "" } };
            yield return new Case { Id = "revit_override_element_graphics:colors", Tool = "revit_override_element_graphics",
                Args = { ["surfaceForegroundColor"] = "#00FF00", ["cutLineColor"] = "" } };
            yield return new Case { Id = "revit_purge_unused:targets", Tool = "revit_purge_unused",
                Args = { ["targets"] = new[] { "materials" } } };
            yield return new Case { Id = "revit_set_view_crop:bounds", Tool = "revit_set_view_crop",
                Args = { ["bounds_json"] = "{\"min_x\":0}" } };

            // Read-only guards that answer before the wire.
            yield return ReadOnly("revit_open_model");
            yield return ReadOnly("revit_set_project_info");
            yield return ReadOnly("revit_capture_view_image");
            yield return ReadOnly("revit_set_view_crop");
            yield return ReadOnly("revit_set_view_scale");
            yield return ReadOnly("revit_activate_view");
            yield return ReadOnly("revit_show_element_in_view");
            yield return ReadOnly("revit_purge_unused", ("dry_run", false));
            yield return ReadOnly("revit_purge_unused", ("dry_run", true));
            yield return ReadOnly("revit_set_structural_load", ("action", "update"));
            yield return ReadOnly("revit_set_structural_load", ("action", "create"));

            // Long-run timeout validation.
            foreach (var tool in new[] { "revit_export_ifc", "revit_load_family_from_path", "revit_link_revit_model", "revit_reload_link", "revit_open_model" })
            {
                yield return Timeout(tool, 0);
                yield return Timeout(tool, 901);
                yield return Timeout(tool, 900);
            }
        }

        [Fact]
        public async Task Tool_wrapper_edge_paths_match_the_recorded_results()
        {
            var tools = EveryToolContractTests.AllTools().ToDictionary(t => t.Name, t => t.Method);
            var actual = new JObject();
            foreach (var c in Cases())
            {
                Assert.True(tools.ContainsKey(c.Tool), "Unknown tool " + c.Tool);
                var method = tools[c.Tool];
                var args = EveryToolContractTests.ProbeArguments(method);
                var parameters = method.GetParameters();
                foreach (var pair in c.Args)
                {
                    var index = Array.FindIndex(parameters, p => p.Name == pair.Key);
                    Assert.True(index >= 0, c.Id + ": no parameter " + pair.Key);
                    args[index] = pair.Value;
                }
                Assert.False(actual.ContainsKey(c.Id), "Duplicate case " + c.Id);
                actual[c.Id] = await Record(method, args, c.ReadOnly);
            }

            var path = Path.Combine(RepoRoot(), "tests", "RvtMcp.Tests", "Golden", "server-tool-wrapper-edges.json");
            var text = actual.ToString(Formatting.Indented).Replace("\r\n", "\n") + "\n";
            if (Environment.GetEnvironmentVariable("RVTMCP_UPDATE_GOLDEN") == "1")
                File.WriteAllText(path, text);

            Assert.True(File.Exists(path), "Missing golden file " + path);
            var expected = JObject.Parse(File.ReadAllText(path));
            actual = JObject.Parse(text);
            foreach (var property in expected.Properties())
                Assert.True(JToken.DeepEquals(property.Value, actual[property.Name]),
                    property.Name + " changed:\n" + actual[property.Name]);
            Assert.Equal(expected.Properties().Select(p => p.Name), actual.Properties().Select(p => p.Name));
        }

        private static async Task<JObject> Record(System.Reflection.MethodInfo method, object[] args, bool readOnly)
        {
            var calls = new JArray();
            var originalSend = ToolGateway.SendOverride;
            var originalConfig = ServerState.Config;
            ServerState.Config = new RvtMcpConfig { ReadOnly = readOnly };
            ToolGateway.SendOverride = (command, parameters, timeout) =>
            {
                calls.Add(new JObject
                {
                    ["command"] = command,
                    ["params"] = parameters == null ? null : JsonConvert.SerializeObject(parameters),
                    ["timeout"] = timeout,
                });
                return Task.FromResult(new JObject { ["golden_probe"] = true });
            };
            try
            {
                string result;
                try { result = await EveryToolContractTests.Invoke(method, args); }
                catch (Exception ex) { result = "THREW " + ex.GetType().Name + ": " + ex.Message; }
                return new JObject { ["calls"] = calls, ["result"] = result };
            }
            finally
            {
                ToolGateway.SendOverride = originalSend;
                ServerState.Config = originalConfig;
            }
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "server", "RvtMcp.Server.csproj")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        }
    }
}
