using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Reports.Model;
using RvtMcp.Plugin.Reports.Presentation;
using RvtMcp.Plugin.Reports.Views;
using static H;
using static ReportsChecks;

/// <summary>Content checks: body, links, changes, reload, ShowReport, delete/copy, wheel, performance.</summary>
internal static class ContentChecks
{
    private const string MarkdownBody =
        "# Heading one\n" +
        "Intro with **bold**, *italic*, `code_x` and a [live link](revit:#0).\n" +
        "Second line after a single newline.\n\n" +
        "## Heading two\n" +
        "- bullet one\n" +
        "  - nested child\n" +
        "- bullet two with [second](revit:#1)\n\n" +
        "3. third\n" +
        "4. fourth\n\n" +
        "99999999999. huge\n\n" +
        "### Heading three\n" +
        "```\n" +
        "  indented code\n" +
        "second code line\n" +
        "```\n\n" +
        "---\n\n" +
        "| A | B | C |\n" +
        "| --- | --- | --- |\n" +
        "| 1 | [bad](revit:#9) | [web](https://example.com) |\n";

    /// <summary>5. Every node type of the mapping table, walked in the FlowDocument shown by the window.</summary>
    public static void Markdown()
    {
        var report = DemoData.Report("m01", "completed", "Markdown sample", DemoData.Local(0, 9, 0), DemoData.DocA(), body: MarkdownBody,
            links: new[] { DemoData.Link("live link", "d0", DemoData.Element(1, "Walls")), DemoData.Link("second", "d0", DemoData.Element(2, "Walls")) },
            changes: new ReportChanges { Complete = true });
        using (var s = new Scenario(DisplayMode.Wide, reports: new List<AgentReport> { report }).Show())
        {
            var doc = s.Wide.Body.Document;
            var blocks = doc.Blocks.ToList();
            var kinds = string.Join(",", blocks.Select(b => b.GetType().Name));
            Expect(kinds == "Paragraph,Paragraph,Paragraph,List,List,List,Paragraph,Paragraph,BlockUIContainer,Table", "block kinds: " + kinds);
            Expect(blocks[0].FontSize == 15 && blocks[2].FontSize == 13.5 && blocks[6].FontSize == 12.5, "heading sizes 15 / 13.5 / 12.5");
            Expect(new[] { 0, 2, 6 }.All(i => blocks[i].FontWeight == FontWeights.SemiBold)
                && ReferenceEquals(blocks[6].Foreground, ReportsStyles.TextSecondary), "headings are SemiBold; level 3 is secondary text");
            var runs = Elements(doc).OfType<Run>().ToList();
            var texts = runs.Select(r => r.Text).ToList();
            foreach (var expected in new[] { "bold", "italic", "code_x", "  indented code", "second code line", "third", "bad", "web", "nested child" })
                Expect(texts.Contains(expected), "run text '" + expected + "'");
            Expect(((Paragraph)blocks[1]).Inlines.OfType<LineBreak>().Count() == 1, "a single newline becomes a LineBreak");
            var code = runs.First(r => r.Text == "code_x");
            Expect(code.FontFamily == ReportsStyles.Mono && blocks[7].FontFamily == ReportsStyles.Mono, "inline code and fence use the mono font");
            Expect(((Paragraph)blocks[7]).Inlines.OfType<LineBreak>().Count() == 1, "fence lines are separated by a LineBreak");
            Expect(Elements(doc).OfType<Bold>().Any(b => Text(b) == "bold") && Elements(doc).OfType<Italic>().Any(i => Text(i) == "italic"), "bold and italic spans");
            var bullets = (List)blocks[3];
            Expect(bullets.MarkerStyle == TextMarkerStyle.Disc && bullets.ListItems.Count == 2
                && bullets.ListItems.FirstListItem.Blocks.OfType<List>().Count() == 1, "bullet list with one nested list");
            var ordered = (List)blocks[4];
            Expect(ordered.MarkerStyle == TextMarkerStyle.Decimal && ordered.StartIndex == 3 && ordered.ListItems.Count == 2, "ordered list starts at 3");
            var huge = (List)blocks[5];
            Expect(huge.MarkerStyle == TextMarkerStyle.None && texts.Contains("99999999999. "), "a number past int uses a text prefix");
            var table = (Table)blocks[9];
            Expect(table.Columns.Count == 3 && table.RowGroups[0].Rows.Count == 2, "table has 3 columns and 2 rows");
            var links = Elements(doc).OfType<Hyperlink>().ToList();
            Expect(links.Count == 2 && Text(links[0]) == "live link" && Text(links[1]) == "second", "one Hyperlink per valid link");
            Expect(runs.Where(r => r.Text == "bad" || r.Text == "web").All(r => !(r.Parent is Hyperlink)), "invalid and external links stay plain text");
            Expect(texts.All(t => !t.Contains("revit:#") && !t.Contains("https")), "no link targets are shown");
        }

        // Hostile nesting: thousands of emphasis levels build and lay out with a capped span depth.
        const int depth = 3000;
        var deep = new StringBuilder();
        for (var i = 0; i < depth; i++) deep.Append("*x ");
        deep.Append("deep");
        for (var i = 0; i < depth; i++) deep.Append(" x*");
        var nested = DemoData.Report("m02", "completed", "Nested sample", DemoData.Local(0, 9, 0), DemoData.DocA(), body: deep.ToString(),
            changes: new ReportChanges { Complete = true });
        using (var s = new Scenario(DisplayMode.Wide, reports: new List<AgentReport> { nested }).Show())
        {
            var runs = Elements(s.Wide.Body.Document).OfType<Run>().ToList();
            var deepRun = runs.FirstOrDefault(r => r.Text.Contains("deep"));
            Expect(deepRun != null && deepRun.FontStyle == FontStyles.Italic, "deeply nested text is shown in italic");
            var maxDepth = runs.Max(r => SpanDepth(r));
            Expect(maxDepth <= ReportFlowDocumentBuilder.MaxSpanDepth, "span depth capped at " + ReportFlowDocumentBuilder.MaxSpanDepth + ", got " + maxDepth);
            Expect(s.Wide.Body.ActualHeight > 0, "nested body lays out");
        }
    }

    /// <summary>6. Live and dimmed links; switching the active document restyles the same document.</summary>
    public static void Links()
    {
        using (var s = new Scenario(DisplayMode.Wide).Show())
        {
            var body = s.Wide.Body;
            var valves = BodyLinks(body).First();
            Expect(Text(valves) == "6 valves V-01…V-06" && ColorOf(valves.Foreground) == Hex(0x007ACC) && valves.Cursor == Cursors.Hand,
                "live link is blue with a hand cursor");
            Click(valves);
            var request = s.Navigation.Requests.Single();
            Expect(request.Document?.Token == DemoData.TokenA && request.Elements.Select(e => e.Id).SequenceEqual(Enumerable.Range(3101, 6).Select(i => (long)i)),
                "live click sends document A and its 6 elements");
            var badges = Elements(body.Document).OfType<InlineUIContainer>().Select(c => ((c.Child as Border)?.Child as TextBlock)?.Text).ToList();
            Expect(badges.Contains("6") && badges.Contains(L.T("reports.link.view")) && badges.Contains(L.T("reports.link.sheet")),
                "count badge and view / sheet tags: " + string.Join(",", badges));

            var changeLink = Visuals<TextBlock>(s.Wide.Changes).SelectMany(t => t.Inlines.OfType<Hyperlink>()).First();
            Click(changeLink);
            Expect(s.Navigation.Requests.Count == 2 && s.Navigation.Requests[1].Elements.Single().Id == 4101, "a changed element is a live link too");

            s.Select("r02");
            var doc = body.Document;
            var connector = BodyLinks(body).First();
            Expect(Text(connector) == "the inlet connector" && connector.IsEnabled, "dimmed link stays enabled");
            Expect(ColorOf(connector.Foreground) == Hex(0x94A3B8) && connector.Cursor == Cursors.Arrow, "dimmed link is grey with an arrow cursor");
            Expect((connector.ToolTip as string)?.Contains("Demo-pump.rfa") == true, "dimmed tooltip names the model: " + connector.ToolTip);
            Click(connector);
            Expect(s.Navigation.Requests.Count == 2, "dimmed click sends nothing");

            s.Runtime.SetActive(DemoData.ActivePump);
            Pump();
            Expect(ReferenceEquals(body.Document, doc) && ReferenceEquals(BodyLinks(body).First(), connector), "same FlowDocument after the switch");
            Expect(ColorOf(connector.Foreground) == Hex(0x007ACC) && connector.ToolTip == null, "the pump link turns live");
            Expect(ColorOf(BodyLinks(body).ElementAt(1).Foreground) == Hex(0x94A3B8), "the DEMO-A link turns dim");
            Click(connector);
            Expect(s.Navigation.Requests.Count == 3 && s.Navigation.Requests[2].Document.Token == DemoData.TokenPump, "the now-live link navigates");
        }
    }

    /// <summary>7. Actual changes: badges, notes, deleted items, documents, compact chip, "Show N more".</summary>
    public static void Changes()
    {
        using (var s = new Scenario(DisplayMode.Wide).Show())
        {
            var view = s.Wide;
            Click(view.AllFilterButton);
            Expect(view.Changes.IsExpanded, "wide changes start open");
            var header = Texts(view.Changes);
            foreach (var badge in new[] { L.T("reports.changes.badge.added", ("count", 4)), L.T("reports.changes.badge.modified", ("count", 8)),
                L.T("reports.changes.badge.deleted", ("count", 1)), L.T("reports.changes.title") })
                Expect(header.Contains(badge), "header shows '" + badge + "'");
            var changes = ChangesView(view);
            var groups = Texts(changes);
            Expect(groups.Contains("Added (4)") && groups.Contains("Modified (8)") && groups.Contains("Deleted (1)"), "group titles with counts");
            var deleted = Visuals<TextBlock>(changes).Single(t => t.Inlines.OfType<Run>().Any(r => r.Text == "3301"));
            var deletedRun = deleted.Inlines.OfType<Run>().Single();
            Expect(deletedRun.TextDecorations == TextDecorations.Strikethrough && !deleted.Inlines.OfType<Hyperlink>().Any(), "deleted item is struck through, not a link");
            Expect(Visuals<TextBlock>(changes).All(t => (t.Tag as string) != "ChangeDocumentTitle"), "one document: no document titles");

            foreach (var id in new[] { "r03", "r04" })
            {
                s.Select(id);
                var notes = Visuals<TextBlock>(ChangesView(view)).ToList();
                Expect(notes.Count == 1 && (notes[0].Tag as string) == "ChangesNote" && notes[0].Text == s.ViewModel.SelectedDetail.Changes.NoteText
                    && notes[0].Text.Length > 0, id + ": only a note");
                var badge = id == "r03" ? L.T("reports.changes.badge.noData") : L.T("reports.changes.badge.none");
                Expect(Texts(view.Changes).Contains(badge), id + ": header badge '" + badge + "'");
            }
            s.Select("r05");
            var r05 = Texts(ChangesView(view));
            Expect(r05.Contains(L.T("reports.changes.incomplete")) && r05.Contains(L.T("reports.changes.truncated")), "incomplete and truncated notes");
            s.Select("r02");
            var titles = Visuals<TextBlock>(ChangesView(view)).Where(t => (t.Tag as string) == "ChangeDocumentTitle").Select(t => t.Text).ToList();
            Expect(titles.SequenceEqual(new[] { "Demo-pump.rfa", "DEMO-A.rvt" }), "two documents get titles: " + string.Join(",", titles));

            s.Select(DemoData.BulkReport);
            var bulk = ChangesView(view);
            Expect(Items(bulk) == ReportChangesView.GroupLimit, "a group shows 200 items first");
            var more = Visuals<Button>(bulk).Single();
            Expect((more.Content as string) == L.T("reports.changes.showMore", ("count", ReportChangesView.MoreStep)), "Show N more: " + more.Content);
            Click(more);
            Expect(Items(bulk) == ReportChangesView.GroupLimit + ReportChangesView.MoreStep && Visuals<Button>(bulk).Single() == more,
                "one press adds one step and the button stays");
            var presses = 1;
            while (Visuals<Button>(bulk).Any() && presses < 10)
            {
                var remaining = DemoData.BulkCount - Items(bulk);
                Expect((more.Content as string) == L.T("reports.changes.showMore", ("count", Math.Min(ReportChangesView.MoreStep, remaining))),
                    "the button names the next step: " + more.Content);
                Click(more);
                presses++;
            }
            Expect(Items(bulk) == DemoData.BulkCount && !Visuals<Button>(bulk).Any() && presses == 5,
                "five presses list every item and the button goes away (" + presses + ")");

            view.Changes.IsExpanded = false;
            Pump();
            s.Select("r01");
            Expect(!view.Changes.IsExpanded, "a collapsed changes section stays collapsed for the next report");
            Click(s.Window.CompactButton);
            Click(s.Window.WideButton);
            Expect(!s.Wide.Changes.IsExpanded, "and across a mode switch");
        }
        using (var s = new Scenario(DisplayMode.Compact).Show())
        {
            s.ViewModel.Filter = ReportFilter.All;
            Pump();
            s.Select("r01");
            var open = s.Compact.OpenBody;
            Expect(open != null && open.Chip != null && !open.ChangesVisible && open.ChangesView == null, "compact chip starts collapsed, list not built");
            Expect(Texts(open.Chip).Contains(L.T("reports.changes.chipTitle")) && Texts(open.Chip).Contains("▾"), "chip shows title, badges and ▾");
            Click(open.Chip);
            Expect(open.ChangesVisible && open.ChangesView != null && s.ViewModel.CompactChangesExpanded && Texts(open.Chip).Contains("▴"), "chip click opens the list");
            Click(open.Chip);
            Expect(!open.ChangesVisible, "second click closes it");
            foreach (var id in new[] { "r03", "r04" })
            {
                s.Select(id);
                var body = s.Compact.OpenBody;
                Expect(body.Chip == null && Visuals<TextBlock>(body).Any(t => (t.Tag as string) == "ChangesNote"), id + ": compact has a note and no chip");
            }
        }
        var partial = DemoData.Report("p01", "completed", "Nothing captured, capture incomplete", DemoData.Local(0, 9, 0), DemoData.DocA(),
            body: "Checked.", changes: new ReportChanges { Complete = false });
        using (var s = new Scenario(DisplayMode.Compact, reports: new List<AgentReport> { partial }).Show())
        {
            s.Select("p01");
            var notes = Texts(s.Compact.OpenBody);
            Expect(s.Compact.OpenBody.Chip == null && notes.Contains(L.T("reports.changes.empty")) && notes.Contains(L.T("reports.changes.incomplete")),
                "compact without a chip still warns that the capture is incomplete");
        }
    }

    /// <summary>8. Reload with unchanged data keeps document and scroll offsets; a new report at the top keeps the selection.</summary>
    public static void Reload()
    {
        using (var s = new Scenario(DisplayMode.Wide).Show())
        {
            var view = s.Wide;
            s.Select(DemoData.LongReport);
            view.DetailScroll.ScrollToVerticalOffset(400);
            var listScroll = InnerScroll(view.List);
            listScroll.ScrollToVerticalOffset(3);
            Pump();
            var doc = view.Body.Document;
            var detail = view.DetailScroll.VerticalOffset;
            var list = listScroll.VerticalOffset;
            Expect(Near(detail, 400) && Near(list, 3), $"scrolled to body 400 and list 3 ({detail}, {list})");

            s.Runtime.RaiseReportsChanged();
            s.WaitForRefresh();
            Expect(ReferenceEquals(view.Body.Document, doc), "unchanged reload keeps the FlowDocument");
            Expect(Near(view.DetailScroll.VerticalOffset, detail) && Near(listScroll.VerticalOffset, list),
                $"unchanged reload keeps both offsets ({view.DetailScroll.VerticalOffset}, {listScroll.VerticalOffset})");

            var reports = s.Runtime.Reports;
            reports.Insert(0, DemoData.Report("r00", "completed", "A newer report", DemoData.Local(0, 14, 59), DemoData.DocA(), body: "New.",
                changes: new ReportChanges { Complete = true }));
            s.Runtime.Reports = reports;
            s.Runtime.RaiseReportsChanged();
            s.WaitForRefresh();
            Expect(s.ViewModel.Reports[0].ReportId == "r00" && s.ViewModel.SelectedReport?.ReportId == DemoData.LongReport
                && ReferenceEquals(view.List.SelectedItem, s.ViewModel.SelectedReport), "a new report at the top keeps the selection");
            Expect(ReferenceEquals(view.Body.Document, doc), "and keeps the FlowDocument");
        }
    }

    /// <summary>9. ShowReport for another model's report: filter goes to All, the report is selected and scrolled into view.</summary>
    public static void ShowReport()
    {
        using (var s = new Scenario(DisplayMode.Wide).Show())
        {
            s.ViewModel.ShowReport("r27");
            Pump();
            Expect(s.ViewModel.Filter == ReportFilter.All && s.Wide.AllFilterButton.IsChecked == true, "wide: filter switches to All");
            Expect(s.ViewModel.SelectedReport?.ReportId == "r27" && ReferenceEquals(s.Wide.List.SelectedItem, s.ViewModel.SelectedReport), "wide: report selected");
            var row = s.Wide.List.ItemContainerGenerator.ContainerFromItem(s.ViewModel.SelectedReport) as FrameworkElement;
            Expect(row != null && InView(row, InnerScroll(s.Wide.List)), "wide: the row is scrolled into view");
        }
        using (var s = new Scenario(DisplayMode.Compact).Show())
        {
            s.ViewModel.ShowReport("r27");
            Pump();
            Pump();
            Expect(s.ViewModel.Filter == ReportFilter.All && s.Compact.Filter.SelectedIndex == 1, "compact: filter switches to All");
            var card = Cards(s.Compact).FirstOrDefault(c => ReferenceEquals(c.DataContext, s.ViewModel.SelectedReport));
            Expect(s.ViewModel.SelectedReport?.ReportId == "r27" && card != null && card.IsOpen, "compact: the card is open");
            Expect(InView(card.Header, InnerScroll(s.Compact.Feed)), "compact: the card header is scrolled into view");
            Expect(Visuals<TextBlock>(card).Any(t => (t.Tag as string) == "OtherModelLine" && t.Visibility == Visibility.Visible
                && t.Text == "DEMO-B.rvt · " + L.T("reports.list.otherModel")), "compact: other-model line under the summary");
        }
    }

    /// <summary>10. Two-step delete with the danger style; copy sends plain text and shows Success.</summary>
    public static void DeleteAndCopy()
    {
        using (var s = new Scenario(DisplayMode.Wide).Show())
        {
            var view = s.Wide;
            s.Select("r04");
            Expect((view.DeleteButton.Content as string) == L.T("reports.delete.button"), "delete starts as Delete");
            Click(view.DeleteButton);
            Expect((view.DeleteButton.Content as string) == L.T("reports.delete.confirm")
                && ReferenceEquals(view.DeleteButton.Style, ReportsStyles.Dictionary[ReportsStyles.DangerButtonKey]), "first click asks to confirm in danger style");
            Expect(s.Runtime.Deleted.Count == 0, "nothing deleted yet");
            Click(view.DeleteButton);
            Expect(s.Runtime.Deleted.SequenceEqual(new[] { "r04" }) && s.ViewModel.Reports.All(r => r.ReportId != "r04"), "second click deletes r04");
            Expect((view.DeleteButton.Content as string) == L.T("reports.delete.button")
                && ReferenceEquals(view.DeleteButton.Style, ReportsStyles.Dictionary[RvtMcp.Plugin.Views.BimwrightStyles.ToolbarButtonKey]), "button back to normal");
            Expect(s.Window.StatusText.Text == L.T("reports.status.deleted"), "deleted status");

            s.Select("r01");
            Click(view.CopyButton);
            var copied = s.Runtime.Copied.Last();
            Expect(copied.StartsWith(s.ViewModel.SelectedDetail.Summary + "\n\n", StringComparison.Ordinal) && copied.Contains("6 valves V-01…V-06"),
                "copy sends summary and body text");
            Expect(!copied.Contains("revit:#") && !copied.Contains("**") && !copied.Contains("]("), "copy has no markup");
            Expect(s.Window.StatusText.Text == L.T("reports.status.copied") && ColorOf(s.Window.StatusText.Foreground) == Hex(0x00548C)
                && ColorOf(s.Window.StatusDot.Fill) == Hex(0x007ACC), "copy shows the Success status");
        }
        using (var s = new Scenario(DisplayMode.Compact).Show())
        {
            s.Select("r05");
            var body = s.Compact.OpenBody;
            Click(body.DeleteButton);
            Expect((body.DeleteButton.Content as string) == L.T("reports.delete.confirm")
                && ReferenceEquals(body.DeleteButton.Style, ReportsStyles.Dictionary[ReportsStyles.DangerButtonKey]), "compact: confirm in danger style");
            Click(body.DeleteButton);
            Expect(s.Runtime.Deleted.SequenceEqual(new[] { "r05" }) && s.ViewModel.SelectedReport == null, "compact: second click deletes r05");
        }
    }

    /// <summary>14. The wheel over the body scrolls the outer region in both modes.</summary>
    public static void Wheel()
    {
        using (var s = new Scenario(DisplayMode.Wide).Show())
        {
            s.Select(DemoData.LongReport);
            Expect(Near(s.Wide.DetailScroll.VerticalOffset, 0), "detail starts at the top");
            Roll(InnerScroll(s.Wide.Body));
            Expect(s.Wide.DetailScroll.VerticalOffset > 0, "wide: wheel on the body scrolls the detail (" + s.Wide.DetailScroll.VerticalOffset + ")");
        }
        using (var s = new Scenario(DisplayMode.Compact).Show())
        {
            s.Select(DemoData.LongReport);
            var feed = InnerScroll(s.Compact.Feed);
            var before = feed.VerticalOffset;
            Roll(InnerScroll(s.Compact.OpenBody.Body));
            Expect(feed.VerticalOffset > before, $"compact: wheel on the body scrolls the feed ({before} → {feed.VerticalOffset})");
        }
    }

    /// <summary>16. 32 KiB body, 5,000 changes and one "Show N more" step each take under 1,500 ms (printed).</summary>
    public static void Performance()
    {
        const double limit = 1500;
        using (var s = new Scenario(DisplayMode.Wide).Show())
        {
            var bytes = Encoding.UTF8.GetByteCount(s.Runtime.Reports.Single(r => r.ReportId == DemoData.LongReport).Body);
            var clock = Stopwatch.StartNew();
            s.Select(DemoData.LongReport);
            s.Window.UpdateLayout();
            var body = clock.Elapsed.TotalMilliseconds;
            Console.WriteLine($"PERF wide: {bytes / 1024.0:0.0} KiB body, {BodyLinks(s.Wide.Body).Count()} hyperlinks: select + layout {body:0} ms");
            Expect(body < limit, "32 KiB body under " + limit + " ms");

            clock.Restart();
            s.Select(DemoData.BulkReport);
            s.Window.UpdateLayout();
            var changes = clock.Elapsed.TotalMilliseconds;
            Console.WriteLine($"PERF wide: {DemoData.BulkCount} changes, changes open: select + layout {changes:0} ms");
            Expect(changes < limit, "5,000-change report under " + limit + " ms");

            clock.Restart();
            Click(Visuals<Button>(ChangesView(s.Wide)).Single());
            s.Window.UpdateLayout();
            var more = clock.Elapsed.TotalMilliseconds;
            Console.WriteLine($"PERF wide: Show {ReportChangesView.MoreStep} more + layout {more:0} ms");
            Expect(more < limit, "one Show more step under " + limit + " ms");
        }
        using (var s = new Scenario(DisplayMode.Compact).Show())
        {
            var clock = Stopwatch.StartNew();
            s.Select(DemoData.LongReport);
            s.Window.UpdateLayout();
            var body = clock.Elapsed.TotalMilliseconds;
            Console.WriteLine($"PERF compact: 32 KiB body card open + layout {body:0} ms");
            Expect(body < limit, "compact 32 KiB body under " + limit + " ms");

            s.Select(DemoData.BulkReport);
            clock.Restart();
            Click(s.Compact.OpenBody.Chip);
            s.Window.UpdateLayout();
            var changes = clock.Elapsed.TotalMilliseconds;
            Console.WriteLine($"PERF compact: {DemoData.BulkCount} changes, chip open + layout {changes:0} ms");
            Expect(changes < limit, "compact 5,000-change list under " + limit + " ms");
        }
    }

    private static ReportChangesView ChangesView(ReportsWideView view) => (ReportChangesView)((Border)view.Changes.Content).Child;

    private static int Items(ReportChangesView changes) => changes.Children.OfType<WrapPanel>().Sum(w => w.Children.Count);

    private static int SpanDepth(TextElement element)
    {
        var depth = 0;
        for (var parent = element.Parent; parent is Span span; parent = span.Parent) depth++;
        return depth;
    }

    private static bool InView(FrameworkElement element, ScrollViewer scroll)
    {
        var top = element.TransformToAncestor(scroll).Transform(new Point(0, 0)).Y;
        return top >= -0.5 && top < scroll.ActualHeight - 10;
    }

    private static void Roll(UIElement target)
    {
        target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
        Pump();
    }
}
