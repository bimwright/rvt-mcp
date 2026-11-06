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
    public class ViewToolShellTests
    {
        private static readonly string[] ViewToolsInClass =
        {
            "revit_activate_view",
            "revit_analyze_sheet_layout",
            "revit_capture_view_image",
            "revit_create_camera_view",
            "revit_create_view",
            "revit_place_view_on_sheet",
            "revit_set_view_crop",
            "revit_set_view_orientation",
            "revit_set_view_scale",
            "revit_set_view_style",
            "revit_show_element_in_view"
        };

        [Fact]
        public void Every_view_tool_is_listed_for_a_shell_case()
        {
            var declared = typeof(ViewTools)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
                .Where(name => name != null)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(ViewToolsInClass, declared);
        }

        [Fact]
        public async Task Create_view_sends_camel_case()
        {
            var sent = await Capture.Send(() => ViewTools.CreateView("floorplan", "L1", "VP"));

            Assert.Equal("create_view", sent.Command);
            var json = sent.Json();
            Assert.Equal("floorplan", json.Value<string>("viewType"));
            Assert.Equal("L1", json.Value<string>("level"));
            Assert.Equal("VP", json.Value<string>("name"));
        }

        [Fact]
        public async Task Place_view_on_sheet_sends_ids()
        {
            var sent = await Capture.Send(() => ViewTools.PlaceViewOnSheet(11, 22, "A1", "S"));

            Assert.Equal("place_view_on_sheet", sent.Command);
            var json = sent.Json();
            Assert.Equal(11, json.Value<long>("viewId"));
            Assert.Equal(22, json.Value<long>("sheetId"));
            Assert.Equal("A1", json.Value<string>("sheetNumber"));
            Assert.Equal("S", json.Value<string>("sheetName"));
        }

        [Fact]
        public async Task Analyze_sheet_layout_sends_mixed_case()
        {
            var sent = await Capture.Send(() => ViewTools.AnalyzeSheetLayout("A103", 8736626, 2, 10));

            Assert.Equal("analyze_sheet_layout", sent.Command);
            var json = sent.Json();
            Assert.Equal("A103", json.Value<string>("sheetNumber"));
            Assert.Equal(8736626, json.Value<long>("sheetId"));
            Assert.Equal(2, json.Value<int>("start_viewport"));
            Assert.Equal(10, json.Value<int>("max_viewports"));
        }

        [Fact]
        public async Task Capture_view_image_sends_snake_case()
        {
            var sent = await Capture.Send(() => ViewTools.CaptureViewImage(null, 10449048, 800, "jpeg"));

            Assert.Equal("capture_view_image", sent.Command);
            var json = sent.Json();
            Assert.Equal(10449048, json.Value<long>("view_id"));
            Assert.Equal(800, json.Value<int>("pixel_size"));
            Assert.Equal("jpeg", json.Value<string>("image_format"));
        }

        [Fact]
        public async Task Set_view_crop_parses_bounds_json()
        {
            var sent = await Capture.Send(() => ViewTools.SetViewCrop(
                1, true, false, "{\"min\":[0,0,0],\"max\":[1,1,1]}", new long[] { 9 }, 50));

            Assert.Equal("set_view_crop", sent.Command);
            var json = sent.Json();
            Assert.Equal(1, json.Value<long>("view_id"));
            Assert.True(json.Value<bool>("enabled"));
            Assert.False(json.Value<bool>("visible"));
            Assert.Equal(0, json["bounds"]["min"][0].ToObject<double>());
            Assert.Single(json["fit_element_ids"]);
            Assert.Equal(50, json.Value<double>("padding_mm"));
        }

        [Fact]
        public async Task Set_view_scale_sends_scale()
        {
            var sent = await Capture.Send(() => ViewTools.SetViewScale(50, 7));

            Assert.Equal("set_view_scale", sent.Command);
            var json = sent.Json();
            Assert.Equal(50, json.Value<int>("scale"));
            Assert.Equal(7, json.Value<long>("view_id"));
        }

        [Fact]
        public async Task Activate_view_sends_selector()
        {
            var sent = await Capture.Send(() => ViewTools.ActivateView(null, "V1"));

            Assert.Equal("activate_view", sent.Command);
            var json = sent.Json();
            Assert.Null(json["view_id"]);
            Assert.Equal("V1", json.Value<string>("view_name"));
        }

        [Fact]
        public async Task Show_element_in_view_sends_flags()
        {
            var sent = await Capture.Send(() => ViewTools.ShowElementInView(new long[] { 1, 2 }, 3, false, true, false));

            Assert.Equal("show_element_in_view", sent.Command);
            var json = sent.Json();
            Assert.Equal(2, json["element_ids"].Count());
            Assert.Equal(3, json.Value<long>("view_id"));
            Assert.False(json.Value<bool>("activate_view"));
            Assert.True(json.Value<bool>("select"));
            Assert.False(json.Value<bool>("zoom"));
        }

        [Fact]
        public async Task Set_view_orientation_sends_points()
        {
            var sent = await Capture.Send(() => ViewTools.SetViewOrientation(new double[] { 1, 2, 3 }, new double[] { 4, 5, 6 }, view_name: "3D", perspective: true));

            Assert.Equal("set_view_orientation", sent.Command);
            var json = sent.Json();
            Assert.Equal("3D", json.Value<string>("view_name"));
            Assert.Equal(3, json["eye"].Value<double>(2));
            Assert.Equal(4, json["target"].Value<double>(0));
            Assert.True(json.Value<bool>("perspective"));
            // Omitted fit lets the add-in choose by view kind; an explicit value is forwarded.
            Assert.Null(json["fit"]);
            var explicitFit = await Capture.Send(() => ViewTools.SetViewOrientation(new double[] { 1, 2, 3 }, new double[] { 4, 5, 6 }, fit: false));
            Assert.False(explicitFit.Json().Value<bool>("fit"));
        }

        [Fact]
        public async Task Create_camera_view_sends_name_and_points()
        {
            var sent = await Capture.Send(() => ViewTools.CreateCameraView("Cam", new double[] { 0, 0, 1700 }, new double[] { 0, 1, 1700 }));

            Assert.Equal("create_camera_view", sent.Command);
            var json = sent.Json();
            Assert.Equal("Cam", json.Value<string>("name"));
            Assert.False(json.Value<bool>("far_clip"));
        }

        [Fact]
        public async Task Set_view_style_sends_style_and_detail()
        {
            var sent = await Capture.Send(() => ViewTools.SetViewStyle(view_id: 7, display_style: "shaded", detail_level: "fine"));

            Assert.Equal("set_view_style", sent.Command);
            var json = sent.Json();
            Assert.Equal(7, json.Value<long>("view_id"));
            Assert.Equal("shaded", json.Value<string>("display_style"));
            Assert.Equal("fine", json.Value<string>("detail_level"));
        }
    }
}
