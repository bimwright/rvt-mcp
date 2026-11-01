using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Plugin.Views.Toast;
using RvtMcp.Server;
using RvtMcp.Server.Bake;
using RvtMcp.Tests.Helpers;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("ServerStateConfig")]
    public sealed class ReportToolsTests
    {
        internal static Task<string> Report(string taskId, string outcome, string summary, string body = null)
            => ReportTools.ReportTaskResult(taskId, outcome, summary, body);

        [Theory]
        [InlineData(null)]
        [InlineData("not-a-report")]
        public void Invalid_history_identity_returns_error_text(string id)
            => Assert.Equal("Error: Invalid report_id.", ReportTools.GetReportHistory(id));

        [Theory]
        [InlineData(-1, 0, 5, 10)]
        [InlineData(0, -1, 5, 10)]
        [InlineData(0, 0, 0, 10)]
        [InlineData(0, 0, 11, 10)]
        [InlineData(0, 0, 5, 0)]
        [InlineData(0, 0, 5, 26)]
        public void Invalid_history_page_returns_error_text(int callOffset, int elementOffset, int callLimit, int elementLimit)
            => Assert.StartsWith("Error: Invalid History page;", ReportTools.GetReportHistory(Guid.NewGuid().ToString("N"), callOffset, elementOffset, callLimit, elementLimit));

        [Fact]
        public void Missing_history_report_returns_error_text()
            => Assert.Equal("Error: Report is missing or ambiguous.", ReportTools.GetReportHistory(Guid.NewGuid().ToString("N")));

        public static IEnumerable<object[]> InvalidReports()
        {
            yield return new object[] { null, "completed", "Done", null, "task_id must contain 1-80 characters." };
            yield return new object[] { "", "completed", "Done", null, "task_id must contain 1-80 characters." };
            yield return new object[] { new string('t', 81), "completed", "Done", null, "task_id must contain 1-80 characters." };
            yield return new object[] { "job-1", "unknown", "Done", null, "outcome must be completed, failed or cancelled." };
            yield return new object[] { "job-1", "completed", "", null, "summary must contain 1-120 characters." };
            yield return new object[] { "job-1", "completed", new string('s', 121), null, "summary must contain 1-120 characters." };
            yield return new object[] { "job-1", "completed", "first\nsecond", null, "summary must be one line." };
            yield return new object[] { "job-1", "completed", "first\u2028second", null, "summary must be one line." };
            yield return new object[] { "job-1", "completed", "Done", new string('é', 16385), "body must not exceed 32768 UTF-8 bytes." };
        }

        [Theory]
        [MemberData(nameof(InvalidReports))]
        public async Task Invalid_report_returns_error_without_dispatch(string taskId, string outcome, string summary, string body, string error)
        {
            var calls = 0;
            await Capture.WithSendOverride((_, __, ___) =>
            {
                calls++;
                return Task.FromResult(new JObject());
            }, async () =>
            {
                var result = await Report(taskId, outcome, summary, body);
                Assert.Equal("Error: " + error, result);
                Assert.Equal(0, calls);
            });
        }

        [Theory]
        [InlineData("completed")]
        [InlineData("failed")]
        [InlineData("cancelled")]
        public async Task Valid_reports_at_limits_are_forwarded(string outcome)
        {
            var sent = await Capture.Send(() => Report(new string('t', 80), outcome, new string('s', 120), new string('é', 16384)));
            Assert.Equal("report_task_result", sent.Command);
            Assert.Equal(outcome, sent.Json().Value<string>("outcome"));
            Assert.Equal(32768, System.Text.Encoding.UTF8.GetByteCount(sent.Json().Value<string>("body")));
        }

        [Fact]
        public async Task Absent_body_is_omitted_from_the_actual_wire_request()
        {
            using var wire = new ReportTestWire(agentReports: true);
            var context = TargetCallContext.Begin();
            try
            {
                var result = JObject.Parse(await Report("job-1", "completed", "Done"));
                Assert.True(result.Value<bool>("ok"));
            }
            finally { TargetCallContext.End(context); }

            var request = Assert.Single(wire.Requests);
            var parameters = (JObject)request["params"];
            Assert.Equal("job-1", parameters.Value<string>("task_id"));
            Assert.Equal("completed", parameters.Value<string>("outcome"));
            Assert.Equal("Done", parameters.Value<string>("summary"));
            Assert.False(parameters.ContainsKey("body"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Default_toolset_exposes_report_with_read_only_annotations(bool readOnly)
        {
            var config = new RvtMcpConfig { ReadOnly = readOnly };
            var methods = Program.ResolveRegisteredToolMethods(ToolsetFilter.Resolve(config), config);
            var method = Assert.Single(methods.Where(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == "revit_report_task_result"));
            var attr = method.GetCustomAttribute<McpServerToolAttribute>();
            Assert.True(attr.ReadOnly);
            Assert.False(attr.Destructive);
            Assert.True(attr.Idempotent);
            Assert.False(attr.OpenWorld);
            Assert.Equal(ToolActivityKind.Read, ToolActivityClassifier.Classify("report_task_result"));
        }

        [Fact]
        public async Task Read_only_report_needs_no_document_confirmation()
        {
            using var wire = new ReportTestWire(agentReports: true, readOnly: true, confirmDocument: false);
            var result = await wire.Send();
            Assert.True(result.Value<bool>("ok"));
            var request = Assert.Single(wire.Requests);
            Assert.Equal("report_task_result", request.Value<string>("command"));
            Assert.Null(request["expected_document_id"]);
        }

        [Fact]
        public void Report_is_excluded_from_adaptive_bake_usage()
        {
            var root = Path.Combine(Path.GetTempPath(), "rvt-report-bake-" + Guid.NewGuid().ToString("N"));
            var paths = new BakePaths(root);
            try
            {
                var logger = new UsageEventLogger(paths, new RvtMcpConfig { EnableAdaptiveBake = true });
                Assert.Null(logger.RecordToolCall("report_task_result", "{\"task_id\":\"job-1\"}", true));
                Assert.False(File.Exists(paths.UsageJsonl));
            }
            finally { TestDiagnostics.DeleteTempDirectory(root, "rvt-report-bake-"); }
        }
    }
}
