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
    public class ScheduleToolShellTests
    {
        private static readonly string[] ScheduleToolsInClass =
        {
            "revit_add_schedule_field",
            "revit_apply_schedule_filter_sort",
            "revit_create_schedule",
            "revit_find_schedule_elements",
            "revit_get_schedulable_fields",
            "revit_get_schedule_data",
            "revit_get_schedule_definition",
            "revit_get_schedule_formulas",
            "revit_list_schedules",
            "revit_update_schedule_field"
        };

        [Fact]
        public void Every_schedule_tool_is_listed_for_a_shell_case()
        {
            var declared = typeof(ScheduleTools)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
                .Where(name => name != null)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(ScheduleToolsInClass, declared);
        }

        [Fact]
        public async Task List_schedules_sends_filters()
        {
            var sent = await Capture.Send(() => ScheduleTools.ListSchedules("Walls", "Takeoff"));

            Assert.Equal("list_schedules", sent.Command);
            var json = sent.Json();
            Assert.Equal("Walls", json.Value<string>("categoryFilter"));
            Assert.Equal("Takeoff", json.Value<string>("namePattern"));
        }

        [Fact]
        public async Task Get_schedule_definition_sends_id_and_name()
        {
            var sent = await Capture.Send(() => ScheduleTools.GetScheduleDefinition(42, "S"));

            Assert.Equal("get_schedule_definition", sent.Command);
            var json = sent.Json();
            Assert.Equal(42, json.Value<long>("scheduleId"));
            Assert.Equal("S", json.Value<string>("scheduleName"));
        }

        [Fact]
        public async Task Get_schedule_data_sends_pagination()
        {
            var sent = await Capture.Send(() => ScheduleTools.GetScheduleData(7, "", 5, 25, true));

            Assert.Equal("get_schedule_data", sent.Command);
            var json = sent.Json();
            Assert.Equal(7, json.Value<long>("scheduleId"));
            Assert.Equal(5, json.Value<int>("startRow"));
            Assert.Equal(25, json.Value<int>("maxRows"));
            Assert.True(json.Value<bool>("includeCellMeta"));
        }

        [Fact]
        public async Task Get_schedule_formulas_sends_selector()
        {
            var sent = await Capture.Send(() => ScheduleTools.GetScheduleFormulas(null, "Rebar"));

            Assert.Equal("get_schedule_formulas", sent.Command);
            var json = sent.Json();
            Assert.Null(json["scheduleId"]);
            Assert.Equal("Rebar", json.Value<string>("scheduleName"));
        }

        [Fact]
        public async Task Get_schedulable_fields_sends_kind_filter()
        {
            var sent = await Capture.Send(() => ScheduleTools.GetSchedulableFields(9, "", new[] { "Instance" }));

            Assert.Equal("get_schedulable_fields", sent.Command);
            var json = sent.Json();
            Assert.Equal(9, json.Value<long>("scheduleId"));
            Assert.Single(json["kindFilter"]);
        }

        [Fact]
        public async Task Find_schedule_elements_sends_options()
        {
            var sent = await Capture.Send(() => ScheduleTools.FindScheduleElements(9, "", false, true, 33));

            Assert.Equal("find_schedule_elements", sent.Command);
            var json = sent.Json();
            Assert.Equal(9, json.Value<long>("scheduleId"));
            Assert.False(json.Value<bool>("groupByCategory"));
            Assert.True(json.Value<bool>("includeParameters"));
            Assert.Equal(33, json.Value<int>("limit"));
        }

        [Fact]
        public async Task Create_schedule_parses_json_strings_into_arrays()
        {
            var sent = await Capture.Send(() => ScheduleTools.CreateSchedule(
                "Walls", "S1", "[{\"kind\":\"parameter\",\"parameterName\":\"Mark\"}]",
                "[{\"field\":\"Family\",\"op\":\"contains\",\"value\":\"X\"}]", "[]", false));

            Assert.Equal("create_schedule", sent.Command);
            var json = sent.Json();
            Assert.Equal("Walls", json.Value<string>("category"));
            Assert.Equal("S1", json.Value<string>("name"));
            Assert.Equal("parameter", json["fields"][0].Value<string>("kind"));
            Assert.Equal("Family", json["filters"][0].Value<string>("field"));
            Assert.False(json.Value<bool>("isItemized"));
        }

        [Fact]
        public async Task Add_schedule_field_parses_field_object()
        {
            var sent = await Capture.Send(() => ScheduleTools.AddScheduleField(
                "{\"kind\":\"formula\",\"name\":\"F\",\"formula\":\"Volume*2\"}", 5, "", 2, "Head", true, 12.5));

            Assert.Equal("add_schedule_field", sent.Command);
            var json = sent.Json();
            Assert.Equal(5, json.Value<long>("scheduleId"));
            Assert.Equal("formula", json["field"].Value<string>("kind"));
            Assert.Equal(2, json.Value<int>("insertIndex"));
            Assert.Equal("Head", json.Value<string>("columnHeading"));
            Assert.True(json.Value<bool>("hidden"));
            Assert.Equal(12.5, json.Value<double>("columnWidth"));
        }

        [Fact]
        public async Task Update_schedule_field_parses_ref_and_changes()
        {
            var sent = await Capture.Send(() => ScheduleTools.UpdateScheduleField(
                "{\"index\":0}", "{\"hidden\":true}", 5, ""));

            Assert.Equal("update_schedule_field", sent.Command);
            var json = sent.Json();
            Assert.Equal(0, json["fieldRef"].Value<int>("index"));
            Assert.True(json["changes"].Value<bool>("hidden"));
            Assert.Equal(5, json.Value<long>("scheduleId"));
        }

        [Fact]
        public async Task Apply_schedule_filter_sort_omits_unset_sections()
        {
            var sent = await Capture.Send(() => ScheduleTools.ApplyScheduleFilterSort(5, "", null, "[]", null));

            Assert.Equal("apply_schedule_filter_sort", sent.Command);
            var json = sent.Json();
            Assert.Equal(5, json.Value<long>("scheduleId"));
            Assert.Empty(json["sortGroup"]);
            Assert.True(json["filters"] == null || json["filters"].Type == Newtonsoft.Json.Linq.JTokenType.Null);
            Assert.True(json["settings"] == null || json["settings"].Type == Newtonsoft.Json.Linq.JTokenType.Null);
        }
    }
}
