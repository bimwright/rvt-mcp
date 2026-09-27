using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using RvtMcp.Server;
using RvtMcp.Tests.Helpers;
using Xunit;

namespace RvtMcp.Tests
{
    public class SheetsToolShellTests
    {
        private static readonly string[] SheetsToolsInClass =
        {
            "revit_assign_revision_to_sheet",
            "revit_create_placeholder_sheet",
            "revit_create_revision",
            "revit_create_sheet",
            "revit_duplicate_sheet",
            "revit_get_titleblock_parameters",
            "revit_list_revisions",
            "revit_list_sheets",
            "revit_list_titleblocks",
            "revit_place_schedule_on_sheet",
            "revit_renumber_sheets",
            "revit_set_titleblock_parameters"
        };

        [Fact]
        public void Every_sheets_tool_is_listed_for_a_shell_case()
        {
            var declared = typeof(SheetsTools)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
                .Where(name => name != null)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(SheetsToolsInClass, declared);
        }

        [Fact]
        public async Task Create_sheet_sends_number_and_titleblock()
        {
            var sent = await Capture.Send(() => SheetsTools.CreateSheet("A101", "Plan", 6330577, ""));

            Assert.Equal("create_sheet", sent.Command);
            var json = sent.Json();
            Assert.Equal("A101", json.Value<string>("sheet_number"));
            Assert.Equal("Plan", json.Value<string>("sheet_name"));
            Assert.Equal(6330577, json.Value<long>("title_block_type_id"));
        }

        [Fact]
        public async Task Duplicate_sheet_sends_options()
        {
            var sent = await Capture.Send(() => SheetsTools.DuplicateSheet("A102", 8736626, "", "Copy", "with_detailing", false, false, true));

            Assert.Equal("duplicate_sheet", sent.Command);
            var json = sent.Json();
            Assert.Equal("A102", json.Value<string>("new_sheet_number"));
            Assert.Equal(8736626, json.Value<long>("source_sheet_id"));
            Assert.Equal("Copy", json.Value<string>("new_sheet_name"));
            Assert.Equal("with_detailing", json.Value<string>("duplicate_view_option"));
            Assert.False(json.Value<bool>("include_schedules"));
            Assert.False(json.Value<bool>("include_revisions"));
            Assert.True(json.Value<bool>("reuse_views_when_allowed"));
        }

        [Fact]
        public async Task Create_placeholder_sheet_sends_number_and_name()
        {
            var sent = await Capture.Send(() => SheetsTools.CreatePlaceholderSheet("P1", "PH"));

            Assert.Equal("create_placeholder_sheet", sent.Command);
            var json = sent.Json();
            Assert.Equal("P1", json.Value<string>("sheet_number"));
            Assert.Equal("PH", json.Value<string>("sheet_name"));
        }

        [Fact]
        public async Task List_sheets_sends_filters()
        {
            var sent = await Capture.Send(() => SheetsTools.ListSheets("A", "Plan", false, true, false, 25));

            Assert.Equal("list_sheets", sent.Command);
            var json = sent.Json();
            Assert.Equal("A", json.Value<string>("number_filter"));
            Assert.Equal("Plan", json.Value<string>("name_pattern"));
            Assert.False(json.Value<bool>("include_revisions"));
            Assert.True(json.Value<bool>("include_viewports"));
            Assert.False(json.Value<bool>("include_placeholders"));
            Assert.Equal(25, json.Value<int>("limit"));
        }

        [Fact]
        public async Task Set_titleblock_parameters_sends_dict()
        {
            var sent = await Capture.Send(() => SheetsTools.SetTitleblockParameters(
                new System.Collections.Generic.Dictionary<string, object> { ["Drawn By"] = "X" }, 8736626, "", "instance"));

            Assert.Equal("set_titleblock_parameters", sent.Command);
            var json = sent.Json();
            Assert.Equal(8736626, json.Value<long>("sheet_id"));
            Assert.Equal("X", json["parameters"].Value<string>("Drawn By"));
            Assert.Equal("instance", json.Value<string>("target"));
        }

        [Fact]
        public async Task Get_titleblock_parameters_sends_target()
        {
            var sent = await Capture.Send(() => SheetsTools.GetTitleblockParameters(8736626, "", "both", false));

            Assert.Equal("get_titleblock_parameters", sent.Command);
            var json = sent.Json();
            Assert.Equal(8736626, json.Value<long>("sheet_id"));
            Assert.Equal("both", json.Value<string>("target"));
            Assert.False(json.Value<bool>("include_read_only"));
        }

        [Fact]
        public async Task List_titleblocks_sends_flags()
        {
            var sent = await Capture.Send(() => SheetsTools.ListTitleblocks("KEI", false, 10));

            Assert.Equal("list_titleblocks", sent.Command);
            var json = sent.Json();
            Assert.Equal("KEI", json.Value<string>("name_pattern"));
            Assert.False(json.Value<bool>("include_inactive"));
            Assert.Equal(10, json.Value<int>("limit"));
        }

        [Fact]
        public async Task Place_schedule_on_sheet_sends_coords_mm()
        {
            var sent = await Capture.Send(() => SheetsTools.PlaceScheduleOnSheet(100.5, 50.25, 8736626, "", 10310280, ""));

            Assert.Equal("place_schedule_on_sheet", sent.Command);
            var json = sent.Json();
            Assert.Equal(100.5, json.Value<double>("x_mm"));
            Assert.Equal(50.25, json.Value<double>("y_mm"));
            Assert.Equal(8736626, json.Value<long>("sheet_id"));
            Assert.Equal(10310280, json.Value<long>("schedule_id"));
        }

        [Fact]
        public async Task Create_revision_sends_fields()
        {
            var sent = await Capture.Send(() => SheetsTools.CreateRevision("Desc", "2026-01-01", "Owner", "Me", true));

            Assert.Equal("create_revision", sent.Command);
            var json = sent.Json();
            Assert.Equal("Desc", json.Value<string>("description"));
            Assert.Equal("2026-01-01", json.Value<string>("date"));
            Assert.Equal("Owner", json.Value<string>("issued_to"));
            Assert.Equal("Me", json.Value<string>("issued_by"));
            Assert.True(json.Value<bool>("issued"));
        }

        [Fact]
        public async Task Assign_revision_to_sheet_sends_ids_and_mode()
        {
            var sent = await Capture.Send(() => SheetsTools.AssignRevisionToSheet(49030, new long[] { 1, 2 }, null, "replace"));

            Assert.Equal("assign_revision_to_sheet", sent.Command);
            var json = sent.Json();
            Assert.Equal(49030, json.Value<long>("revision_id"));
            Assert.Equal(2, json["sheet_ids"].Count());
            Assert.Equal("replace", json.Value<string>("mode"));
        }

        [Fact]
        public async Task List_revisions_sends_flag()
        {
            var sent = await Capture.Send(() => SheetsTools.ListRevisions(false));

            Assert.Equal("list_revisions", sent.Command);
            Assert.False(sent.Json().Value<bool>("include_sheets"));
        }

        [Fact]
        public async Task Renumber_sheets_sends_dry_run_and_normalized_items()
        {
            var items = JsonDocument.Parse("[{\"sheet_id\":1,\"new_number\":\"N1\"}]").RootElement;
            var sent = await Capture.Send(() => SheetsTools.RenumberSheets(items, "A", "B", "P-", "-S", true));

            Assert.Equal("renumber_sheets", sent.Command);
            var json = sent.Json();
            Assert.Equal(JTokenType.Array, json["items"].Type);
            Assert.Equal(1, json["items"][0].Value<long>("sheet_id"));
            Assert.Equal("A", json.Value<string>("find"));
            Assert.Equal("B", json.Value<string>("replace"));
            Assert.Equal("P-", json.Value<string>("prefix"));
            Assert.Equal("-S", json.Value<string>("suffix"));
            Assert.True(json.Value<bool>("dry_run"));
        }
    }
}
