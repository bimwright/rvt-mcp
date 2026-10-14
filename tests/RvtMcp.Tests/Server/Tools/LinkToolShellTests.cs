using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using RvtMcp.Server;
using RvtMcp.Tests.Helpers;
using Xunit;

namespace RvtMcp.Tests
{
    public class LinkToolShellTests
    {
        private static readonly string[] LinksToolsInClass =
        {
            "revit_acquire_coordinates_from_link",
            "revit_get_link_coordinate_system",
            "revit_get_link_elements",
            "revit_get_project_coordinate_system",
            "revit_import_cad_to_view",
            "revit_link_revit_model",
            "revit_list_linked_cad",
            "revit_list_linked_models",
            "revit_publish_coordinates_to_link",
            "revit_reload_link",
            "revit_set_project_base_point",
            "revit_unload_link"
        };

        [Fact]
        public void Every_links_tool_is_listed_for_a_shell_case()
        {
            var declared = typeof(LinksTools)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
                .Where(name => name != null)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(LinksToolsInClass, declared);
        }

        [Fact]
        public async Task List_linked_models_sends_flags()
        {
            var sent = await Capture.Send(() => LinksTools.ListLinkedModels(false, true));

            Assert.Equal("list_linked_models", sent.Command);
            var json = sent.Json();
            Assert.False(json.Value<bool>("include_instances"));
            Assert.True(json.Value<bool>("include_unloaded"));
        }

        [Fact]
        public async Task List_linked_cad_sends_flags()
        {
            var sent = await Capture.Send(() => LinksTools.ListLinkedCad(false, true));

            Assert.Equal("list_linked_cad", sent.Command);
            var json = sent.Json();
            Assert.False(json.Value<bool>("include_imports"));
            Assert.True(json.Value<bool>("include_links"));
        }

        [Fact]
        public async Task Import_cad_to_view_sends_snake_case()
        {
            var sent = await Capture.Send(() => LinksTools.ImportCadToView(
                @"C:\a.dwg", 7, true, "center", "mm", false, false));

            Assert.Equal("import_cad_to_view", sent.Command);
            var json = sent.Json();
            Assert.Equal(@"C:\a.dwg", json.Value<string>("path"));
            Assert.Equal(7, json.Value<long>("view_id"));
            Assert.True(json.Value<bool>("link"));
            Assert.Equal("center", json.Value<string>("placement"));
            Assert.Equal("mm", json.Value<string>("unit"));
            Assert.False(json.Value<bool>("this_view_only"));
            Assert.False(json.Value<bool>("visible_layers_only"));
        }

        [Fact]
        public async Task Link_revit_model_sends_timeout_separately()
        {
            var sent = await Capture.Send(() => LinksTools.LinkRevitModel(@"C:\m.rvt", "shared", true, true, 120));

            Assert.Equal("link_revit_model", sent.Command);
            var json = sent.Json();
            Assert.Equal(@"C:\m.rvt", json.Value<string>("path"));
            Assert.Equal("shared", json.Value<string>("placement"));
            Assert.True(json.Value<bool>("relative"));
            Assert.True(json.Value<bool>("reuse_existing_type"));
            Assert.Equal(120, sent.TimeoutSeconds);
        }

        [Fact]
        public async Task Unload_link_sends_ids_and_scope()
        {
            var sent = await Capture.Send(() => LinksTools.UnloadLink(11, 22, "myself"));

            Assert.Equal("unload_link", sent.Command);
            var json = sent.Json();
            Assert.Equal(11, json.Value<long>("link_type_id"));
            Assert.Equal(22, json.Value<long>("link_instance_id"));
            Assert.Equal("myself", json.Value<string>("scope"));
        }

        [Fact]
        public async Task Reload_link_sends_timeout()
        {
            var sent = await Capture.Send(() => LinksTools.ReloadLink(11, null, 300));

            Assert.Equal("reload_link", sent.Command);
            var json = sent.Json();
            Assert.Equal(11, json.Value<long>("link_type_id"));
            Assert.Null(json["link_instance_id"]);
            Assert.Equal(300, sent.TimeoutSeconds);
        }

        [Fact]
        public async Task Get_link_elements_sends_options()
        {
            var sent = await Capture.Send(() => LinksTools.GetLinkElements(99, "Walls", 50, true));

            Assert.Equal("get_link_elements", sent.Command);
            var json = sent.Json();
            Assert.Equal(99, json.Value<long>("link_instance_id"));
            Assert.Equal("Walls", json.Value<string>("category"));
            Assert.Equal(50, json.Value<int>("limit"));
            Assert.True(json.Value<bool>("include_bounding_box"));
        }

        [Fact]
        public async Task Get_project_coordinate_system_sends_empty_object()
        {
            var sent = await Capture.Send(() => LinksTools.GetProjectCoordinateSystem());

            Assert.Equal("get_project_coordinate_system", sent.Command);
            Assert.Empty(sent.Json());
        }

        [Fact]
        public async Task Get_link_coordinate_system_sends_id()
        {
            var sent = await Capture.Send(() => LinksTools.GetLinkCoordinateSystem(42));

            Assert.Equal("get_link_coordinate_system", sent.Command);
            Assert.Equal(42, sent.Json().Value<long>("link_instance_id"));
        }

        [Fact]
        public async Task Acquire_coordinates_sends_confirm()
        {
            var sent = await Capture.Send(() => LinksTools.AcquireCoordinatesFromLink(42, true));

            Assert.Equal("acquire_coordinates_from_link", sent.Command);
            var json = sent.Json();
            Assert.Equal(42, json.Value<long>("link_instance_id"));
            Assert.True(json.Value<bool>("confirm"));
        }

        [Fact]
        public async Task Publish_coordinates_sends_location_id()
        {
            var sent = await Capture.Send(() => LinksTools.PublishCoordinatesToLink(42, 21748, true));

            Assert.Equal("publish_coordinates_to_link", sent.Command);
            var json = sent.Json();
            Assert.Equal(42, json.Value<long>("link_instance_id"));
            Assert.Equal(21748, json.Value<long>("linked_project_location_id"));
            Assert.True(json.Value<bool>("confirm"));
        }

        [Fact]
        public async Task Set_project_base_point_sends_dry_run()
        {
            var sent = await Capture.Send(() => LinksTools.SetProjectBasePoint(1.5, 2.5, 3, 4, "survey_point", true));

            Assert.Equal("set_project_base_point", sent.Command);
            var json = sent.Json();
            Assert.Equal(1.5, json.Value<double>("east_west"));
            Assert.Equal(2.5, json.Value<double>("north_south"));
            Assert.Equal(3, json.Value<double>("elevation"));
            Assert.Equal(4, json.Value<double>("angle_to_true_north"));
            Assert.Equal("survey_point", json.Value<string>("point_kind"));
            Assert.True(json.Value<bool>("dry_run"));
        }
    }
}
