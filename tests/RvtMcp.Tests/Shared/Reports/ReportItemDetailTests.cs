using System;
using System.Collections.Generic;
using System.Globalization;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Reports.Model;
using RvtMcp.Plugin.Reports.Presentation;
using Xunit;

namespace RvtMcp.Tests.Shared.Reports
{
    [Collection("L10n")]
    public class ReportItemDetailTests : IDisposable
    {
        public ReportItemDetailTests()
        {
            L.InitializeForTests(StringTable.Build("en", new Dictionary<string, string> {
                ["reports.time.today"] = "Today {time}", ["reports.time.yesterday"] = "Yesterday {time}",
                ["reports.outcome.completed"] = "Completed" }, null, null));
        }
        public void Dispose() { L.ResetForTests(); }

        [Fact]
        public void List_item_formats_local_created_time_and_tracks_any_matching_document()
        {
            var now = new DateTime(2026, 10, 28, 16, 0, 0, DateTimeKind.Local);
            var report = new AgentReport { Summary = "Synthetic report", Outcome = "completed", Documents = new List<ReportDocument> {
                new ReportDocument { Ref = "d0", Token = "family", Title = "Family" },
                new ReportDocument { Ref = "d1", Token = "project", Title = "Project" } } };
            var active = new ActiveDocumentIdentity { Token = "project" };
            report.CreatedUtc = Utc(now.Date.AddHours(9).AddMinutes(5));
            var item = new ReportListItemViewModel(report, () => active, () => now);
            Assert.Equal("Synthetic report", item.Summary);
            Assert.Equal("Completed", item.OutcomeLabel);
            Assert.Equal("Family", item.ModelName);
            Assert.False(item.IsOtherModel);
            Assert.Equal("Today 09:05", item.TimeText);
            report.CreatedUtc = Utc(now.Date.AddDays(-1).AddHours(23));
            Assert.Equal("Yesterday 23:00", item.TimeText);
            report.CreatedUtc = Utc(now.Date.AddDays(-2).AddHours(8));
            Assert.Equal("2026-10-26 08:00", item.TimeText);
            active = new ActiveDocumentIdentity { Token = "other" };
            var changed = false;
            item.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(item.IsOtherModel)) changed = true; };
            item.UpdateActiveDocument();
            Assert.True(changed);
            Assert.True(item.IsOtherModel);
        }

        [Fact]
        public void Detail_parses_stored_links_in_order_and_exposes_header()
        {
            var report = new AgentReport { ReportId = "r", TaskId = "task-synthetic", Summary = "Done", Outcome = "completed",
                CreatedUtc = Utc(new DateTime(2026, 10, 28, 10, 0, 0, DateTimeKind.Local)), Body = "See [first](revit:#0), [second](revit:#1)",
                Documents = new List<ReportDocument> { new ReportDocument { Ref = "d0", Token = "t0", Title = "Model A" }, new ReportDocument { Ref = "d1", Token = "t1", Title = "Model B" } },
                Links = new List<ReportLink> { new ReportLink { Doc = "d0", Label = "first" }, new ReportLink { Doc = "d1", Label = "second" } } };
            var vm = new ReportDetailViewModel(report, new FakeNavigation(), () => new ActiveDocumentIdentity { Token = "t1" },
                () => new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Local));
            Assert.Equal("Done", vm.Summary);
            Assert.Equal("task-synthetic", vm.TaskId);
            Assert.Equal("Model A", vm.ModelName);
            Assert.Equal("Today 10:00", vm.TimeText);
            Assert.Equal(2, vm.Body.Links.Count);
            Assert.Equal(0, vm.Body.Links[0].Syntax.StoredIndex);
            Assert.False(vm.Links[0].IsLive);
            Assert.True(vm.Links[1].IsLive);
            Assert.Equal("first", vm.Links[0].Label);
        }

        private static string Utc(DateTime local) => local.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
    }
}
