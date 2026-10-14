using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RvtMcp.Server;
using RvtMcp.Tests.Helpers;
using Xunit;

namespace RvtMcp.Tests
{
    public class MepToolShellTests
    {
        private static readonly string[] MepToolsInClass =
        {
            "revit_analyze_mep_network",
            "revit_connect_mep_elements",
            "revit_create_air_terminal",
            "revit_create_cable_tray",
            "revit_create_conduit",
            "revit_create_duct",
            "revit_create_lighting_fixture",
            "revit_create_mep_fitting",
            "revit_create_pipe",
            "revit_detect_system_elements",
            "revit_find_mep_disconnects",
            "revit_get_mep_element_connectors",
            "revit_get_panel_schedule",
            "revit_get_system_inventory",
            "revit_list_mep_systems",
            "revit_set_system_classification"
        };

        [Fact]
        public void Every_mep_tool_is_listed_for_a_shell_case()
        {
            var declared = typeof(MepTools)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
                .Where(name => name != null)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(MepToolsInClass, declared);
        }

        [Fact]
        public async Task Detect_system_elements_sends_paging()
        {
            var sent = await Capture.Send(() => MepTools.DetectSystemElements(42, 5, 100));

            Assert.Equal("detect_system_elements", sent.Command);
            var json = sent.Json();
            Assert.Equal(42, json.Value<long>("elementId"));
            Assert.Equal(5, json.Value<int>("start_element"));
            Assert.Equal(100, json.Value<int>("max_elements"));
        }

        [Fact]
        public async Task Create_duct_sends_geometry_and_types()
        {
            var sent = await Capture.Send(() => MepTools.CreateDuct(1, 2, 3, 4, 5, 6, 11, 12, 13, 200, 100, null));

            Assert.Equal("create_duct", sent.Command);
            var json = sent.Json();
            Assert.Equal(1, json.Value<double>("start_x"));
            Assert.Equal(6, json.Value<double>("end_z"));
            Assert.Equal(11, json.Value<long>("duct_type_id"));
            Assert.Equal(12, json.Value<long>("system_type_id"));
            Assert.Equal(13, json.Value<long>("level_id"));
            Assert.Equal(200, json.Value<double>("width"));
            Assert.Equal(100, json.Value<double>("height"));
            Assert.Null(json["diameter"]);
        }

        [Fact]
        public async Task Create_pipe_sends_connector_fields()
        {
            var sent = await Capture.Send(() => MepTools.CreatePipe(0, 0, 0, 1000, 0, 0, 9, 8, 7, 50, 6, 2));

            Assert.Equal("create_pipe", sent.Command);
            var json = sent.Json();
            Assert.Equal(9, json.Value<long>("pipe_type_id"));
            Assert.Equal(50, json.Value<double>("diameter"));
            Assert.Equal(6, json.Value<long>("start_element_id"));
            Assert.Equal(2, json.Value<int>("start_connector_id"));
        }

        [Fact]
        public async Task Create_cable_tray_and_conduit_send_snake_case()
        {
            var tray = await Capture.Send(() => MepTools.CreateCableTray(0, 0, 0, 1, 0, 0, 5, 6, 100, 50));
            Assert.Equal("create_cable_tray", tray.Command);
            Assert.Equal(5, tray.Json().Value<long>("cable_tray_type_id"));

            var conduit = await Capture.Send(() => MepTools.CreateConduit(0, 0, 0, 1, 0, 0, 5, 6, 25));
            Assert.Equal("create_conduit", conduit.Command);
            Assert.Equal(5, conduit.Json().Value<long>("conduit_type_id"));
            Assert.Equal(25, conduit.Json().Value<double>("diameter"));
        }

        [Fact]
        public async Task Create_terminal_and_fixture_send_type_and_host()
        {
            var terminal = await Capture.Send(() => MepTools.CreateAirTerminal(9, 1, 2, 3, 4, 5));
            Assert.Equal("create_air_terminal", terminal.Command);
            var tj = terminal.Json();
            Assert.Equal(9, tj.Value<long>("type_id"));
            Assert.Equal(4, tj.Value<long>("level_id"));
            Assert.Equal(5, tj.Value<long>("host_id"));

            var fixture = await Capture.Send(() => MepTools.CreateLightingFixture(9, 1, 2, 3, 4, 5));
            Assert.Equal("create_lighting_fixture", fixture.Command);
            Assert.Equal(9, fixture.Json().Value<long>("type_id"));
        }

        [Fact]
        public async Task List_mep_systems_sends_domain_filter()
        {
            var sent = await Capture.Send(() => MepTools.ListMepSystems("electrical", 20));

            Assert.Equal("list_mep_systems", sent.Command);
            var json = sent.Json();
            Assert.Equal("electrical", json.Value<string>("domain_filter"));
            Assert.Equal(20, json.Value<int>("limit"));
        }

        [Fact]
        public async Task Get_system_inventory_sends_selector()
        {
            var sent = await Capture.Send(() => MepTools.GetSystemInventory(7, "", true, 50));

            Assert.Equal("get_system_inventory", sent.Command);
            var json = sent.Json();
            Assert.Equal(7, json.Value<long>("system_id"));
            Assert.True(json.Value<bool>("include_parameters"));
            Assert.Equal(50, json.Value<int>("limit"));
        }

        [Fact]
        public async Task Get_mep_element_connectors_sends_id()
        {
            var sent = await Capture.Send(() => MepTools.GetMepElementConnectors(42));

            Assert.Equal("get_mep_element_connectors", sent.Command);
            Assert.Equal(42, sent.Json().Value<long>("element_id"));
        }

        [Fact]
        public async Task Connect_mep_elements_sends_both_sides()
        {
            var sent = await Capture.Send(() => MepTools.ConnectMepElements(1, 2, 0, 1));

            Assert.Equal("connect_mep_elements", sent.Command);
            var json = sent.Json();
            Assert.Equal(1, json.Value<long>("element_id_1"));
            Assert.Equal(2, json.Value<long>("element_id_2"));
            Assert.Equal(0, json.Value<long>("connector_index_1"));
            Assert.Equal(1, json.Value<long>("connector_index_2"));
        }

        [Fact]
        public async Task Create_mep_fitting_parses_connectors_json()
        {
            var sent = await Capture.Send(() => MepTools.CreateMepFitting("elbow", "[{\"elementId\":1,\"connectorIndex\":0},{\"elementId\":2,\"connectorIndex\":0}]"));

            Assert.Equal("create_mep_fitting", sent.Command);
            var json = sent.Json();
            Assert.Equal("elbow", json.Value<string>("fitting_kind"));
            Assert.Equal(2, json["connectors"].Count());
            Assert.Equal(1, json["connectors"][0].Value<long>("elementId"));
        }

        [Fact]
        public async Task Set_system_classification_sends_ids()
        {
            var sent = await Capture.Send(() => MepTools.SetSystemClassification(new long[] { 1, 2 }, 9));

            Assert.Equal("set_system_classification", sent.Command);
            var json = sent.Json();
            Assert.Equal(2, json["element_ids"].Count());
            Assert.Equal(9, json.Value<long>("system_id"));
        }

        [Fact]
        public async Task Get_panel_schedule_sends_paging()
        {
            var sent = await Capture.Send(() => MepTools.GetPanelSchedule(7, "", 2, 20));

            Assert.Equal("get_panel_schedule", sent.Command);
            var json = sent.Json();
            Assert.Equal(7, json.Value<long>("panel_id"));
            Assert.Equal(2, json.Value<int>("start_circuit"));
            Assert.Equal(20, json.Value<int>("max_circuits"));
        }

        [Fact]
        public async Task Find_mep_disconnects_sends_filters()
        {
            var sent = await Capture.Send(() => MepTools.FindMepDisconnects("duct", true, 100));

            Assert.Equal("find_mep_disconnects", sent.Command);
            var json = sent.Json();
            Assert.Equal("duct", json.Value<string>("domain_filter"));
            Assert.True(json.Value<bool>("view_only"));
            Assert.Equal(100, json.Value<int>("limit"));
        }

        [Fact]
        public async Task Analyze_mep_network_sends_selector()
        {
            var sent = await Capture.Send(() => MepTools.AnalyzeMepNetwork(null, "Sys1"));

            Assert.Equal("analyze_mep_network", sent.Command);
            var json = sent.Json();
            Assert.Null(json["system_id"]);
            Assert.Equal("Sys1", json.Value<string>("system_name"));
        }
    }
}
