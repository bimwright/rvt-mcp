using System;
using RvtMcp.Plugin.Views.Toast;
using Xunit;

namespace RvtMcp.Tests
{
    public class ToastReportRouteTests
    {
        private const string ReportId = "0123456789abcdef0123456789abcdef";
        private const string ReportResult = "{\"report_id\":\"" + ReportId + "\",\"replaced\":false}";

        [Fact]
        public void Report_id_comes_only_from_a_successful_report_task_result()
        {
            Assert.Equal(ReportId, ToastReportRoute.ReportIdFrom("report_task_result", true, ReportResult));
            Assert.Null(ToastReportRoute.ReportIdFrom("report_task_result", false, ReportResult));
            Assert.Null(ToastReportRoute.ReportIdFrom("create_level", true, ReportResult));
            Assert.Null(ToastReportRoute.ReportIdFrom(null, true, ReportResult));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("[]")]
        [InlineData("{}")]
        [InlineData("{\"report_id\":42}")]
        [InlineData("{\"report_id\":{\"id\":\"x\"}}")]
        [InlineData("{\"report_id\":\"not-a-report-id\"}")]
        public void A_missing_or_malformed_report_id_routes_to_history(string resultJson)
        {
            Assert.Null(ToastReportRoute.ReportIdFrom("report_task_result", true, resultJson));
        }

        [Fact]
        public void A_click_on_the_card_whose_latest_result_is_the_report_opens_it()
        {
            var route = new ToastReportRoute();
            route.Recorded(3, ReportId);
            Assert.Equal(ReportId, route.ReportFor(3));
            Assert.Null(route.ReportFor(2));
        }

        [Fact]
        public void A_later_result_on_the_same_card_or_a_new_card_sends_clicks_to_history()
        {
            var route = new ToastReportRoute();
            route.Recorded(3, ReportId);
            route.Recorded(3, null);
            Assert.Null(route.ReportFor(3));

            route.Recorded(3, ReportId);
            route.Recorded(4, null);
            Assert.Null(route.ReportFor(3));
            Assert.Null(route.ReportFor(4));
        }

        [Fact]
        public void Nothing_recorded_means_history()
        {
            Assert.Null(new ToastReportRoute().ReportFor(0));
        }

        [Fact]
        public void Current_card_id_matches_the_id_a_click_reports()
        {
            var aggregator = new ActivityAggregator(() => 20, () => TimeSpan.Zero);
            aggregator.RecordResult("Report", "Moved 6 valves", true, null, true);
            var first = aggregator.CurrentCardId;
            Assert.Equal(aggregator.TakeRender().Card.CardId, first);

            aggregator.RecordResult("Create Level", "Level 2", true, null, true);
            Assert.Equal(first, aggregator.CurrentCardId); // same open card

            aggregator.Dismiss(first);
            aggregator.RecordResult("Report", "Next task", true, null, true);
            Assert.NotEqual(first, aggregator.CurrentCardId);
        }
    }
}
