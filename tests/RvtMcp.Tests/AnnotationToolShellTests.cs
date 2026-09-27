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
    public class AnnotationToolShellTests
    {
        private static readonly string[] AnnotationToolsInClass =
        {
            "revit_apply_keynote_to_element",
            "revit_create_callout_view",
            "revit_create_detail_line",
            "revit_create_dimensions",
            "revit_create_filled_region",
            "revit_create_text_note",
            "revit_find_undimensioned_elements",
            "revit_find_untagged_elements",
            "revit_list_keynotes",
            "revit_tag_all_by_category",
            "revit_tag_all_rooms",
            "revit_tag_all_walls",
            "revit_tag_elements",
            "revit_wipe_empty_tags"
        };

        [Fact]
        public void Every_annotation_tool_is_listed_for_a_shell_case()
        {
            var declared = typeof(AnnotationTools)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
                .Where(name => name != null)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(AnnotationToolsInClass, declared);
        }

        [Fact]
        public async Task Tag_all_walls_and_rooms_send_no_payload()
        {
            var walls = await Capture.Send(() => AnnotationTools.TagAllWalls());
            Assert.Equal("tag_all_walls", walls.Command);

            var rooms = await Capture.Send(() => AnnotationTools.TagAllRooms());
            Assert.Equal("tag_all_rooms", rooms.Command);
        }

        [Fact]
        public async Task Tag_elements_sends_ids_and_style()
        {
            var sent = await Capture.Send(() => AnnotationTools.TagElements(new long[] { 1, 2 }, 7, 9, "Vertical", true, 1.5, 2.5));

            Assert.Equal("tag_elements", sent.Command);
            var json = sent.Json();
            Assert.Equal(2, json["element_ids"].Count());
            Assert.Equal(7, json.Value<long>("view_id"));
            Assert.Equal(9, json.Value<long>("tag_type_id"));
            Assert.Equal("Vertical", json.Value<string>("orientation"));
            Assert.True(json.Value<bool>("leader"));
            Assert.Equal(1.5, json.Value<double>("offset_x"));
        }

        [Fact]
        public async Task Tag_all_by_category_sends_dry_run()
        {
            var sent = await Capture.Send(() => AnnotationTools.TagAllByCategory("Detail Items", 7, 9, false, true, true, 50));

            Assert.Equal("tag_all_by_category", sent.Command);
            var json = sent.Json();
            Assert.Equal("Detail Items", json.Value<string>("category"));
            Assert.Equal(7, json.Value<long>("view_id"));
            Assert.False(json.Value<bool>("skip_existing"));
            Assert.True(json.Value<bool>("leader"));
            Assert.True(json.Value<bool>("dry_run"));
            Assert.Equal(50, json.Value<int>("limit"));
        }

        [Fact]
        public async Task Create_text_note_sends_geometry()
        {
            var sent = await Capture.Send(() => AnnotationTools.CreateTextNote("Hi", 1.5, 2.5, 7, 9, 10, 15));

            Assert.Equal("create_text_note", sent.Command);
            var json = sent.Json();
            Assert.Equal("Hi", json.Value<string>("text"));
            Assert.Equal(1.5, json.Value<double>("x"));
            Assert.Equal(7, json.Value<long>("view_id"));
            Assert.Equal(9, json.Value<long>("text_type_id"));
            Assert.Equal(15, json.Value<double>("rotation_deg"));
        }

        [Fact]
        public async Task Create_dimensions_normalizes_references()
        {
            var refs = JsonDocument.Parse("[{\"elementId\":1}]").RootElement;
            var sent = await Capture.Send(() => AnnotationTools.CreateDimensions(new object[] { refs }, 7, 9, null));

            Assert.Equal("create_dimensions", sent.Command);
            var json = sent.Json();
            Assert.NotNull(json["references"]);
            Assert.Equal(7, json.Value<long>("view_id"));
            Assert.Equal(9, json.Value<long>("dimension_type_id"));
        }

        [Fact]
        public async Task Create_filled_region_normalizes_points()
        {
            var pts = JsonDocument.Parse("[{\"x\":0,\"y\":0}]").RootElement;
            var sent = await Capture.Send(() => AnnotationTools.CreateFilledRegion(new object[] { pts }, 7, 9));

            Assert.Equal("create_filled_region", sent.Command);
            var json = sent.Json();
            Assert.NotNull(json["points"]);
            Assert.Equal(9, json.Value<long>("filled_region_type_id"));
        }

        [Fact]
        public async Task Create_detail_line_sends_coords()
        {
            var sent = await Capture.Send(() => AnnotationTools.CreateDetailLine(0, 0, 100, 50, 7, 9));

            Assert.Equal("create_detail_line", sent.Command);
            var json = sent.Json();
            Assert.Equal(100, json.Value<double>("end_x"));
            Assert.Equal(50, json.Value<double>("end_y"));
            Assert.Equal(7, json.Value<long>("view_id"));
            Assert.Equal(9, json.Value<long>("line_style_id"));
        }

        [Fact]
        public async Task Create_callout_view_sends_box()
        {
            var sent = await Capture.Send(() => AnnotationTools.CreateCalloutView(11, 0, 0, 10, 10, 22, "C1"));

            Assert.Equal("create_callout_view", sent.Command);
            var json = sent.Json();
            Assert.Equal(11, json.Value<long>("parent_view_id"));
            Assert.Equal(10, json.Value<double>("max_x"));
            Assert.Equal(22, json.Value<long>("view_family_type_id"));
            Assert.Equal("C1", json.Value<string>("name"));
        }

        [Fact]
        public async Task List_keynotes_sends_filters()
        {
            var sent = await Capture.Send(() => AnnotationTools.ListKeynotes("A", "door", 10));

            Assert.Equal("list_keynotes", sent.Command);
            var json = sent.Json();
            Assert.Equal("A", json.Value<string>("key_prefix"));
            Assert.Equal("door", json.Value<string>("search"));
            Assert.Equal(10, json.Value<int>("limit"));
        }

        [Fact]
        public async Task Apply_keynote_sends_dry_run()
        {
            var sent = await Capture.Send(() => AnnotationTools.ApplyKeynoteToElement(new long[] { 1 }, "K1", true));

            Assert.Equal("apply_keynote_to_element", sent.Command);
            var json = sent.Json();
            Assert.Single(json["element_ids"]);
            Assert.Equal("K1", json.Value<string>("keynote"));
            Assert.True(json.Value<bool>("dry_run"));
        }

        [Fact]
        public async Task Find_untagged_and_undimensioned_send_category()
        {
            var untagged = await Capture.Send(() => AnnotationTools.FindUntaggedElements("Walls", 7, 10));
            Assert.Equal("find_untagged_elements", untagged.Command);
            Assert.Equal("Walls", untagged.Json().Value<string>("category"));

            var undim = await Capture.Send(() => AnnotationTools.FindUndimensionedElements("Walls", 7, 10));
            Assert.Equal("find_undimensioned_elements", undim.Command);
            Assert.Equal(7, undim.Json().Value<long>("view_id"));
        }

        [Fact]
        public async Task Wipe_empty_tags_sends_dry_run()
        {
            var sent = await Capture.Send(() => AnnotationTools.WipeEmptyTags(7, true, 20));

            Assert.Equal("wipe_empty_tags", sent.Command);
            var json = sent.Json();
            Assert.Equal(7, json.Value<long>("view_id"));
            Assert.True(json.Value<bool>("dry_run"));
            Assert.Equal(20, json.Value<int>("limit"));
        }
    }
}
