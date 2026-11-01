using ModelContextProtocol.Protocol;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class AgentReportReminderTests
    {
        private static JObject Changes(int added, bool complete = true) => new JObject
        {
            ["complete"] = complete,
            ["documents"] = new JArray(new JObject
            {
                ["document"] = "DEMO-A.rvt",
                ["added"] = new JObject { ["count"] = added, ["ids"] = new JArray() },
                ["modified"] = new JObject { ["count"] = 0, ["ids"] = new JArray() },
                ["deleted"] = new JObject { ["count"] = 0, ["ids"] = new JArray() }
            })
        };

        private static JObject Call(AgentReportReminder reminder, string tool, JObject changes = null, string text = "{\"success\":true}", bool error = false)
        {
            var result = RuntimeToolFilter.Apply(new CallToolRequestParams { Name = tool },
                new CallToolResult { Content = new[] { new TextContentBlock { Text = text } }, IsError = error },
                new RvtMcpConfig(), changes: changes, reportReminder: reminder);
            var block = (TextContentBlock)result.Content[0];
            try { return JObject.Parse(block.Text); } catch { return new JObject { ["text"] = block.Text }; }
        }

        [Fact]
        public void Every_change_is_reminded_until_a_report_starts_the_next_job()
        {
            var reminder = new AgentReportReminder(() => true);
            Assert.Null(Call(reminder, "revit_get_current_view_info")["_report"]);
            Assert.Equal(AgentReportReminder.Text, Call(reminder, "revit_create_line_based_element", Changes(2))["_report"]?.ToString());
            Assert.Null(Call(reminder, "revit_get_element_details")["_report"]);
            Assert.Equal(AgentReportReminder.ShortText, Call(reminder, "revit_operate_element", Changes(1))["_report"]?.ToString());

            Call(reminder, "revit_report_task_result");
            Assert.Equal(AgentReportReminder.Text, Call(reminder, "revit_send_code_to_revit", Changes(1))["_report"]?.ToString());
        }

        [Fact]
        public void A_failed_report_leaves_the_report_due()
        {
            var reminder = new AgentReportReminder(() => true);
            Assert.NotNull(Call(reminder, "revit_create_level", Changes(1))["_report"]);
            Call(reminder, "revit_report_task_result", text: "Error: AGENT_REPORTS_CAPABILITY_REQUIRED", error: true);
            Assert.Equal(AgentReportReminder.ShortText, Call(reminder, "revit_create_level", Changes(1))["_report"]?.ToString());
        }

        [Fact]
        public void Only_a_change_or_an_incomplete_capture_reminds()
        {
            var reminder = new AgentReportReminder(() => true);
            Assert.Null(Call(reminder, "revit_operate_element", Changes(0))["_report"]);
            Assert.NotNull(Call(reminder, "revit_operate_element", Changes(0, complete: false))["_report"]);
        }

        [Fact]
        public void A_check_answer_reminds_once_per_job()
        {
            var reminder = new AgentReportReminder(() => true);
            Assert.Equal(AgentReportReminder.CheckText, Call(reminder, "revit_find_untagged_elements")["_report"]?.ToString());
            Assert.Null(Call(reminder, "revit_get_model_warnings_summary")["_report"]);
            Assert.Equal(AgentReportReminder.Text, Call(reminder, "revit_create_level", Changes(1))["_report"]?.ToString());

            Call(reminder, "revit_report_task_result");
            Assert.Null(Call(reminder, "revit_find_untagged_elements", text: "Error: No document is open.", error: true)["_report"]);
            Assert.NotNull(Call(reminder, "revit_clash_detection")["_report"]);
        }

        [Fact]
        public void A_check_after_a_change_adds_no_second_reminder()
        {
            var reminder = new AgentReportReminder(() => true);
            Assert.NotNull(Call(reminder, "revit_create_level", Changes(1))["_report"]);
            Assert.Null(Call(reminder, "revit_find_untagged_elements")["_report"]);
        }

        [Fact]
        public void No_reminder_without_report_support()
        {
            Assert.Null(Call(new AgentReportReminder(() => false), "revit_create_level", Changes(1))["_report"]);
            Assert.Null(Call(new AgentReportReminder(() => false), "revit_find_untagged_elements")["_report"]);
            Assert.Null(Call(null, "revit_create_level", Changes(1))["_report"]);
        }

        [Fact]
        public void A_failed_command_that_changed_the_model_reminds_too()
        {
            // A job that failed after changing the model is reported as failed.
            var failed = Call(new AgentReportReminder(() => true), "revit_create_level", Changes(1), text: "Error: boom", error: true);
            Assert.Equal("Error: boom", failed["error"]?.ToString());
            Assert.NotNull(failed["_report"]);
        }
    }
}
