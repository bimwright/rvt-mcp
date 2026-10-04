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
    public class ExportToolShellTests
    {
        private static readonly string[] ExportToolsInClass =
        {
            "revit_batch_export_sheets",
            "revit_create_view_sheet_set",
            "revit_export_dgn",
            "revit_export_dwf",
            "revit_export_dwg",
            "revit_export_elements_data",
            "revit_export_fbx",
            "revit_export_gbxml",
            "revit_export_ifc",
            "revit_export_image",
            "revit_export_nwc",
            "revit_export_pdf",
            "revit_export_room_data",
            "revit_export_schedule_csv",
            "revit_get_print_settings",
            "revit_list_export_settings"
        };

        [Fact]
        public void Every_export_tool_is_listed_for_a_shell_case()
        {
            var declared = typeof(ExportTools)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
                .Where(name => name != null)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(ExportToolsInClass, declared);
        }

        [Fact]
        public async Task Export_room_data_sends_output_mode()
        {
            var sent = await Capture.Send(() => ExportTools.ExportRoomData("file"));

            Assert.Equal("export_room_data", sent.Command);
            Assert.Equal("file", sent.Json().Value<string>("output"));
        }

        [Fact]
        public async Task Export_pdf_sends_snake_case()
        {
            var sent = await Capture.Send(() => ExportTools.ExportPdf(@"C:\out", new long[] { 7, 8 }, true, "set"));

            Assert.Equal("export_pdf", sent.Command);
            var json = sent.Json();
            Assert.Equal(@"C:\out", json.Value<string>("output_folder"));
            Assert.Equal(new long[] { 7, 8 }, json["view_ids"].ToObject<long[]>());
            Assert.True(json.Value<bool>("combine"));
            Assert.Equal("set", json.Value<string>("file_name"));
        }

        [Fact]
        public async Task Export_dwg_sends_settings_and_prefix()
        {
            var sent = await Capture.Send(() => ExportTools.ExportDwg(@"C:\out", new long[] { 7 }, "setup1", "pre"));

            Assert.Equal("export_dwg", sent.Command);
            var json = sent.Json();
            Assert.Equal(@"C:\out", json.Value<string>("output_folder"));
            Assert.Equal("setup1", json.Value<string>("settings_name"));
            Assert.Equal("pre", json.Value<string>("file_name_prefix"));
        }

        [Fact]
        public async Task Export_dgn_omits_null_view_ids()
        {
            var sent = await Capture.Send(() => ExportTools.ExportDgn(@"C:\out"));

            Assert.Equal("export_dgn", sent.Command);
            var json = sent.Json();
            Assert.Equal(@"C:\out", json.Value<string>("output_folder"));
            Assert.Null(json["view_ids"]);
            Assert.Equal("", json.Value<string>("file_name_prefix"));
        }

        [Fact]
        public async Task Export_dwf_sends_use_dwfx()
        {
            var sent = await Capture.Send(() => ExportTools.ExportDwf(@"C:\out", null, "plan", true));

            Assert.Equal("export_dwf", sent.Command);
            var json = sent.Json();
            Assert.Equal("plan", json.Value<string>("file_name"));
            Assert.True(json.Value<bool>("use_dwfx"));
        }

        [Fact]
        public async Task Export_ifc_sends_version()
        {
            var sent = await Capture.Send(() => ExportTools.ExportIfc(@"C:\out", "model", "IFC4"));

            Assert.Equal("export_ifc", sent.Command);
            var json = sent.Json();
            Assert.Equal("model", json.Value<string>("file_name"));
            Assert.Equal("IFC4", json.Value<string>("ifc_version"));
            Assert.Equal(600, sent.TimeoutSeconds);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(120)]
        [InlineData(900)]
        public async Task Export_ifc_forwards_custom_wait_budget(int timeout)
        {
            var sent = await Capture.Send(() => ExportTools.ExportIfc(@"C:\out", "model", "IFC4", timeout));
            Assert.Equal(timeout, sent.TimeoutSeconds);
            Assert.Null(sent.Json()["timeout_seconds"]); // The budget belongs to the wire envelope.
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(901)]
        [InlineData(600000)]
        public async Task Export_ifc_rejects_invalid_budget_before_dispatch(int timeout)
        {
            var calls = 0;
            await Capture.WithSendOverride((command, parameters, budget) =>
            {
                calls++;
                return Task.FromResult(new JObject());
            }, async () =>
            {
                var result = await ExportTools.ExportIfc(@"C:\out", "model", "IFC4", timeout);
                Assert.StartsWith("Error:", result);
                Assert.Contains("1 and 900", result);
            });
            Assert.Equal(0, calls);
        }

        [Fact]
        public async Task Export_nwc_sends_scope_view_id()
        {
            var sent = await Capture.Send(() => ExportTools.ExportNwc(@"C:\out", "model", 99));

            Assert.Equal("export_nwc", sent.Command);
            var json = sent.Json();
            Assert.Equal(99, json.Value<long>("export_scope_view_id"));
        }

        [Fact]
        public async Task Export_fbx_sends_view_id()
        {
            var sent = await Capture.Send(() => ExportTools.ExportFbx(@"C:\out", "model", 55));

            Assert.Equal("export_fbx", sent.Command);
            Assert.Equal(55, sent.Json().Value<long>("view_id"));
        }

        [Fact]
        public async Task Export_gbxml_sends_folder_and_name()
        {
            var sent = await Capture.Send(() => ExportTools.ExportGbxml(@"C:\out", "energy"));

            Assert.Equal("export_gbxml", sent.Command);
            var json = sent.Json();
            Assert.Equal(@"C:\out", json.Value<string>("output_folder"));
            Assert.Equal("energy", json.Value<string>("file_name"));
        }

        [Fact]
        public async Task Export_image_sends_output_path_and_format()
        {
            var sent = await Capture.Send(() => ExportTools.ExportImage(@"C:\out\v.png", 7, 1024, "png"));

            Assert.Equal("export_image", sent.Command);
            var json = sent.Json();
            Assert.Equal(@"C:\out\v.png", json.Value<string>("output_path"));
            Assert.Equal(7, json.Value<long>("view_id"));
            Assert.Equal(1024, json.Value<int>("pixel_size"));
            Assert.Equal("png", json.Value<string>("image_format"));
        }

        [Fact]
        public async Task Export_schedule_csv_sends_selector_and_delimiter()
        {
            var sent = await Capture.Send(() => ExportTools.ExportScheduleCsv(@"C:\out\s.csv", 44, "", ";"));

            Assert.Equal("export_schedule_csv", sent.Command);
            var json = sent.Json();
            Assert.Equal(@"C:\out\s.csv", json.Value<string>("output_path"));
            Assert.Equal(44, json.Value<long>("schedule_id"));
            Assert.Equal(";", json.Value<string>("delimiter"));
        }

        [Fact]
        public async Task Export_elements_data_sends_parameter_names()
        {
            var sent = await Capture.Send(() => ExportTools.ExportElementsData(
                "Walls", @"C:\out\w.csv", new[] { "Mark", "Type" }, "csv"));

            Assert.Equal("export_elements_data", sent.Command);
            var json = sent.Json();
            Assert.Equal("Walls", json.Value<string>("category"));
            Assert.Equal(@"C:\out\w.csv", json.Value<string>("output_path"));
            Assert.Equal(new[] { "Mark", "Type" }, json["parameter_names"].ToObject<string[]>());
            Assert.Equal("csv", json.Value<string>("format"));
        }

        [Fact]
        public async Task Batch_export_sheets_sends_ids_and_filter()
        {
            var sent = await Capture.Send(() => ExportTools.BatchExportSheets(
                @"C:\out", "dwg", new long[] { 3, 4 }, "A1"));

            Assert.Equal("batch_export_sheets", sent.Command);
            var json = sent.Json();
            Assert.Equal("dwg", json.Value<string>("format"));
            Assert.Equal(new long[] { 3, 4 }, json["sheet_ids"].ToObject<long[]>());
            Assert.Equal("A1", json.Value<string>("sheet_number_filter"));
        }

        [Fact]
        public async Task List_export_settings_sends_paging()
        {
            var sent = await Capture.Send(() => ExportTools.ListExportSettings("dwg", 10, 5));

            Assert.Equal("list_export_settings", sent.Command);
            var json = sent.Json();
            Assert.Equal("dwg", json.Value<string>("kind_filter"));
            Assert.Equal(10, json.Value<int>("start_index"));
            Assert.Equal(5, json.Value<int>("max_results"));
        }

        [Fact]
        public async Task Create_view_sheet_set_sends_name_and_ids()
        {
            var sent = await Capture.Send(() => ExportTools.CreateViewSheetSet("issue1", new long[] { 9 }));

            Assert.Equal("create_view_sheet_set", sent.Command);
            var json = sent.Json();
            Assert.Equal("issue1", json.Value<string>("name"));
            Assert.Equal(new long[] { 9 }, json["view_ids"].ToObject<long[]>());
        }

        [Fact]
        public async Task Get_print_settings_sends_kind_filter()
        {
            var sent = await Capture.Send(() => ExportTools.GetPrintSettings("print", 0, 20));

            Assert.Equal("get_print_settings", sent.Command);
            var json = sent.Json();
            Assert.Equal("print", json.Value<string>("kind_filter"));
            Assert.Equal(0, json.Value<int>("start_index"));
            Assert.Equal(20, json.Value<int>("max_results"));
        }
    }
}
