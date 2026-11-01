using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Reports.Model;
using RvtMcp.Plugin.Reports.Presentation;

namespace RvtMcp.Plugin.Reports.Views
{
    /// <summary>
    /// Builds the report body <see cref="FlowDocument"/> from the stored-mode parse tree.
    /// Only <c>revit:#n</c> links with a matching <c>Links[n]</c> become hyperlinks; never an external URL.
    /// </summary>
    internal static class ReportFlowDocumentBuilder
    {
        /// <summary>
        /// Bold/Italic spans deeper than this become formatted runs. Hostile input can nest
        /// emphasis thousands deep; a flat tree keeps WPF and this builder off the stack limit.
        /// </summary>
        internal const int MaxSpanDepth = 16;
        internal const int TablePageRows = 25;
        internal const int DeferredCellBudget = 200;

        public static FlowDocument Build(MarkdownParseResult body, IReadOnlyList<ReportLinkViewModel> links,
            IDictionary<int, bool> expandedSections = null, IDictionary<int, int> tablePages = null)
        {
            var document = new FlowDocument
            {
                PagePadding = new Thickness(0), FontFamily = ReportsStyles.Ui, FontSize = 12.5,
                Foreground = ReportsStyles.Text, TextAlignment = TextAlignment.Left
            };
            if (body == null) return document;
            links = links ?? new ReportLinkViewModel[0];
            expandedSections = expandedSections ?? new Dictionary<int, bool>();
            tablePages = tablePages ?? new Dictionary<int, int>();
            var defer = body.Blocks.Sum(b => b.Kind == MarkdownBlockKind.Table ? (long)b.Rows.Count * (b.Rows.FirstOrDefault()?.Count ?? 0) : 0) > DeferredCellBudget;
            var sectionNumber = 0;
            for (var i = 0; i < body.Blocks.Count; i++)
            {
                var block = body.Blocks[i];
                if (defer && block.Kind == MarkdownBlockKind.Heading && block.Level == 2)
                {
                    var end = i + 1;
                    while (end < body.Blocks.Count && !(body.Blocks[end].Kind == MarkdownBlockKind.Heading && body.Blocks[end].Level <= 2)) end++;
                    document.Blocks.Add(BuildSection(body.Blocks, i, end, sectionNumber++ < 2, links, expandedSections, tablePages));
                    i = end - 1;
                    continue;
                }
                var built = BuildBlock(block, links, document.Blocks.Count == 0, i, tablePages);
                if (built != null) document.Blocks.Add(built);
            }
            return document;
        }

        /// <summary>Element link that stays enabled when dimmed; a click runs <paramref name="open"/> only when it can execute.</summary>
        internal static Hyperlink CreateLink(object viewModel, ICommand open, string dimTooltip)
        {
            var link = new Hyperlink { DataContext = viewModel, Tag = dimTooltip, Style = ReportsStyles.LinkStyle };
            link.Click += (s, e) => { if (open != null && open.CanExecute(null)) open.Execute(null); };
            return link;
        }

        private static Section BuildSection(IReadOnlyList<MarkdownBlock> blocks, int start, int end, bool initiallyOpen,
            IReadOnlyList<ReportLinkViewModel> links, IDictionary<int, bool> expanded, IDictionary<int, int> pages)
        {
            var section = new Section { Margin = new Thickness(0) };
            var heading = (Paragraph)BuildBlock(blocks[start], links, false);
            var title = new TextRange(heading.ContentStart, heading.ContentEnd).Text.Trim();
            var arrow = new Run();
            var toggle = new Hyperlink(arrow) { Tag = "ReportSectionToggle", Foreground = ReportsStyles.Text, TextDecorations = null };
            // Heading links remain independent targets; a plain heading can be clicked anywhere.
            if (!ContainsLink(heading.Inlines))
            {
                var inlines = heading.Inlines.ToArray();
                heading.Inlines.Clear();
                foreach (var inline in inlines) toggle.Inlines.Add(inline);
            }
            if (heading.Inlines.FirstInline == null) heading.Inlines.Add(toggle);
            else heading.Inlines.InsertBefore(heading.Inlines.FirstInline, toggle);
            section.Blocks.Add(heading);
            void Render()
            {
                var open = expanded.TryGetValue(start, out var saved) ? saved : initiallyOpen;
                arrow.Text = open ? "▾ " : "▸ ";
                var hint = L.T(open ? "reports.body.collapse" : "reports.body.expand") + " " + title;
                toggle.ToolTip = hint;
                AutomationProperties.SetName(toggle, hint);
                while (section.Blocks.Count > 1) section.Blocks.Remove(section.Blocks.LastBlock);
                if (!open) return;
                for (var i = start + 1; i < end; i++)
                {
                    var built = BuildBlock(blocks[i], links, false, i, pages);
                    if (built != null) section.Blocks.Add(built);
                }
            }
            toggle.Click += (s, e) =>
            {
                expanded[start] = !(expanded.TryGetValue(start, out var saved) ? saved : initiallyOpen);
                Render();
            };
            Render();
            return section;
        }

        private static bool ContainsLink(IEnumerable<Inline> inlines)
        {
            var pending = new Stack<Inline>(inlines);
            while (pending.Count > 0)
            {
                var inline = pending.Pop();
                if (inline is Hyperlink) return true;
                if (inline is Span span) foreach (var child in span.Inlines) pending.Push(child);
            }
            return false;
        }

        private static Block BuildBlock(MarkdownBlock block, IReadOnlyList<ReportLinkViewModel> links, bool first,
            int index = 0, IDictionary<int, int> pages = null)
        {
            if (block == null) return null;
            switch (block.Kind)
            {
                case MarkdownBlockKind.Heading:
                {
                    var heading = new Paragraph
                    {
                        FontWeight = FontWeights.SemiBold,
                        FontSize = block.Level <= 1 ? 15 : block.Level == 2 ? 13.5 : 12.5,
                        Margin = new Thickness(0, first ? 4 : 12, 0, 5)
                    };
                    if (block.Level >= 3) heading.Foreground = ReportsStyles.TextSecondary;
                    AddInlines(heading.Inlines, block.Inlines, links);
                    return heading;
                }
                case MarkdownBlockKind.List:
                    return block.Items.Count == 0 ? null : BuildList(block.Items, links);
                case MarkdownBlockKind.CodeFence:
                {
                    var code = new Paragraph
                    {
                        FontFamily = ReportsStyles.Mono, FontSize = 11.5, Background = ReportsStyles.CodeBackground,
                        BorderBrush = ReportsStyles.CodeBorder, BorderThickness = new Thickness(1),
                        Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 8)
                    };
                    var lines = (block.Text ?? "").Split('\n');
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (i > 0) code.Inlines.Add(new LineBreak());
                        code.Inlines.Add(new Run(lines[i]));
                    }
                    return code;
                }
                case MarkdownBlockKind.HorizontalRule:
                    return new BlockUIContainer(new Border { Height = 1, Background = ReportsStyles.BorderBrush, Margin = new Thickness(0, 8, 0, 8) })
                        { Margin = new Thickness(0) };
                case MarkdownBlockKind.Table:
                    return block.Rows.Count == 0 ? null : block.Rows.Count <= TablePageRows + 1
                        ? (Block)BuildTable(block.Rows, links) : BuildPagedTable(block.Rows, links, index, pages ?? new Dictionary<int, int>());
                default:
                {
                    var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
                    AddInlines(paragraph.Inlines, block.Inlines, links);
                    return paragraph;
                }
            }
        }

        /// <summary>A deeper item nests in a child list of the previous item, one level at a time.</summary>
        private static List BuildList(List<MarkdownListItem> items, IReadOnlyList<ReportLinkViewModel> links)
        {
            var root = NewList(items[0]);
            root.Margin = new Thickness(0, 0, 0, 8);
            var open = new Stack<KeyValuePair<List, int>>();
            open.Push(new KeyValuePair<List, int>(root, items[0].Level));
            foreach (var item in items)
            {
                if (item == null) continue;
                while (open.Count > 1 && item.Level < open.Peek().Value) open.Pop();
                var list = open.Peek().Key;
                if (item.Level > open.Peek().Value && list.ListItems.Count > 0)
                {
                    var child = NewList(item);
                    list.ListItems.LastListItem.Blocks.Add(child);
                    open.Push(new KeyValuePair<List, int>(child, item.Level));
                    list = child;
                }
                var paragraph = new Paragraph { Margin = new Thickness(0) };
                if (list.MarkerStyle == TextMarkerStyle.None) paragraph.Inlines.Add(new Run(item.Number + ". "));
                AddInlines(paragraph.Inlines, item.Inlines, links);
                list.ListItems.Add(new ListItem(paragraph) { Margin = new Thickness(0, 2, 0, 2) });
            }
            return root;
        }

        /// <summary>An ordered list starts at its first number; a number that is not an int ≥ 1 is written as a prefix.</summary>
        private static List NewList(MarkdownListItem first)
        {
            var list = new List { Padding = new Thickness(20, 0, 0, 0), Margin = new Thickness(0), MarkerStyle = TextMarkerStyle.Disc };
            if (!first.IsOrdered) return list;
            if (int.TryParse(first.Number, NumberStyles.None, CultureInfo.InvariantCulture, out var start) && start >= 1)
            {
                list.MarkerStyle = TextMarkerStyle.Decimal;
                list.StartIndex = start;
            }
            else list.MarkerStyle = TextMarkerStyle.None;
            return list;
        }

        private static Section BuildPagedTable(List<MarkdownTableRow> rows, IReadOnlyList<ReportLinkViewModel> links, int index, IDictionary<int, int> pages)
        {
            var section = new Section { Margin = new Thickness(0) };
            var previous = new Hyperlink(new Run(L.T("reports.body.previous"))) { Tag = "ReportTablePrevious" };
            var next = new Hyperlink(new Run(L.T("reports.body.next"))) { Tag = "ReportTableNext" };
            var label = new Run();
            var pager = new Paragraph { FontSize = 11.5, Margin = new Thickness(0, 0, 0, 10), Foreground = ReportsStyles.TextSecondary };
            pager.Inlines.Add(previous); pager.Inlines.Add(new Run("   ")); pager.Inlines.Add(label);
            pager.Inlines.Add(new Run("   ")); pager.Inlines.Add(next);
            var lastPage = (rows.Count - 2) / TablePageRows;
            var page = pages.TryGetValue(index, out var saved) ? Math.Max(0, Math.Min(lastPage, saved)) : 0;
            void Render()
            {
                pages[index] = page;
                if (section.Blocks.FirstBlock is Table oldTable) section.Blocks.Remove(oldTable);
                var table = BuildTable(rows, links, page * TablePageRows, TablePageRows);
                // Keep the focused pager attached while replacing the data rows.
                if (pager.Parent == null) { section.Blocks.Add(table); section.Blocks.Add(pager); }
                else section.Blocks.InsertBefore(pager, table);
                previous.IsEnabled = page > 0;
                next.IsEnabled = page < lastPage;
                label.Text = L.T("reports.body.rows", ("first", page * TablePageRows + 1),
                    ("last", Math.Min(rows.Count - 1, (page + 1) * TablePageRows)), ("total", rows.Count - 1));
            }
            previous.Click += (s, e) => { if (page > 0) { page--; Render(); } };
            next.Click += (s, e) => { if (page < lastPage) { page++; Render(); } };
            Render();
            return section;
        }

        private static Table BuildTable(List<MarkdownTableRow> rows, IReadOnlyList<ReportLinkViewModel> links, int offset = 0, int count = int.MaxValue)
        {
            var columns = rows[0].Count;
            var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 10), FontSize = 12 };
            for (var c = 0; c < columns; c++) table.Columns.Add(new TableColumn());
            var group = new TableRowGroup();
            table.RowGroups.Add(group);
            var dataRows = Math.Min(count, rows.Count - 1 - offset);
            for (var r = 0; r <= dataRows; r++)
            {
                var source = rows[r == 0 ? 0 : offset + r];
                var row = new TableRow();
                if (r == 0)
                {
                    row.FontWeight = FontWeights.SemiBold;
                    row.FontSize = 11.5;
                    row.Foreground = ReportsStyles.TextSecondary;
                    row.Background = ReportsStyles.TableHeader;
                }
                for (var c = 0; c < columns; c++)
                {
                    var paragraph = new Paragraph { Margin = new Thickness(0) };
                    if (source != null && c < source.Count) AddInlines(paragraph.Inlines, source[c], links);
                    // Left/top edges only on the first column/row, so neighbouring cells never draw a double line.
                    row.Cells.Add(new TableCell(paragraph)
                    {
                        Padding = new Thickness(8, 4, 8, 4), BorderBrush = ReportsStyles.BorderBrush,
                        BorderThickness = new Thickness(c == 0 ? 1 : 0, r == 0 ? 1 : 0, 1, 1)
                    });
                }
                group.Rows.Add(row);
            }
            return table;
        }

        /// <summary>Iterative walk with an explicit stack: input depth never becomes call depth.</summary>
        private static void AddInlines(InlineCollection target, IReadOnlyList<MarkdownInline> inlines, IReadOnlyList<ReportLinkViewModel> links)
        {
            if (inlines == null) return;
            var frames = new Stack<Frame>();
            frames.Push(new Frame(target, inlines, 0, false, false));
            while (frames.Count > 0)
            {
                var frame = frames.Peek();
                if (frame.Index >= frame.Items.Count) { frames.Pop(); continue; }
                var inline = frame.Items[frame.Index++];
                if (inline == null) continue;
                switch (inline.Kind)
                {
                    case MarkdownInlineKind.Strong:
                    case MarkdownInlineKind.Emphasis:
                    {
                        var strong = inline.Kind == MarkdownInlineKind.Strong;
                        if (frame.Depth < MaxSpanDepth)
                        {
                            Span span = strong ? (Span)new Bold() : new Italic();
                            frame.Target.Add(Styled(span, frame));
                            frames.Push(new Frame(span.Inlines, inline.Children, frame.Depth + 1, false, false));
                        }
                        else frames.Push(new Frame(frame.Target, inline.Children, frame.Depth, frame.Bold || strong, frame.Italic || !strong));
                        break;
                    }
                    case MarkdownInlineKind.Code:
                        frame.Target.Add(Styled(new Run(inline.Text ?? "")
                            { FontFamily = ReportsStyles.Mono, FontSize = 11.5, Background = ReportsStyles.CodeBackground }, frame));
                        break;
                    case MarkdownInlineKind.LineBreak:
                        frame.Target.Add(new LineBreak());
                        break;
                    case MarkdownInlineKind.Link:
                        AddLink(frames, frame, inline, links);
                        break;
                    default:
                        frame.Target.Add(Styled(new Run(inline.Text ?? ""), frame));
                        break;
                }
            }
        }

        private static void AddLink(Stack<Frame> frames, Frame frame, MarkdownInline inline, IReadOnlyList<ReportLinkViewModel> links)
        {
            var index = inline.Link?.Syntax?.StoredIndex;
            var link = index.HasValue && index.Value >= 0 && index.Value < links.Count ? links[index.Value] : null;
            var label = inline.Link?.Label ?? inline.Text ?? "";
            if (link == null)
            {
                // Not a resolvable revit:#n: the label stays plain text and nothing is clickable.
                if (inline.Children.Count > 0) frames.Push(new Frame(frame.Target, inline.Children, frame.Depth, frame.Bold, frame.Italic));
                else frame.Target.Add(Styled(new Run(label), frame));
                return;
            }
            var hyperlink = CreateLink(link, link.OpenCommand, L.T("reports.link.otherModel", ("model", link.DimTooltip)));
            // A local tooltip wins over the style's dim-only one: body links say what they point at, live or dimmed.
            hyperlink.ToolTip = ReportLinkTooltip.Create(link);
            frame.Target.Add(Styled(hyperlink, frame));
            if (link.ElementCount > 1)
                frame.Target.Add(Badge(link, link.ElementCount.ToString(CultureInfo.InvariantCulture), ReportsStyles.CountBadgeStyle));
            if (link.TargetKind == ReportLinkTargetKind.View || link.TargetKind == ReportLinkTargetKind.Sheet)
                frame.Target.Add(Badge(link, L.T(link.TargetKind == ReportLinkTargetKind.View ? "reports.link.view" : "reports.link.sheet"),
                    ReportsStyles.TagStyle));
            if (inline.Children.Count > 0) frames.Push(new Frame(hyperlink.Inlines, inline.Children, frame.Depth + 1, false, false));
            else hyperlink.Inlines.Add(new Run(label));
        }

        private static InlineUIContainer Badge(ReportLinkViewModel link, string text, Style style) =>
            new InlineUIContainer(new Border { Style = style, DataContext = link, Child = new TextBlock { Text = text } })
                { BaselineAlignment = BaselineAlignment.Center };

        private static Inline Styled(Inline inline, Frame frame)
        {
            if (frame.Bold) inline.FontWeight = FontWeights.Bold;
            if (frame.Italic) inline.FontStyle = FontStyles.Italic;
            return inline;
        }

        private sealed class Frame
        {
            public Frame(InlineCollection target, IReadOnlyList<MarkdownInline> items, int depth, bool bold, bool italic)
            {
                Target = target; Items = items ?? new MarkdownInline[0]; Depth = depth; Bold = bold; Italic = italic;
            }
            public InlineCollection Target { get; }
            public IReadOnlyList<MarkdownInline> Items { get; }
            public int Depth { get; }
            public bool Bold { get; }
            public bool Italic { get; }
            public int Index { get; set; }
        }
    }
}
