using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using RvtMcp.Plugin.Reports.Model;
using RvtMcp.Plugin.Reports.Presentation;
using static H;

/// <summary>Synthetic large-table report: no customer model or report data.</summary>
internal static class ReportReadingChecks
{
    private static AgentReport Report()
    {
        var body = new StringBuilder("# Sheet review\n\n## Scope\n\n");
        for (var i = 0; i < 10; i++) body.Append("Scope paragraph with enough text to place the sheet links below the viewport.\n\n");
        body.Append("## Locations\n\n| Sheet | Status |\n|---|---|\n| [P101](revit:#0) | Review |\n| [P201](revit:#1) | Review |\n\n");
        body.Append("## Dimensions\n\n| Element | Measured | Written | Difference |\n|---|---|---|---|\n");
        for (var i = 0; i < 108; i++) body.Append("| [Dim ").Append(i).Append("](revit:#").Append(i + 2).Append(") | 100 | 101 | 1 |\n");
        body.Append("\n## Final notes\n\nLast detail row must remain available in Copy.\n");
        var links = new List<ReportLink> {
            DemoData.Link("P101", "d0", DemoData.Element(8101, "Sheets", ReportElementKinds.Sheet)),
            DemoData.Link("P201", "d0", DemoData.Element(8201, "Sheets", ReportElementKinds.Sheet))
        };
        links.AddRange(Enumerable.Range(0, 108).Select(i => DemoData.Link("Dim " + i, "d0", DemoData.Element(8300 + i, "Dimensions"))));
        return DemoData.Report("reading", "completed", "Sheet review", DemoData.Local(0, 9, 0), DemoData.DocA(), body: body.ToString(),
            links: links.ToArray(), changes: new ReportChanges { Complete = true });
    }

    public static void LinkFocus()
    {
        foreach (var mode in new[] { DisplayMode.Compact, DisplayMode.Wide })
        using (var s = new Scenario(mode, reports: new List<AgentReport> { Report() }))
        {
            s.Window.ShowActivated = false;
            s.Show(); s.Select("reading");
            var body = mode == DisplayMode.Compact ? s.Compact.OpenBody.Body : s.Wide.Body;
            var scroll = mode == DisplayMode.Compact ? InnerScroll(s.Compact.Feed) : s.Wide.DetailScroll;
            var links = Elements(body.Document).OfType<Hyperlink>().Where(h => h.DataContext is ReportLinkViewModel).Take(2).ToArray();
            var document = body.Document;
            foreach (var link in links.Concat(links))
            {
                // Put the real linked text in the visible viewport before focusing it, as a pointer click does.
                var rect = link.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                var inScroll = body.TransformToAncestor(scroll).Transform(new Point(rect.X, rect.Y));
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + inScroll.Y - 100);
                Pump();
                var before = scroll.VerticalOffset;
                Expect(before > 0, mode + ": the report is scrolled");
                Expect(link.Focus(), mode + ": link takes focus");
                Click(link); s.Window.UpdateLayout(); Pump();
                Expect(Near(before, scroll.VerticalOffset), mode + ": visible link keeps reading position (" + before + " -> " + scroll.VerticalOffset + ")");
                Expect(ReferenceEquals(document, body.Document), "link focus keeps the document");
            }
            Expect(s.Navigation.Requests.Count == 4, "all sheet clicks still navigate");
            links[0].Focus(); Pump();
            var edge = links[1].ContentStart.GetCharacterRect(LogicalDirection.Forward);
            var edgePoint = body.TransformToAncestor(scroll).Transform(new Point(edge.X, edge.Y));
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + edgePoint.Y + 5); Pump();
            var pointerOffset = scroll.VerticalOffset;
            links[1].RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
            links[1].Focus(); Click(links[1]);
            Expect(Near(pointerOffset, scroll.VerticalOffset), mode + ": a pointer click on partly visible text does not reveal its first character");
            // Keyboard focus on an off-screen link must reveal its actual position, rather than the document origin.
            links[0].Focus(); Pump();
            scroll.ScrollToTop(); Pump();
            // A Tab arriving before the pointer-focus reset must still reveal the keyboard target.
            links[0].RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
            links[0].RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(body), Environment.TickCount, Key.Tab)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            links[1].Focus(); Pump();
            var target = links[1].ContentStart.GetCharacterRect(LogicalDirection.Forward);
            var point = body.TransformToAncestor(scroll).Transform(new Point(target.X, target.Y));
            Expect(point.Y >= -1 && point.Y < scroll.ViewportHeight, mode + ": keyboard focus reveals the real link (y=" + point.Y + ", viewport=" + scroll.ViewportHeight + ")");
        }
    }

    public static void DeferredTables()
    {
        using (var s = new Scenario(DisplayMode.Wide, reports: new List<AgentReport> { Report() }))
        {
            s.Window.ShowActivated = false; s.Show();
            var body = s.Wide.Body;
            Expect(Elements(body.Document).OfType<TableCell>().Count() < 100, "closed long sections do not realize their table cells");
            var toggle = Elements(body.Document).OfType<Hyperlink>().FirstOrDefault(h => Equals(h.Tag, "ReportSectionToggle") && Text(h).Contains("Dimensions"));
            Expect(toggle != null, "the long section has a labelled, keyboard-accessible toggle");
            Click(toggle);
            var next = Elements(body.Document).OfType<Hyperlink>().Single(h => Equals(h.Tag, "ReportTableNext"));
            Expect(Elements(body.Document).OfType<TableRow>().Count() == 3 + 26, "opening a long table builds its header and 25 data rows");
            var shown = Elements(body.Document).OfType<Hyperlink>().Where(h => h.DataContext is ReportLinkViewModel).ToList();
            Expect(shown.Any(h => Text(h) == "Dim 0") && !shown.Any(h => Text(h) == "Dim 25"), "the first page is bounded");
            Click(next);
            shown = Elements(body.Document).OfType<Hyperlink>().Where(h => h.DataContext is ReportLinkViewModel).ToList();
            var dim25 = shown.Single(h => Text(h) == "Dim 25");
            Expect(!shown.Any(h => Text(h) == "Dim 0"), "changing pages replaces, rather than appends, rows");
            Click(dim25);
            Expect(s.Navigation.Requests.Last().Elements.Single().Id == 8325, "paged links keep the original element identity");
            Click(next); Click(next); Click(next);
            Expect(!next.IsEnabled && Elements(body.Document).OfType<TableRow>().Count() == 3 + 9,
                "the final partial page has eight data rows and cannot advance");
            var previous = Elements(body.Document).OfType<Hyperlink>().Single(h => Equals(h.Tag, "ReportTablePrevious"));
            Click(previous); Click(previous); Click(previous);
            s.ViewModel.CopyCommand.Execute(null);
            Expect(s.Runtime.Copied.Last().Contains("Dim 107") && s.Runtime.Copied.Last().Contains("Last detail row"), "Copy includes hidden sections and all table pages");
            s.Runtime.RaiseReportsChanged(); s.WaitForRefresh();
            Expect(ReferenceEquals(body.Document, s.Wide.Body.Document), "unchanged refresh keeps the current page");
            Click(s.Window.CompactButton);
            var reopened = Elements(s.Compact.OpenBody.Body.Document).OfType<Hyperlink>().Where(h => h.DataContext is ReportLinkViewModel).ToList();
            Expect(reopened.Any(h => Text(h) == "Dim 25") && !reopened.Any(h => Text(h) == "Dim 0"), "mode switches retain expanded sections and table pages");
        }
    }
}
