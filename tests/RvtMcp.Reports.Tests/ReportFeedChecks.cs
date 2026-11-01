using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RvtMcp.Plugin.Reports.Model;
using RvtMcp.Plugin.Reports.Presentation;
using RvtMcp.Plugin.Reports.Views;
using static H;

internal static class ReportFeedChecks
{
    public static void Groups()
    {
        var reports = new List<AgentReport>
        {
            DemoData.Report("day1", "completed", "Today first", DemoData.Local(0, 14, 0), DemoData.DocA()),
            DemoData.Report("day2", "completed", "Today second", DemoData.Local(0, 13, 0), DemoData.DocA()),
            DemoData.Report("day3", "completed", "Yesterday", DemoData.Local(-1, 14, 0), DemoData.DocA()),
            DemoData.Report("day4", "completed", "Unknown date", "not-a-date", DemoData.DocA())
        };
        using (var s = new Scenario(DisplayMode.Compact, reports: reports).Show())
        {
            var headers = Visuals<TextBlock>(s.Compact).Where(t => Equals(t.Tag, "ReportDayHeader") && t.IsVisible).ToList();
            Expect(headers.Any(), "Compact has date separators");
            Expect(headers[0].Text.Contains("Today") && headers[0].Text.Contains("2"), "one Today separator counts two reports");
            Expect(headers.All(t => !t.Focusable && !t.IsHitTestVisible), "day separators are not actions or keyboard stops");
            Expect(s.Compact.Feed.Items.Count == 4, "separators do not add reports to the feed");
            var first = Visuals<ReportCard>(s.Compact).First();
            Expect(Visuals<TextBlock>(first).Any(t => Equals(t.Tag, "ReportCardTime") && t.Text == "14:00" && t.ToolTip != null),
                "card has time-only text and a full timestamp tooltip");
            s.Compact.Feed.ScrollIntoView(s.Item("day4"));
            Pump();
            Expect(Visuals<TextBlock>(s.Compact).Any(t => Equals(t.Tag, "ReportDayHeader") && t.Text.Contains("Unknown")), "invalid timestamp has an unknown-date group");
        }
    }

    public static void Reading()
    {
        var longReport = DemoData.Reports().Single(r => r.ReportId == DemoData.LongReport);
        longReport.CreatedUtc = longReport.UpdatedUtc = DemoData.Local(0, 14, 32);
        using (var s = new Scenario(DisplayMode.Compact, reports: new List<AgentReport> { longReport }).Show())
        {
            s.Select(DemoData.LongReport);
            var scroll = InnerScroll(s.Compact.Feed);
            var card = Card(s, DemoData.LongReport);
            card.Header.Focus();
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 240);
            Pump();
            var top = Top(card, scroll);
            var body = s.Compact.OpenBody.Body.Document;
            var focused = Keyboard.FocusedElement;
            var reports = s.Runtime.Reports;
            reports.Insert(0, DemoData.Report("new-feed", "completed", "New report", DemoData.Local(0, 14, 59), DemoData.DocA()));
            s.Runtime.Reports = reports;
            s.Runtime.RaiseReportsChanged();
            s.WaitForRefresh();

            Expect(Near(Top(Card(s, DemoData.LongReport), scroll), top), "prepending a report preserves the reading card and pixel offset");
            Expect(ReferenceEquals(body, s.Compact.OpenBody.Body.Document), "new report keeps the open FlowDocument");
            Expect(ReferenceEquals(focused, Keyboard.FocusedElement), "new report preserves keyboard focus");
            var hint = Visuals<Button>(s.Compact).SingleOrDefault(b => Equals(b.Tag, "NewReportsButton"));
            Expect(hint != null && hint.IsVisible && hint.Content.ToString().Contains("1"), "new report offers an explicit jump button");
            Click(hint);
            Expect(Near(scroll.VerticalOffset, 0) && !hint.IsVisible, "jump button goes to the top and clears the hint");
            Expect(s.ViewModel.SelectedReport.ReportId == DemoData.LongReport, "jumping to new reports does not open a different report");
            reports = s.Runtime.Reports;
            reports.Insert(0, DemoData.Report("at-top", "completed", "Another new report", DemoData.Local(0, 14, 59), DemoData.DocA()));
            s.Runtime.Reports = reports;
            s.Runtime.RaiseReportsChanged();
            s.WaitForRefresh();
            Expect(Near(scroll.VerticalOffset, 0) && !hint.IsVisible, "arrivals at the top stay at the top without a notice");
            s.ViewModel.ShowReport(DemoData.LongReport);
            Pump();
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 200);
            Pump();
            var hiddenTop = Top(s.Compact.OpenBody.Body, scroll);
            s.Window.Hide();
            reports = s.Runtime.Reports;
            reports.Insert(0, DemoData.Report("while-hidden", "completed", "Arrived while hidden", DemoData.Local(0, 14, 59), DemoData.DocA()));
            s.Runtime.Reports = reports;
            s.Runtime.RaiseReportsChanged();
            s.WaitForRefresh();
            s.Window.Show();
            s.WaitForRefresh();
            Expect(Near(Top(s.Compact.OpenBody.Body, scroll), hiddenTop), "hidden watcher refresh restores reading position on show");
        }
    }

    public static void Updated()
    {
        using (var s = new Scenario(DisplayMode.Compact).Show())
        {
            s.Select(DemoData.LongReport);
            var scroll = InnerScroll(s.Compact.Feed);
            Card(s, DemoData.LongReport).Header.Focus();
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 240);
            Pump();
            var top = Top(s.Compact.OpenBody.Body, scroll);
            var reports = s.Runtime.Reports;
            var index = reports.FindIndex(r => r.ReportId == DemoData.LongReport);
            Expect(ReportJson.TryDeserialize(ReportJson.Serialize(reports[index]), out var updated, out _), "clone synthetic report");
            updated.UpdatedUtc = DemoData.Local(0, 14, 59);
            updated.Summary += " (updated)";
            reports[index] = updated;
            s.Runtime.Reports = reports;
            s.Runtime.RaiseReportsChanged();
            s.WaitForRefresh();

            Expect(s.ViewModel.SelectedReport.ReportId == DemoData.LongReport, "updated report stays selected");
            var actual = Top(s.Compact.OpenBody.Body, scroll);
            Expect(Near(actual, top), $"refreshing the selected report keeps its body position ({top:0.##} -> {actual:0.##}, offset {scroll.VerticalOffset:0.##})");
            Expect(Visuals<TextBlock>(Card(s, DemoData.LongReport)).Any(t => Equals(t.Tag, "ReportUpdated") && t.IsVisible), "updated report is labelled");
            Expect(Card(s, DemoData.LongReport).IsKeyboardFocusWithin, "updated report keeps keyboard focus in its card");
            s.Runtime.Reports = new List<AgentReport> { updated };
            s.Runtime.RaiseReportsChanged();
            s.WaitForRefresh();
            Card(s, DemoData.LongReport).Header.Focus();
            scroll.ScrollToTop();
            Pump();
            Expect(ReportJson.TryDeserialize(ReportJson.Serialize(updated), out var again, out _), "clone top report");
            again.UpdatedUtc = ReportJson.FormatUtc(DemoData.Now.AddMinutes(1));
            s.Runtime.Reports = new List<AgentReport> { again };
            s.Runtime.RaiseReportsChanged();
            s.WaitForRefresh();
            Expect(Near(scroll.VerticalOffset, 0) && Card(s, DemoData.LongReport).IsKeyboardFocusWithin,
                "updating at the top keeps both top position and live card focus");
        }
        using (var s = new Scenario(DisplayMode.Wide).Show())
        {
            s.Select(DemoData.LongReport);
            s.Wide.DetailScroll.ScrollToVerticalOffset(300);
            Pump();
            var offset = s.Wide.DetailScroll.VerticalOffset;
            var reports = s.Runtime.Reports;
            var index = reports.FindIndex(r => r.ReportId == DemoData.LongReport);
            Expect(ReportJson.TryDeserialize(ReportJson.Serialize(reports[index]), out var updated, out _), "clone synthetic wide report");
            updated.UpdatedUtc = DemoData.Local(0, 14, 59);
            updated.Summary += " (updated)";
            reports[index] = updated;
            s.Runtime.Reports = reports;
            s.Runtime.RaiseReportsChanged();
            s.WaitForRefresh();
            Expect(Near(s.Wide.DetailScroll.VerticalOffset, offset), "wide update preserves the body scroll position");
        }
    }

    public static void VirtualizedFeed()
    {
        var longBody = DemoData.Reports().Single(r => r.ReportId == DemoData.LongReport).Body;
        var reports = Enumerable.Range(0, 500).Select(i => DemoData.Report("feed-" + i, "completed", "Synthetic report " + i,
            ReportJson.FormatUtc(DemoData.Now.AddHours(-i)), DemoData.DocA(), body: i == 250 ? longBody : "Short content.")).ToList();
        ReportsViewModel disposed;
        using (var s = new Scenario(DisplayMode.Compact, reports: reports).Show())
        {
            disposed = s.ViewModel;
            Expect(s.Compact.Feed.Items.Count == 500 && VirtualizingPanel.GetIsVirtualizing(s.Compact.Feed), "500 reports use the virtualized feed");
            Expect(Visuals<ReportCard>(s.Compact).Count() < 60 && s.Compact.OpenBody == null, "only nearby cards are realized; closed bodies are not built");
            s.Select("feed-250");
            var scroll = InnerScroll(s.Compact.Feed);
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 160);
            Pump();
            var top = Top(s.Compact.OpenBody.Body, scroll);
            var document = s.Compact.OpenBody.Body.Document;
            var added = Enumerable.Range(1, 3).Select(i => DemoData.Report("arrival-" + i, "completed", "New report " + i,
                ReportJson.FormatUtc(DemoData.Now.AddMinutes(i)), DemoData.DocA())).ToList();
            s.Runtime.Reports = added.Concat(s.Runtime.Reports).Take(500).ToList();
            s.Runtime.RaiseReportsChanged();
            s.WaitForRefresh();
            Expect(s.ViewModel.Reports.Count == 500 && s.ViewModel.SelectedReport.ReportId == "feed-250", "capped arrivals keep the open report");
            Expect(ReferenceEquals(document, s.Compact.OpenBody.Body.Document) && Near(Top(s.Compact.OpenBody.Body, scroll), top), "500-report refresh preserves the reading body and offset");
            Expect(Visuals<Button>(s.Compact).Single(b => Equals(b.Tag, "NewReportsButton")).Content.ToString().Contains("3"), "a batch is counted once");
            s.Runtime.RaiseReportsChanged();
            s.WaitForRefresh();
            Expect(Visuals<Button>(s.Compact).Single(b => Equals(b.Tag, "NewReportsButton")).Content.ToString().Contains("3"), "an unchanged refresh does not count the batch twice");
            Expect(Visuals<ReportCard>(s.Compact).Count() < 60, "virtualization remains active after deep navigation");
            s.Runtime.Reports = s.Runtime.Reports.Where(r => r.ReportId != "feed-250").ToList();
            s.Runtime.RaiseReportsChanged();
            s.WaitForRefresh();
            Expect(s.ViewModel.SelectedReport == null && scroll.VerticalOffset > 0, "removing the anchor stays near surviving reports without opening another card");
            var headers = Visuals<TextBlock>(s.Compact).Where(t => Equals(t.Tag, "ReportDayHeader") && t.IsVisible).Select(t => t.Text).ToList();
            s.Runtime.Now = s.Runtime.Now.AddDays(1);
            s.ViewModel.RefreshDates();
            Pump();
            Expect(scroll.VerticalOffset > 0 && headers.Count > 0, "clock refresh retains a deep reading position");
        }
        foreach (var name in new[] { "ListUpdating", "ListUpdated", "RevealRequested" })
            Expect(Handlers(disposed, typeof(ReportsViewModel), name) == 0, "disposed Compact detaches " + name);
    }

    private static ReportCard Card(Scenario scenario, string id) => Visuals<ReportCard>(scenario.Compact)
        .First(c => (c.DataContext as ReportListItemViewModel)?.ReportId == id);
    private static double Top(FrameworkElement element, ScrollViewer viewer) => element.TranslatePoint(new Point(), viewer).Y;
}
