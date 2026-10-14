using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RvtMcp.Server;
using RvtMcp.Tests.Helpers;
using Xunit;

namespace RvtMcp.Tests
{
    public class RoomsToolShellTests
    {
        private static readonly string[] RoomsToolsInClass =
        {
            "revit_auto_create_rooms_from_walls",
            "revit_compute_room_finishes",
            "revit_create_area",
            "revit_create_room_separator",
            "revit_create_space",
            "revit_get_room_boundaries",
            "revit_get_room_openings",
            "revit_list_areas",
            "revit_list_rooms",
            "revit_tag_all_areas"
        };

        [Fact]
        public void Every_rooms_tool_is_listed_for_a_shell_case()
        {
            var declared = typeof(RoomsTools)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
                .Where(name => name != null)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(RoomsToolsInClass, declared);
        }

        [Fact]
        public async Task List_rooms_sends_filters_snake_case()
        {
            var sent = await Capture.Send(() => RoomsTools.ListRooms("L1", "New", "placed", true, 100));

            Assert.Equal("list_rooms", sent.Command);
            var json = sent.Json();
            Assert.Equal("L1", json.Value<string>("level_name"));
            Assert.Equal("New", json.Value<string>("phase_name"));
            Assert.Equal("placed", json.Value<string>("status"));
            Assert.True(json.Value<bool>("include_parameters"));
            Assert.Equal(100, json.Value<int>("limit"));
        }

        [Fact]
        public async Task Get_room_boundaries_sends_room_id()
        {
            var sent = await Capture.Send(() => RoomsTools.GetRoomBoundaries(42, "center", false));

            Assert.Equal("get_room_boundaries", sent.Command);
            var json = sent.Json();
            Assert.Equal(42, json.Value<long>("room_id"));
            Assert.Equal("center", json.Value<string>("boundary_location"));
            Assert.False(json.Value<bool>("include_boundary_elements"));
        }

        [Fact]
        public async Task Get_room_openings_sends_flags()
        {
            var sent = await Capture.Send(() => RoomsTools.GetRoomOpenings(42, false, true));

            Assert.Equal("get_room_openings", sent.Command);
            var json = sent.Json();
            Assert.Equal(42, json.Value<long>("room_id"));
            Assert.False(json.Value<bool>("include_doors"));
            Assert.True(json.Value<bool>("include_windows"));
        }

        [Fact]
        public async Task Create_room_separator_normalizes_points()
        {
            var points = JsonDocument.Parse("[{\"x\":0,\"y\":0},{\"x\":100,\"y\":0}]").RootElement;
            var sent = await Capture.Send(() => RoomsTools.CreateRoomSeparator(new object[] { points }, 7, "L1", true));

            Assert.Equal("create_room_separator", sent.Command);
            var json = sent.Json();
            Assert.Equal(7, json.Value<long>("view_id"));
            Assert.Equal("L1", json.Value<string>("level_name"));
            Assert.True(json.Value<bool>("close_loop"));
            Assert.NotNull(json["points"]);
        }

        [Fact]
        public async Task Create_area_sends_options()
        {
            var sent = await Capture.Send(() => RoomsTools.CreateArea(1, 2, 9, "AP", "AS", "L1", true, "N", "01"));

            Assert.Equal("create_area", sent.Command);
            var json = sent.Json();
            Assert.Equal(9, json.Value<long>("area_plan_view_id"));
            Assert.Equal("AP", json.Value<string>("area_plan_view_name"));
            Assert.Equal("AS", json.Value<string>("area_scheme_name"));
            Assert.True(json.Value<bool>("create_area_plan_if_missing"));
            Assert.Equal("N", json.Value<string>("name"));
            Assert.Equal("01", json.Value<string>("number"));
        }

        [Fact]
        public async Task Create_space_sends_level_and_phase()
        {
            var sent = await Capture.Send(() => RoomsTools.CreateSpace(1, 2, "L1", "P1", "S", "02"));

            Assert.Equal("create_space", sent.Command);
            var json = sent.Json();
            Assert.Equal("L1", json.Value<string>("level_name"));
            Assert.Equal("P1", json.Value<string>("phase_name"));
            Assert.Equal("S", json.Value<string>("name"));
            Assert.Equal("02", json.Value<string>("number"));
        }

        [Fact]
        public async Task List_areas_sends_filters()
        {
            var sent = await Capture.Send(() => RoomsTools.ListAreas("Scheme", "L1", "not_enclosed", 200));

            Assert.Equal("list_areas", sent.Command);
            var json = sent.Json();
            Assert.Equal("Scheme", json.Value<string>("area_scheme_name"));
            Assert.Equal("L1", json.Value<string>("level_name"));
            Assert.Equal("not_enclosed", json.Value<string>("status"));
            Assert.Equal(200, json.Value<int>("limit"));
        }

        [Fact]
        public async Task Compute_room_finishes_sends_ids_and_output()
        {
            var sent = await Capture.Send(() => RoomsTools.ComputeRoomFinishes(new long[] { 1, 2 }, "L1", false, 30, "file"));

            Assert.Equal("compute_room_finishes", sent.Command);
            var json = sent.Json();
            Assert.Equal(2, json["room_ids"].Count());
            Assert.Equal("L1", json.Value<string>("level_name"));
            Assert.False(json.Value<bool>("include_empty"));
            Assert.Equal(30, json.Value<int>("limit"));
            Assert.Equal("file", json.Value<string>("output"));
        }

        [Fact]
        public async Task Auto_create_rooms_sends_dry_run()
        {
            var sent = await Capture.Send(() => RoomsTools.AutoCreateRoomsFromWalls("L1", "P", "Rm", "N-", 10, true, 100));

            Assert.Equal("auto_create_rooms_from_walls", sent.Command);
            var json = sent.Json();
            Assert.Equal("L1", json.Value<string>("level_name"));
            Assert.Equal("Rm", json.Value<string>("name_prefix"));
            Assert.Equal("N-", json.Value<string>("number_prefix"));
            Assert.Equal(10, json.Value<int>("start_number"));
            Assert.True(json.Value<bool>("dry_run"));
            Assert.Equal(100, json.Value<int>("limit"));
        }

        [Fact]
        public async Task Tag_all_areas_sends_scope()
        {
            var sent = await Capture.Send(() => RoomsTools.TagAllAreas(9, "AP", false, 11, 50));

            Assert.Equal("tag_all_areas", sent.Command);
            var json = sent.Json();
            Assert.Equal(9, json.Value<long>("area_plan_view_id"));
            Assert.Equal("AP", json.Value<string>("area_plan_view_name"));
            Assert.False(json.Value<bool>("skip_existing"));
            Assert.Equal(11, json.Value<long>("tag_type_id"));
            Assert.Equal(50, json.Value<int>("limit"));
        }
    }
}
