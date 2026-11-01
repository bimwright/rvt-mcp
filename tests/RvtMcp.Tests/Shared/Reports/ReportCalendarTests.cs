using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Reports.Model;
using RvtMcp.Plugin.Reports.Presentation;
using Xunit;

namespace RvtMcp.Tests.Shared.Reports
{
    [Collection("L10n")]
    public sealed class ReportCalendarTests : IDisposable
    {
        private static readonly TimeZoneInfo East = TimeZoneInfo.CreateCustomTimeZone("Synthetic +07", TimeSpan.FromHours(7), "Synthetic +07", "Synthetic +07");
        private static readonly TimeZoneInfo West = TimeZoneInfo.CreateCustomTimeZone("Synthetic -05", TimeSpan.FromHours(-5), "Synthetic -05", "Synthetic -05");

        public ReportCalendarTests()
        {
            var english = EmbeddedCatalog.Load(typeof(ReportCalendarTests).Assembly, "en");
            L.InitializeForTests(StringTable.Build("en", english, null, null));
        }
        public void Dispose() => L.ResetForTests();

        private static AgentReport Report(string id, string created, string token = "model") => new AgentReport
        {
            ReportId = id, TaskId = id, Summary = id, Outcome = "completed", CreatedUtc = created, UpdatedUtc = created,
            Documents = new List<ReportDocument> { new ReportDocument { Token = token, Ref = "d0", Title = "Synthetic model" } }
        };
        private static FakeReportsRuntime Runtime(params AgentReport[] reports) => new FakeReportsRuntime
        {
            Reports = reports, LocalTimeZone = East, Now = new DateTime(2026, 10, 31, 12, 0, 0),
            ActiveDocument = new ActiveDocumentIdentity { Token = "model" }
        };

        [Fact]
        public async Task Groups_use_local_creation_days_with_one_header_and_filtered_counts()
        {
            var runtime = Runtime(Report("old", "2026-10-29T23:00:00Z"), Report("early", "2026-10-30T17:05:00Z"),
                Report("other", "2026-10-30T18:00:00Z", "other"), Report("latest", "2026-10-31T01:00:00Z"));
            using var vm = new ReportsViewModel(runtime, new FakeNavigation(), DisplayMode.Compact);
            await vm.RefreshAsync();

            Assert.Equal(new[] { "latest", "early", "old" }, vm.Reports.Select(r => r.ReportId));
            Assert.Equal("Today · 2 reports", vm.Reports[0].DayHeading);
            Assert.Equal("", vm.Reports[1].DayHeading);
            Assert.Equal("Yesterday · 1 report", vm.Reports[2].DayHeading);
            Assert.Equal("00:05", vm.Reports[1].CompactTimeText);
            Assert.Contains("UTC+07:00", vm.Reports[1].TimeTooltip);
            Assert.Contains("Synthetic +07", vm.Reports[1].TimeTooltip);
            vm.Filter = ReportFilter.All;
            Assert.Equal("Today · 3 reports", vm.Reports[0].DayHeading);
            Assert.Equal(2, vm.Reports.Count(r => r.HasDayHeading));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("bad")]
        [InlineData("2026-10-31")]
        [InlineData("14:00")]
        [InlineData("2026-10-31T14:00:00")]
        [InlineData("2026-13-31T14:00:00Z")]
        public async Task Invalid_timestamps_have_an_unknown_group_without_guessing(string timestamp)
        {
            var runtime = Runtime(Report("unknown", timestamp), Report("known", "2026-10-31T01:00:00Z"));
            using var vm = new ReportsViewModel(runtime, new FakeNavigation(), DisplayMode.Compact);
            await vm.RefreshAsync();
            Assert.Equal("unknown", vm.Reports.Last().ReportId);
            Assert.Equal("Unknown date · 1 report", vm.Reports.Last().DayHeading);
            Assert.Equal("—", vm.Reports.Last().CompactTimeText);
        }

        [Fact]
        public async Task Midnight_and_time_zone_refresh_keep_selection_and_do_not_request_navigation()
        {
            var runtime = Runtime(Report("a", "2026-10-30T18:00:00Z"));
            using var vm = new ReportsViewModel(runtime, new FakeNavigation(), DisplayMode.Compact);
            await vm.RefreshAsync();
            vm.SelectedReport = vm.Reports[0];
            var item = vm.SelectedReport;
            var detail = vm.SelectedDetail;
            var reveals = 0;
            vm.RevealRequested += (_, __) => reveals++;
            runtime.Now = new DateTime(2026, 11, 1, 0, 1, 0);
            vm.RefreshDates();
            Assert.Equal("Yesterday · 1 report", item.DayHeading);
            runtime.LocalTimeZone = West;
            runtime.Now = new DateTime(2026, 10, 30, 13, 0, 0);
            vm.RefreshDates();
            Assert.Equal(new DateTime(2026, 10, 30), item.LocalDay);
            Assert.Equal("Today · 1 report", item.DayHeading);
            Assert.Equal("13:00", item.CompactTimeText);
            Assert.Contains("UTC-05:00", item.TimeTooltip);
            Assert.Same(item, vm.SelectedReport);
            Assert.Same(detail, vm.SelectedDetail);
            Assert.Equal(0, reveals);
        }

        [Fact]
        public async Task Updated_report_stays_in_its_creation_group_and_reuses_only_compatible_reading_state()
        {
            var original = Report("old", "2026-10-28T10:00:00Z");
            original.Body = "## Section\n\nOriginal content.";
            var runtime = Runtime(original);
            using var vm = new ReportsViewModel(runtime, new FakeNavigation(), DisplayMode.Compact);
            await vm.RefreshAsync();
            vm.SelectedReport = vm.Reports.Single();
            vm.SelectedDetail.ExpandedBodySections[0] = false;
            vm.SelectedDetail.BodyTablePages[0] = 2;
            var updated = Report("old", original.CreatedUtc);
            updated.Body = original.Body;
            updated.UpdatedUtc = "2026-10-31T01:00:00Z";
            runtime.Reports = new[] { Report("new", "2026-10-31T02:00:00Z"), updated };
            await vm.RefreshAsync();

            Assert.Equal("old", vm.Reports.Last().ReportId);
            Assert.Equal(new DateTime(2026, 10, 28), vm.SelectedReport.LocalDay);
            Assert.True(vm.SelectedReport.IsUpdated);
            Assert.Contains("Updated", vm.SelectedReport.UpdatedText);
            Assert.False(vm.SelectedDetail.ExpandedBodySections[0]);
            Assert.Equal(2, vm.SelectedDetail.BodyTablePages[0]);
            var changed = Report("old", original.CreatedUtc);
            changed.Body = "## A different section\n\nChanged content.";
            changed.UpdatedUtc = "2026-10-31T02:00:00Z";
            runtime.Reports = new[] { changed };
            await vm.RefreshAsync();
            Assert.Empty(vm.SelectedDetail.ExpandedBodySections);
            Assert.Empty(vm.SelectedDetail.BodyTablePages);
        }

        [Fact]
        public async Task Equivalent_timestamps_do_not_label_a_report_as_updated()
        {
            var report = Report("a", "2026-10-31T01:00:00Z");
            report.UpdatedUtc = "2026-10-31T08:00:00.0000000+07:00";
            using var vm = new ReportsViewModel(Runtime(report), new FakeNavigation(), DisplayMode.Compact);
            await vm.RefreshAsync();
            Assert.False(vm.Reports.Single().IsUpdated);
        }

        [Fact]
        public async Task Deleting_the_last_report_of_a_day_removes_that_group()
        {
            using var vm = new ReportsViewModel(Runtime(Report("today", "2026-10-31T01:00:00Z"), Report("yesterday", "2026-10-30T01:00:00Z")),
                new FakeNavigation(), DisplayMode.Compact);
            await vm.RefreshAsync();
            vm.SelectedReport = vm.Reports.Last();
            vm.DeleteCommand.Execute(null);
            vm.DeleteCommand.Execute(null);
            Assert.Equal("Today · 1 report", Assert.Single(vm.Reports).DayHeading);
            Assert.Null(vm.SelectedReport);
        }

        [Fact]
        public async Task Only_explicit_selection_and_open_requests_reveal_a_report()
        {
            var runtime = Runtime(Report("a", "2026-10-31T01:00:00Z"));
            using var vm = new ReportsViewModel(runtime, new FakeNavigation(), DisplayMode.Compact);
            await vm.RefreshAsync();
            var reveals = 0;
            vm.RevealRequested += (_, __) => reveals++;
            vm.SelectedReport = vm.Reports[0];
            vm.ShowReport("a");
            Assert.Equal(2, reveals);
            runtime.Reports = new[] { Report("b", "2026-10-31T02:00:00Z"), runtime.Reports[0] };
            await vm.RefreshAsync();
            vm.RefreshDates();
            Assert.Equal(2, reveals);
        }
    }
}
