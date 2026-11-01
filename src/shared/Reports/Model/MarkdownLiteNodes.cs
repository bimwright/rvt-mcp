using System;
using System.Collections;
using System.Collections.Generic;

namespace RvtMcp.Plugin.Reports.Model
{
    public enum MarkdownBlockKind { Paragraph, Heading, List, CodeFence, HorizontalRule, Table }
    public enum MarkdownInlineKind { Text, Strong, Emphasis, Code, Link, LineBreak }

    public sealed class MarkdownInline
    {
        public MarkdownInlineKind Kind { get; set; }
        public string Text { get; set; }
        public List<MarkdownInline> Children { get; } = new List<MarkdownInline>();
        public MarkdownLinkOccurrence Link { get; set; }
    }

    public sealed class MarkdownListItem
    {
        public int Level { get; set; }
        public bool IsOrdered { get; set; }
        // Text preserves arbitrarily large starting numbers without integer overflow.
        public string Number { get; set; }
        public List<MarkdownInline> Inlines { get; } = new List<MarkdownInline>();
    }

    public sealed class MarkdownTableRow : IReadOnlyList<IReadOnlyList<MarkdownInline>>
    {
        private static readonly IReadOnlyList<MarkdownInline> Empty = new MarkdownInline[0];
        private readonly List<IReadOnlyList<MarkdownInline>> _cells = new List<IReadOnlyList<MarkdownInline>>();
        public IReadOnlyList<IReadOnlyList<MarkdownInline>> Cells => this;
        public int Count { get; private set; }
        internal int ContentCellCount => Math.Min(Count, _cells.Count);
        public IReadOnlyList<MarkdownInline> this[int index]
        {
            get
            {
                if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
                return index < _cells.Count ? _cells[index] : Empty;
            }
        }
        internal void AddCell(IReadOnlyList<MarkdownInline> cell) { _cells.Add(cell); Count = _cells.Count; }
        // Virtual padding keeps wide, sparse tables linear in source size.
        internal void SetColumnCount(int count) { Count = count; }
        public IEnumerator<IReadOnlyList<MarkdownInline>> GetEnumerator()
        {
            for (int i = 0; i < Count; i++) yield return this[i];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public sealed class MarkdownBlock
    {
        public MarkdownBlockKind Kind { get; set; }
        public int Level { get; set; }
        public string Text { get; set; }
        public List<MarkdownInline> Inlines { get; } = new List<MarkdownInline>();
        public List<MarkdownListItem> Items { get; } = new List<MarkdownListItem>();
        // First row is the header; alignment markers are deliberately not retained.
        public List<MarkdownTableRow> Rows { get; } = new List<MarkdownTableRow>();
    }

    public sealed class MarkdownLinkOccurrence
    {
        public string Label { get; set; }
        public string Target { get; set; }
        // UTF-16 offsets into the LF-normalized Body, including the full markup span.
        public int Start { get; set; }
        public int Length { get; set; }
        public int TargetStart { get; set; }
        public int TargetLength { get; set; }
        public LinkTarget Syntax { get; set; }
        public string Reason { get; set; }
    }

    public sealed class MarkdownParseResult
    {
        public string Body { get; set; }
        public List<MarkdownBlock> Blocks { get; } = new List<MarkdownBlock>();
        public List<MarkdownLinkOccurrence> Links { get; } = new List<MarkdownLinkOccurrence>();
        public string ToPlainText() => MarkdownLiteParser.ToPlainText(this);
    }
}
