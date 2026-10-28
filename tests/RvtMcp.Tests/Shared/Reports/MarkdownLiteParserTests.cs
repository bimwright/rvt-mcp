using System.Diagnostics;
using System.Linq;
using RvtMcp.Plugin.Reports.Model;
using Xunit;
using Xunit.Abstractions;

namespace RvtMcp.Tests.Shared.Reports
{
    public sealed class MarkdownLiteParserTests
    {
        private readonly ITestOutputHelper _output;
        public MarkdownLiteParserTests(ITestOutputHelper output) { _output = output; }

        [Fact]
        public void Extra_table_cells_do_not_create_link_occurrences()
        {
            var parsed = MarkdownLiteParser.Parse("| a | b |\n|---|---|\n| 1 | 2 | [Ẩn](revit:9) |");
            Assert.Empty(parsed.Links);
            Assert.Equal(2, parsed.Blocks[0].Rows[1].Cells.Count);
            Assert.DoesNotContain("Ẩn", parsed.ToPlainText());
        }

        [Theory]
        [InlineData("links")]
        [InlineData("ids")]
        public void Extra_table_cells_do_not_spend_report_link_or_id_budgets(string budget)
        {
            var hidden = budget == "links"
                ? string.Concat(Enumerable.Repeat("[Ẩn](revit:9)", 301))
                : string.Concat(Enumerable.Repeat("[Ẩn](revit:" + string.Join(",", Enumerable.Range(1, 500)) + ")", 4));
            var parsed = MarkdownLiteParser.Parse("| a | b |\n|---|---|\n| 1 | 2 | " + hidden + " |\n\n[Hiện](revit:5)");
            Assert.Null(parsed.Links.Last().Reason);
            Assert.Equal("Hiện", Assert.Single(parsed.Links).Label);
        }

        [Theory]
        [InlineData("``` ")]
        [InlineData("```\t")]
        [InlineData("``` \t")]
        [InlineData("```\u00a0")]
        public void Fence_closer_ignores_trailing_whitespace(string closer)
        {
            var parsed = MarkdownLiteParser.Parse("```\nx\n" + closer + "\nSau fence");
            Assert.Equal(2, parsed.Blocks.Count);
            Assert.Equal(MarkdownBlockKind.CodeFence, parsed.Blocks[0].Kind);
            Assert.Equal("x", parsed.Blocks[0].Text);
            Assert.Equal(MarkdownBlockKind.Paragraph, parsed.Blocks[1].Kind);
            Assert.Equal("Sau fence", Assert.Single(parsed.Blocks[1].Inlines).Text);
        }

        [Fact]
        public void Leading_indentation_is_not_removed_from_a_fence_closer()
        {
            var parsed = MarkdownLiteParser.Parse("```\nx\n ``` \nSau fence");
            var code = Assert.Single(parsed.Blocks);
            Assert.Equal(MarkdownBlockKind.CodeFence, code.Kind);
            Assert.Equal("x\n ``` \nSau fence", code.Text);
        }

        [Theory]
        [InlineData("# next", MarkdownBlockKind.Heading)]
        [InlineData("---", MarkdownBlockKind.HorizontalRule)]
        [InlineData("```lang", MarkdownBlockKind.CodeFence)]
        [InlineData("A | B\n|-|-|", MarkdownBlockKind.Table)]
        public void Recognized_blocks_end_a_list_without_a_blank_line(string following, MarkdownBlockKind kind)
        {
            var parsed = MarkdownLiteParser.Parse("- first\n" + following);
            Assert.Equal(2, parsed.Blocks.Count);
            var list = parsed.Blocks[0];
            Assert.Equal(MarkdownBlockKind.List, list.Kind);
            Assert.Single(list.Items);
            Assert.Equal("first", Assert.Single(list.Items[0].Inlines).Text);
            Assert.Equal(kind, parsed.Blocks[1].Kind);
        }

        [Fact]
        public void Heading_after_bullets_starts_its_own_block_and_following_paragraph()
        {
            var parsed = MarkdownLiteParser.Parse("- Ống A\n- Ống B\n## Kết luận\nXong");
            Assert.Equal(new[] { MarkdownBlockKind.List, MarkdownBlockKind.Heading, MarkdownBlockKind.Paragraph },
                parsed.Blocks.Select(block => block.Kind));
            Assert.Equal(2, parsed.Blocks[0].Items.Count);
            Assert.Equal("Kết luận", Assert.Single(parsed.Blocks[1].Inlines).Text);
            Assert.Equal("Xong", Assert.Single(parsed.Blocks[2].Inlines).Text);
        }

        [Fact]
        public void Fence_after_ordered_item_starts_its_own_code_block()
        {
            var parsed = MarkdownLiteParser.Parse("1. Chạy lệnh:\n```\ncode\n```");
            Assert.Equal(2, parsed.Blocks.Count);
            var item = Assert.Single(parsed.Blocks[0].Items);
            Assert.Equal("Chạy lệnh:", Assert.Single(item.Inlines).Text);
            Assert.Equal(MarkdownBlockKind.CodeFence, parsed.Blocks[1].Kind);
            Assert.Equal("code", parsed.Blocks[1].Text);
        }

        [Fact]
        public void Ordinary_text_continues_a_list_item_when_it_is_not_a_block_opener()
        {
            const string body = "- first\n#not-heading\n#### text\n--\nA | B\nnot a separator";
            var parsed = MarkdownLiteParser.Parse(body);
            Assert.Single(Assert.Single(parsed.Blocks).Items);
            Assert.Equal(body, parsed.ToPlainText());
        }

        [Fact]
        public void List_indent_boundary_and_blank_line_termination_are_explicit()
        {
            var parsed = MarkdownLiteParser.Parse(" - first\n  - second\n\nplain");
            Assert.Equal(2, parsed.Blocks.Count);
            Assert.Equal(new[] { 1, 2 }, parsed.Blocks[0].Items.ConvertAll(item => item.Level));
            Assert.Equal(MarkdownBlockKind.Paragraph, parsed.Blocks[1].Kind);
        }

        [Fact]
        public void Blank_line_ends_a_table_and_HTML_blockquote_and_autolink_are_literal()
        {
            var parsed = MarkdownLiteParser.Parse("A|B\n-|-\nx|y\n\n<div>literal</div>\n> quoted\n<https://example.invalid/path>");
            Assert.Equal(2, parsed.Blocks.Count);
            Assert.Equal(MarkdownBlockKind.Table, parsed.Blocks[0].Kind);
            Assert.Equal(MarkdownBlockKind.Paragraph, parsed.Blocks[1].Kind);
            Assert.Empty(parsed.Links);
            Assert.EndsWith("<div>literal</div>\n> quoted\n<https://example.invalid/path>", parsed.ToPlainText());
        }

        [Fact]
        public void Stored_body_over_agent_byte_limit_is_parsed_without_revalidation()
        {
            var body = new string('a', 32769) + "[x](revit:#0)";
            var parsed = MarkdownLiteParser.Parse(body, ReportParseMode.Stored, 1);
            Assert.Null(Assert.Single(parsed.Links).Reason);
            Assert.Equal(new string('a', 32769) + "x", parsed.ToPlainText());
        }

        [Fact]
        public void Escaped_label_brackets_and_parentheses_stay_plain_and_balanced()
        {
            var parsed = MarkdownLiteParser.Parse("[a\\[b\\] c\\)d](revit:1)");
            var link = Assert.Single(parsed.Links);
            Assert.Null(link.Reason);
            Assert.Equal("a[b] c)d", link.Label);
            Assert.Equal("a[b] c)d", parsed.ToPlainText());
        }

        [Fact]
        public void Emphasis_can_span_preserved_line_breaks_in_paragraphs_and_list_items()
        {
            var parsed = MarkdownLiteParser.Parse("**one\ntwo**\n\n- *three\nfour*");
            var strong = Assert.Single(parsed.Blocks[0].Inlines);
            Assert.Equal(MarkdownInlineKind.Strong, strong.Kind);
            Assert.Equal(MarkdownInlineKind.LineBreak, strong.Children[1].Kind);
            var emphasis = Assert.Single(parsed.Blocks[1].Items[0].Inlines);
            Assert.Equal(MarkdownInlineKind.Emphasis, emphasis.Kind);
            Assert.Equal("one\ntwo\n\n- three\nfour", parsed.ToPlainText());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" \t\r\n\r")]
        public void Empty_body_has_no_blocks_or_links(string body)
        {
            var parsed = MarkdownLiteParser.Parse(body);
            Assert.Empty(parsed.Blocks);
            Assert.Empty(parsed.Links);
            Assert.Equal("", parsed.ToPlainText());
        }

        [Theory]
        [InlineData("A | B\nnot a divider\nx | y")]
        [InlineData("A | B\n| : |\nx | y")]
        [InlineData("A | B\n| -x |\nx | y")]
        public void Table_requires_an_immediate_valid_separator(string body)
        {
            var parsed = MarkdownLiteParser.Parse(body);
            Assert.Equal(MarkdownBlockKind.Paragraph, Assert.Single(parsed.Blocks).Kind);
            Assert.Equal(body, parsed.ToPlainText());
        }

        [Fact]
        public void Very_large_ordered_start_number_is_preserved_without_overflow()
        {
            var number = new string('9', 200);
            var block = Assert.Single(MarkdownLiteParser.Parse(number + ". first\n\nplain").Blocks.Where(b => b.Kind == MarkdownBlockKind.List));
            Assert.Equal(number, Assert.Single(block.Items).Number);
        }

        [Fact]
        public void Wide_sparse_32KiB_table_pads_cells_without_quadratic_allocation()
        {
            var body = string.Concat(Enumerable.Repeat("|a", 8192)) + "\n|-|\n" + string.Concat(Enumerable.Repeat("|x\n", 4096));
            body = body.PadRight(32768, ' ');
            MarkdownLiteParser.Parse("|warm|\n|-|\n|x|");
            var clock = Stopwatch.StartNew();
            var parsed = MarkdownLiteParser.Parse(body);
            var copy = parsed.ToPlainText();
            clock.Stop();
            var table = Assert.Single(parsed.Blocks);
            Assert.Equal(4097, table.Rows.Count);
            Assert.Equal(8192, table.Rows[4096].Cells.Count);
            Assert.Empty(table.Rows[4096].Cells[8191]);
            Assert.Contains("a | a", copy);
            _output.WriteLine("wide_sparse_table_32768_bytes: {0:F3} ms", clock.Elapsed.TotalMilliseconds);
            Assert.True(clock.Elapsed.TotalMilliseconds < 200, "Wide sparse table parse/copy exceeded 200 ms.");
        }

        [Fact]
        public void Escaped_pipes_stay_in_table_cells_even_inside_code_spans()
        {
            var table = Assert.Single(MarkdownLiteParser.Parse("A | B\n:--|--:\na\\|b | `c\\|d`\n`x\\\\|y` | z").Blocks);
            Assert.Equal(2, table.Rows[1].Cells.Count);
            Assert.Equal("a|b", Assert.Single(table.Rows[1].Cells[0]).Text);
            Assert.Equal("c|d", Assert.Single(table.Rows[1].Cells[1]).Text);
            var code = Assert.Single(table.Rows[2].Cells[0]);
            Assert.Equal(MarkdownInlineKind.Code, code.Kind);
            Assert.Equal("x\\|y", code.Text);
        }

        [Fact]
        public void Pipe_table_uses_header_width_and_ends_at_non_pipe_line()
        {
            var parsed = MarkdownLiteParser.Parse("| A | B |\n|:---|---:|\n| one | two | extra |\n| short |\nafter");
            Assert.Equal(2, parsed.Blocks.Count);
            var table = parsed.Blocks[0];
            Assert.Equal(MarkdownBlockKind.Table, table.Kind);
            Assert.Equal(3, table.Rows.Count);
            Assert.All(table.Rows, row => Assert.Equal(2, row.Cells.Count));
            Assert.Empty(table.Rows[2].Cells[1]);
            Assert.Equal(MarkdownBlockKind.Paragraph, parsed.Blocks[1].Kind);
            Assert.DoesNotContain("extra", parsed.ToPlainText());
            Assert.Contains("one | two", parsed.ToPlainText());
        }

        [Fact]
        public void Lists_preserve_start_number_clamp_nesting_expand_tabs_and_attach_continuations()
        {
            var result = MarkdownLiteParser.Parse("7. first\n  - nested\n        * deep\n\t- tab\ncontinued\n8. last");
            var block = Assert.Single(result.Blocks);
            Assert.Equal(MarkdownBlockKind.List, block.Kind);
            Assert.Equal(5, block.Items.Count);
            Assert.Equal("7", block.Items[0].Number);
            Assert.True(block.Items[0].IsOrdered);
            Assert.Equal(new[] { 1, 2, 2, 2, 1 }, block.Items.ConvertAll(item => item.Level));
            Assert.Equal("8", block.Items[4].Number);
            Assert.Contains("tab\ncontinued", result.ToPlainText());
        }

        [Theory]
        [InlineData("---", true)]
        [InlineData("  -----\t", true)]
        [InlineData("--", false)]
        [InlineData("-- --", false)]
        public void Horizontal_rule_requires_only_three_or_more_hyphens(string body, bool rule)
        {
            var parsed = MarkdownLiteParser.Parse(body);
            Assert.Equal(rule ? MarkdownBlockKind.HorizontalRule : MarkdownBlockKind.Paragraph, Assert.Single(parsed.Blocks).Kind);
            Assert.Equal(rule ? "---" : body, parsed.ToPlainText());
        }

        [Theory]
        [InlineData("```lang\nraw *text* [x](revit:1)\n```\nafter", "raw *text* [x](revit:1)", 2)]
        [InlineData("```\nraw\nlast", "raw\nlast", 1)]
        public void Code_fence_is_verbatim_and_unclosed_fence_runs_to_end(string body, string code, int count)
        {
            var parsed = MarkdownLiteParser.Parse(body);
            Assert.Equal(count, parsed.Blocks.Count);
            Assert.Equal(MarkdownBlockKind.CodeFence, parsed.Blocks[0].Kind);
            Assert.Equal(code, parsed.Blocks[0].Text);
            Assert.Empty(parsed.Links);
            Assert.StartsWith(code, parsed.ToPlainText());
        }

        [Theory]
        [InlineData("# first", 1)]
        [InlineData("## second", 2)]
        [InlineData("### third", 3)]
        [InlineData("#### fourth", 0)]
        [InlineData("#missing-space", 0)]
        public void Headings_require_one_to_three_hashes_followed_by_space(string body, int level)
        {
            var block = Assert.Single(MarkdownLiteParser.Parse(body).Blocks);
            Assert.Equal(level == 0 ? MarkdownBlockKind.Paragraph : MarkdownBlockKind.Heading, block.Kind);
            Assert.Equal(level, block.Level);
            Assert.Equal(level == 0 ? body : body.Substring(level + 1), MarkdownLiteParser.Parse(body).ToPlainText());
        }

        [Fact]
        public void Paragraph_preserves_single_line_breaks_and_normalizes_CRLF_and_CR()
        {
            var result = MarkdownLiteParser.Parse("one\r\ntwo\rthree\n\nfour");
            Assert.Equal("one\ntwo\nthree\n\nfour", result.Body);
            Assert.Equal(2, result.Blocks.Count);
            Assert.All(result.Blocks, block => Assert.Equal(MarkdownBlockKind.Paragraph, block.Kind));
            Assert.Equal("one\ntwo\nthree\n\nfour", result.ToPlainText());
        }
    }
}
