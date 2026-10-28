using System.Collections.Generic;
using System.Text;

namespace RvtMcp.Plugin.Reports.Model
{
    public static class MarkdownLiteParser
    {
        public static MarkdownParseResult Parse(string body, ReportParseMode mode = ReportParseMode.Raw, int storedLinkCount = 0)
        {
            var result = new MarkdownParseResult { Body = (body ?? "").Replace("\r\n", "\n").Replace('\r', '\n') };
            var scanner = new InlineScanner(result, mode, storedLinkCount);
            MarkdownBlock paragraph = null;
            MarkdownBlock list = null;
            MarkdownListItem pendingItem = null;
            int spanStart = 0, spanEnd = 0;
            void FlushPending()
            {
                if (pendingItem != null)
                    pendingItem.Inlines.AddRange(scanner.Parse(result.Body.Substring(spanStart, spanEnd - spanStart), spanStart));
                else if (paragraph != null)
                    paragraph.Inlines.AddRange(scanner.Parse(result.Body.Substring(spanStart, spanEnd - spanStart), spanStart));
                pendingItem = null;
                paragraph = null;
            }
            var lines = result.Body.Split('\n');
            var starts = new int[lines.Length];
            for (int n = 1; n < lines.Length; n++) starts[n] = starts[n - 1] + lines[n - 1].Length + 1;
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) { FlushPending(); list = null; continue; }
                bool isListItem = TryListItem(line, out var item, out int contentStart);
                var trimmed = line.Trim();
                bool rule = trimmed.Length >= 3;
                for (int n = 0; n < trimmed.Length && rule; n++) rule = trimmed[n] == '-';
                if (rule)
                {
                    FlushPending();
                    result.Blocks.Add(new MarkdownBlock { Kind = MarkdownBlockKind.HorizontalRule });
                    paragraph = null;
                    list = null;
                    continue;
                }
                if (line.StartsWith("```", System.StringComparison.Ordinal))
                {
                    FlushPending();
                    var code = new StringBuilder();
                    int first = ++i;
                    while (i < lines.Length && lines[i].TrimEnd() != "```")
                    {
                        if (i > first) code.Append('\n');
                        code.Append(lines[i++]);
                    }
                    result.Blocks.Add(new MarkdownBlock { Kind = MarkdownBlockKind.CodeFence, Text = code.ToString() });
                    paragraph = null;
                    list = null;
                    continue;
                }
                int level = 0;
                while (level < line.Length && line[level] == '#') level++;
                if (level >= 1 && level <= 3 && level < line.Length && line[level] == ' ')
                {
                    FlushPending();
                    var heading = new MarkdownBlock { Kind = MarkdownBlockKind.Heading, Level = level };
                    heading.Inlines.AddRange(scanner.Parse(line.Substring(level + 1), starts[i] + level + 1));
                    result.Blocks.Add(heading);
                    paragraph = null;
                    list = null;
                    continue;
                }
                if (line.IndexOf('|') >= 0 && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
                {
                    FlushPending();
                    var table = new MarkdownBlock { Kind = MarkdownBlockKind.Table };
                    var header = ParseTableRow(line, starts[i], scanner);
                    table.Rows.Add(header);
                    int next = i + 2;
                    while (next < lines.Length && !string.IsNullOrWhiteSpace(lines[next]) && lines[next].IndexOf('|') >= 0)
                    {
                        var row = ParseTableRow(lines[next], starts[next], scanner, header.Cells.Count);
                        row.SetColumnCount(header.Cells.Count);
                        table.Rows.Add(row);
                        next++;
                    }
                    result.Blocks.Add(table);
                    i = next - 1;
                    paragraph = null;
                    list = null;
                    continue;
                }
                if (isListItem)
                {
                    FlushPending();
                    if (list == null)
                    {
                        list = new MarkdownBlock { Kind = MarkdownBlockKind.List };
                        result.Blocks.Add(list);
                    }
                    list.Items.Add(item);
                    pendingItem = item;
                    spanStart = starts[i] + contentStart;
                    spanEnd = starts[i] + line.Length;
                    continue;
                }
                if (list != null)
                {
                    spanEnd = starts[i] + line.Length;
                    continue;
                }
                if (paragraph == null)
                {
                    paragraph = new MarkdownBlock { Kind = MarkdownBlockKind.Paragraph };
                    result.Blocks.Add(paragraph);
                    spanStart = starts[i];
                }
                spanEnd = starts[i] + line.Length;
            }
            FlushPending();
            return result;
        }

        private static bool IsTableSeparator(string line)
        {
            bool pipe = false, dash = false;
            foreach (char c in line)
            {
                if (c == '|') pipe = true;
                else if (c == '-') dash = true;
                else if (c != ':' && !char.IsWhiteSpace(c)) return false;
            }
            return pipe && dash;
        }

        private static MarkdownTableRow ParseTableRow(string line, int offset, InlineScanner scanner, int columns = int.MaxValue)
        {
            int start = 0, end = line.Length;
            while (start < end && char.IsWhiteSpace(line[start])) start++;
            while (end > start && char.IsWhiteSpace(line[end - 1])) end--;
            if (start < end && line[start] == '|') start++;
            if (end > start && line[end - 1] == '|')
            {
                int slashes = 0;
                for (int n = end - 2; n >= start && line[n] == '\\'; n--) slashes++;
                if (slashes % 2 == 0) end--;
            }
            var row = new MarkdownTableRow();
            var nextTick = new int[end + 1];
            int tick = -1;
            for (int n = end - 1; n >= start; n--)
            {
                nextTick[n] = tick;
                if (line[n] == '`') tick = n;
            }
            bool escaped = false;
            int codeEnd = -1;
            int cellStart = start;
            for (int n = start; n <= end && row.Cells.Count < columns; n++)
            {
                if (n < end && n == codeEnd) { codeEnd = -1; escaped = false; continue; }
                if (n < end && line[n] == '`' && codeEnd < 0 && !escaped && nextTick[n] >= 0)
                {
                    codeEnd = nextTick[n];
                    continue;
                }
                bool pipeEscaped = codeEnd >= 0 ? n > start && line[n - 1] == '\\' : escaped;
                if (n == end || line[n] == '|' && !pipeEscaped)
                {
                    int left = cellStart, right = n;
                    while (left < right && char.IsWhiteSpace(line[left])) left++;
                    while (right > left && char.IsWhiteSpace(line[right - 1])) right--;
                    row.AddCell(scanner.Parse(line.Substring(left, right - left), offset + left, true));
                    cellStart = n + 1;
                    codeEnd = -1;
                }
                if (n < end) escaped = codeEnd < 0 && line[n] == '\\' && !escaped;
            }
            return row;
        }

        private static bool TryListItem(string line, out MarkdownListItem item, out int contentStart)
        {
            item = null;
            contentStart = 0;
            int indent = 0;
            while (contentStart < line.Length && (line[contentStart] == ' ' || line[contentStart] == '\t'))
                indent += line[contentStart++] == '\t' ? 4 : 1;
            int marker = contentStart;
            if (marker == line.Length) return false;
            bool ordered = line[marker] >= '0' && line[marker] <= '9';
            string number = null;
            if (ordered)
            {
                while (contentStart < line.Length && line[contentStart] >= '0' && line[contentStart] <= '9') contentStart++;
                number = line.Substring(marker, contentStart - marker);
                if (contentStart == line.Length || line[contentStart++] != '.') return false;
            }
            else if (line[contentStart] == '-' || line[contentStart] == '*') contentStart++;
            else return false;
            if (contentStart == line.Length || line[contentStart++] != ' ') return false;
            item = new MarkdownListItem { Level = indent >= 2 ? 2 : 1, IsOrdered = ordered, Number = number };
            return true;
        }

        public static string ToPlainText(MarkdownParseResult result)
        {
            var text = new StringBuilder();
            foreach (var block in result.Blocks)
            {
                if (text.Length > 0) text.Append("\n\n");
                if (block.Kind == MarkdownBlockKind.CodeFence) { text.Append(block.Text); continue; }
                if (block.Kind == MarkdownBlockKind.HorizontalRule) { text.Append("---"); continue; }
                if (block.Kind == MarkdownBlockKind.Table)
                {
                    for (int row = 0; row < block.Rows.Count; row++)
                    {
                        if (row > 0) text.Append('\n');
                        for (int col = 0; col < block.Rows[row].ContentCellCount; col++)
                        {
                            if (col > 0) text.Append(" | ");
                            AppendInlines(text, block.Rows[row].Cells[col]);
                        }
                    }
                    continue;
                }
                if (block.Kind == MarkdownBlockKind.List)
                {
                    for (int i = 0; i < block.Items.Count; i++)
                    {
                        var item = block.Items[i];
                        if (i > 0) text.Append('\n');
                        if (item.Level == 2) text.Append("  ");
                        text.Append(item.IsOrdered ? item.Number + ". " : "- ");
                        AppendInlines(text, item.Inlines);
                    }
                    continue;
                }
                AppendInlines(text, block.Inlines);
            }
            return text.ToString();
        }

        private static void AppendInlines(StringBuilder text, IReadOnlyList<MarkdownInline> inlines)
        {
            var pending = new Stack<MarkdownInline>();
            for (int i = inlines.Count - 1; i >= 0; i--) pending.Push(inlines[i]);
            while (pending.Count > 0)
            {
                var inline = pending.Pop();
                if (inline.Kind == MarkdownInlineKind.Strong || inline.Kind == MarkdownInlineKind.Emphasis)
                {
                    for (int i = inline.Children.Count - 1; i >= 0; i--) pending.Push(inline.Children[i]);
                }
                else text.Append(inline.Kind == MarkdownInlineKind.LineBreak ? "\n" : inline.Text);
            }
        }

        private sealed class InlineScanner
        {
            private readonly MarkdownParseResult _result;
            private readonly ReportParseMode _mode;
            private readonly int _storedLinkCount;
            private int _validLinks;
            private int _acceptedIds;

            public InlineScanner(MarkdownParseResult result, ReportParseMode mode, int storedLinkCount)
            {
                _result = result;
                _mode = mode;
                _storedLinkCount = storedLinkCount;
            }

            public List<MarkdownInline> Parse(string text, int bodyOffset, bool inTable = false)
            {
                var tokens = new List<InlineToken>();
                var openMarkers = new List<int>();
                var lastMarker = new[] { -1, -1, -1 };
                var value = new StringBuilder();
                var nextTick = new int[text.Length + 1];
                var nextParen = new int[text.Length + 1];
                var brackets = new int[text.Length];
                nextTick[text.Length] = -1;
                nextParen[text.Length] = -1;
                for (int n = text.Length - 1; n >= 0; n--)
                {
                    nextTick[n] = text[n] == '`' ? n : nextTick[n + 1];
                    nextParen[n] = text[n] == '\n' ? -1 : text[n] == ')' ? n : nextParen[n + 1];
                    brackets[n] = -1;
                }
                var opens = new Stack<int>();
                for (int n = 0; n < text.Length; n++)
                {
                    if (text[n] == '\\' && n + 1 < text.Length && IsEscapable(text[n + 1])) { n++; continue; }
                    if (text[n] == '\n') opens.Clear();
                    else if (text[n] == '[') opens.Push(n);
                    else if (text[n] == ']' && opens.Count > 0) brackets[opens.Pop()] = n;
                }
                for (int i = 0; i < text.Length; i++)
                {
                    if (text[i] == '\\' && i + 1 < text.Length && IsEscapable(text[i + 1]))
                    {
                        value.Append(text[++i]);
                        continue;
                    }
                    if (text[i] == '`' && nextTick[i + 1] >= 0)
                    {
                        FlushText(value, tokens);
                        int close = nextTick[i + 1];
                        var code = text.Substring(i + 1, close - i - 1);
                        tokens.Add(new InlineToken { Node = new MarkdownInline { Kind = MarkdownInlineKind.Code, Text = inTable ? code.Replace("\\|", "|") : code } });
                        i = close;
                        continue;
                    }
                    if (text[i] == '\n')
                    {
                        FlushText(value, tokens);
                        tokens.Add(new InlineToken { Node = new MarkdownInline { Kind = MarkdownInlineKind.LineBreak } });
                        continue;
                    }
                    if (text[i] == '!' && i + 1 < text.Length && text[i + 1] == '[' &&
                        CompleteLink(text, i + 1, brackets, nextParen, out _, out int imageEnd))
                    {
                        value.Append(DecodePlain(text.Substring(i, imageEnd - i + 1)));
                        i = imageEnd;
                        continue;
                    }
                    if (text[i] == '[' && CompleteLink(text, i, brackets, nextParen, out int labelEnd, out int linkEnd))
                    {
                        string label = DecodePlain(text.Substring(i + 1, labelEnd - i - 1));
                        int targetStart = labelEnd + 2;
                        string target = text.Substring(targetStart, linkEnd - targetStart);
                        if (!target.StartsWith("revit:", System.StringComparison.OrdinalIgnoreCase)) value.Append(label);
                        else
                        {
                            var syntax = LinkSyntax.Parse(target, _mode, _storedLinkCount);
                            string reason = string.IsNullOrWhiteSpace(label) ? "bad_syntax" : syntax.Reason;
                            if (_mode == ReportParseMode.Raw && reason == null)
                            {
                                _validLinks++;
                                if (_validLinks > ReportLimits.Links || syntax.Ids.Count > ReportLimits.IdsPerLink ||
                                    _acceptedIds + syntax.Ids.Count > ReportLimits.TotalIds) reason = "limit_exceeded";
                                else _acceptedIds += syntax.Ids.Count;
                            }
                            var occurrence = new MarkdownLinkOccurrence
                            {
                                Label = label, Target = target, Start = bodyOffset + i, Length = linkEnd - i + 1,
                                TargetStart = bodyOffset + targetStart, TargetLength = target.Length,
                                Syntax = syntax, Reason = reason
                            };
                            if (_mode == ReportParseMode.Raw || reason == null) _result.Links.Add(occurrence);
                            if (reason == null)
                            {
                                FlushText(value, tokens);
                                tokens.Add(new InlineToken { Node = new MarkdownInline { Kind = MarkdownInlineKind.Link, Text = label, Link = occurrence } });
                            }
                            else value.Append(label);
                        }
                        i = linkEnd;
                        continue;
                    }
                    if (text[i] == '*')
                    {
                        FlushText(value, tokens);
                        int end = i + 1;
                        while (end < text.Length && text[end] == '*') end++;
                        bool canOpen = end < text.Length && !char.IsWhiteSpace(text[end]);
                        bool canClose = i > 0 && !char.IsWhiteSpace(text[i - 1]);
                        int remaining = end - i;
                        while (remaining > 0)
                        {
                            int size = remaining == 1 ? 1 : remaining == 2 ? 2 :
                                openMarkers.Count > 0 ? tokens[openMarkers[openMarkers.Count - 1]].Marker : 2;
                            AddMarker(tokens, openMarkers, lastMarker, size, canOpen, canClose);
                            remaining -= size;
                        }
                        i = end - 1;
                        continue;
                    }
                    value.Append(text[i]);
                }
                FlushText(value, tokens);
                return BuildInlines(tokens);
            }

            private static bool CompleteLink(string text, int start, int[] brackets, int[] nextParen, out int labelEnd, out int linkEnd)
            {
                labelEnd = brackets[start];
                linkEnd = -1;
                if (labelEnd < 0 || labelEnd + 1 >= text.Length || text[labelEnd + 1] != '(') return false;
                linkEnd = nextParen[labelEnd + 2];
                return linkEnd >= 0;
            }

            private static string DecodePlain(string text)
            {
                var value = new StringBuilder();
                for (int i = 0; i < text.Length; i++)
                {
                    if (text[i] == '\\' && i + 1 < text.Length && IsEscapable(text[i + 1])) i++;
                    value.Append(text[i]);
                }
                return value.ToString();
            }

            private sealed class InlineToken
            {
                public MarkdownInline Node;
                public int Marker;
                public int Match = -1;
                public int Previous = -1;
            }

            private static void AddMarker(List<InlineToken> tokens, List<int> openMarkers, int[] lastMarker,
                int size, bool canOpen, bool canClose)
            {
                int index = tokens.Count;
                int open = lastMarker[size];
                tokens.Add(new InlineToken { Marker = size, Previous = open });
                if (!canClose || open < 0)
                {
                    if (canOpen)
                    {
                        openMarkers.Add(index);
                        lastMarker[size] = index;
                    }
                    return;
                }
                // Each discarded unmatched marker is popped once; nested malformed input stays linear.
                while (openMarkers.Count > 0)
                {
                    int popped = openMarkers[openMarkers.Count - 1];
                    openMarkers.RemoveAt(openMarkers.Count - 1);
                    lastMarker[tokens[popped].Marker] = tokens[popped].Previous;
                    if (popped == open) break;
                }
                tokens[open].Match = index;
                tokens[index].Match = open;
            }

            private static List<MarkdownInline> BuildInlines(List<InlineToken> tokens)
            {
                var result = new List<MarkdownInline>();
                var parents = new Stack<List<MarkdownInline>>();
                var current = result;
                for (int i = 0; i < tokens.Count; i++)
                {
                    var token = tokens[i];
                    if (token.Marker == 0) current.Add(token.Node);
                    else if (token.Match > i)
                    {
                        var node = new MarkdownInline { Kind = token.Marker == 2 ? MarkdownInlineKind.Strong : MarkdownInlineKind.Emphasis };
                        current.Add(node);
                        parents.Push(current);
                        current = node.Children;
                    }
                    else if (token.Match >= 0) current = parents.Pop();
                    else current.Add(new MarkdownInline { Kind = MarkdownInlineKind.Text, Text = new string('*', token.Marker) });
                }
                return result;
            }

            private static void FlushText(StringBuilder text, List<InlineToken> tokens)
            {
                if (text.Length == 0) return;
                tokens.Add(new InlineToken { Node = new MarkdownInline { Kind = MarkdownInlineKind.Text, Text = text.ToString() } });
                text.Clear();
            }

            private static bool IsEscapable(char value) => "\\`*_[]()|#-".IndexOf(value) >= 0;
        }
    }
}
