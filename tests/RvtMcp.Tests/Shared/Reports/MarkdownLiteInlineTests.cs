using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using RvtMcp.Plugin.Reports.Model;
using Xunit;
using Xunit.Abstractions;

namespace RvtMcp.Tests.Shared.Reports
{
    public sealed class MarkdownLiteInlineTests
    {
        private readonly ITestOutputHelper _output;

        public MarkdownLiteInlineTests(ITestOutputHelper output) { _output = output; }

        [Theory]
        [InlineData("Kích thước 600 * 600 * 50 mm")]
        [InlineData("600 ** 600 ** 50")]
        [InlineData("v * mở*")]
        [InlineData("*mở *")]
        [InlineData("** mở**")]
        [InlineData("**mở **")]
        [InlineData("a* b *c")]
        [InlineData("*a *b")]
        [InlineData("*** x ***")]
        [InlineData("*\ttext*")]
        [InlineData("*text\t*")]
        [InlineData("*\ntext*")]
        [InlineData("*\u00a0text*")]
        [InlineData("*text\u00a0*")]
        public void Emphasis_markers_require_non_whitespace_flanks(string body)
        {
            var parsed = MarkdownLiteParser.Parse(body);
            Assert.Equal(body, parsed.ToPlainText());
            Assert.DoesNotContain(Assert.Single(parsed.Blocks).Inlines,
                node => node.Kind == MarkdownInlineKind.Strong || node.Kind == MarkdownInlineKind.Emphasis);
        }

        [Fact]
        public void Valid_bold_and_italic_keep_their_formatting()
        {
            var parsed = MarkdownLiteParser.Parse("**đậm** và *nghiêng*");
            Assert.Equal("đậm và nghiêng", parsed.ToPlainText());
            Assert.Equal(new[] { MarkdownInlineKind.Strong, MarkdownInlineKind.Text, MarkdownInlineKind.Emphasis },
                parsed.Blocks[0].Inlines.Select(node => node.Kind));
        }

        [Theory]
        [InlineData("tạm", "\n", ReportParseMode.Raw)]
        [InlineData("revit:5", "\r\n", ReportParseMode.Raw)]
        [InlineData("https://example.invalid", "\r", ReportParseMode.Raw)]
        [InlineData("tạm", "\r\n", ReportParseMode.Stored)]
        [InlineData("revit:#0", "\r", ReportParseMode.Stored)]
        [InlineData("https://example.invalid", "\n", ReportParseMode.Stored)]
        public void Link_target_cannot_consume_text_past_a_line_break(string target, string lineBreak, ReportParseMode mode)
        {
            var body = "Xem [ghi chú](" + target + lineBreak + "Dòng hai vẫn còn) cuối";
            var parsed = MarkdownLiteParser.Parse(body, mode, 1);
            Assert.Equal("Xem [ghi chú](" + target + "\nDòng hai vẫn còn) cuối", parsed.ToPlainText());
            Assert.Empty(parsed.Links);
        }

        [Fact]
        public void Incomplete_target_on_previous_line_does_not_hide_a_later_valid_link()
        {
            var parsed = MarkdownLiteParser.Parse("Xem [ghi chú](tạm\nDòng hai vẫn còn) [A](revit:5)");
            Assert.Equal("Xem [ghi chú](tạm\nDòng hai vẫn còn) A", parsed.ToPlainText());
            Assert.Equal("A", Assert.Single(parsed.Links).Label);
        }

        [Theory]
        [InlineData("brackets")]
        [InlineData("stars")]
        [InlineData("backticks")]
        [InlineData("inline-backticks")]
        [InlineData("pipes")]
        [InlineData("backslashes")]
        [InlineData("balanced-nested-label")]
        [InlineData("incomplete-nested-label")]
        [InlineData("alternating-emphasis")]
        public void Adversarial_32_KiB_inputs_parse_and_copy_without_throwing_under_200_ms(string shape)
        {
            string body;
            switch (shape)
            {
                case "brackets": body = new string('[', 32768); break;
                case "stars": body = new string('*', 32768); break;
                case "backticks": body = new string('`', 32768); break;
                case "inline-backticks": body = "x" + new string('`', 32767); break;
                case "pipes": body = new string('|', 32768); break;
                case "backslashes": body = new string('\\', 32768); break;
                case "balanced-nested-label": body = new string('[', 16379) + new string(']', 16379) + "(revit:1)x"; break;
                case "incomplete-nested-label": body = string.Concat(Enumerable.Repeat("[[x", 10922)) + "[["; break;
                default: body = string.Concat(Enumerable.Repeat("**a*", 8192)); break;
            }
            Assert.Equal(32768, Encoding.UTF8.GetByteCount(body));
            MarkdownLiteParser.Parse("**warm** [x](revit:1)").ToPlainText();
            var timer = Stopwatch.StartNew();
            var parsed = MarkdownLiteParser.Parse(body);
            var plain = parsed.ToPlainText();
            timer.Stop();
            _output.WriteLine("{0}: {1:F3} ms (parse + copy)", shape, timer.Elapsed.TotalMilliseconds);
            Assert.NotNull(plain);
            Assert.True(timer.Elapsed.TotalMilliseconds < 1000, shape + " took " + timer.Elapsed.TotalMilliseconds + " ms (limit 1000 ms)");
        }

        [Fact]
        public void Every_supported_escape_character_is_literal_outside_code()
        {
            var parsed = MarkdownLiteParser.Parse(@"\` \_ \( \) \| \# \-");
            Assert.Equal("` _ ( ) | # -", parsed.ToPlainText());
            Assert.All(parsed.Blocks[0].Inlines, node => Assert.Equal(MarkdownInlineKind.Text, node.Kind));
        }

        [Fact]
        public void Link_budget_is_shared_across_blocks_and_oversize_valid_links_spend_the_link_ordinal()
        {
            var prefix = string.Concat(Enumerable.Repeat("[x](revit:1) ", 299));
            var oversized = "[large](revit:" + string.Join(",", Enumerable.Range(1, 501)) + ")";
            var links = MarkdownLiteParser.Parse(prefix + "\n\n" + oversized + "\n\n# [last](revit:2)").Links;
            Assert.Equal(301, links.Count);
            Assert.All(links.Take(299), link => Assert.Null(link.Reason));
            Assert.Equal("limit_exceeded", links[299].Reason);
            Assert.Equal("limit_exceeded", links[300].Reason);
        }

        [Theory]
        [InlineData("[](revit:1)")]
        [InlineData("[ \t](revit:1)")]
        [InlineData("[x](revit:0)")]
        [InlineData("[x](revit:1 2)")]
        [InlineData("[x](revit:#0)")]
        public void Complete_invalid_raw_links_are_counted_with_bad_syntax(string body)
        {
            var link = Assert.Single(MarkdownLiteParser.Parse(body).Links);
            Assert.Equal("bad_syntax", link.Reason);
        }

        [Theory]
        [InlineData("[x](revit:1")]
        [InlineData("[x(revit:1)")]
        [InlineData("[x\ny](revit:1)")]
        [InlineData(@"\[x](revit:1)")]
        [InlineData("revit:1")]
        [InlineData("![x](revit:1)")]
        public void Incomplete_escaped_bare_and_image_markup_is_not_a_link(string body) =>
            Assert.Empty(MarkdownLiteParser.Parse(body).Links);

        [Fact]
        public void Complete_inner_link_is_recognized_after_an_unmatched_outer_bracket()
        {
            var parsed = MarkdownLiteParser.Parse("[a[b](revit:1)");
            Assert.Equal("b", Assert.Single(parsed.Links).Label);
            Assert.Equal("[ab", parsed.ToPlainText());
        }

        [Fact]
        public void Stored_mode_links_only_existing_indices_and_renders_other_targets_as_labels()
        {
            var parsed = MarkdownLiteParser.Parse("[live](revit:#0) [raw](revit:1) [outside](revit:#2) [bad](revit:#x)", ReportParseMode.Stored, 1);
            Assert.Equal(0, Assert.Single(parsed.Links).Syntax.StoredIndex);
            Assert.Equal("live raw outside bad", parsed.ToPlainText());
            Assert.Single(parsed.Blocks[0].Inlines.Where(node => node.Kind == MarkdownInlineKind.Link));
        }

        [Fact]
        public void Valid_link_budget_follows_body_order_and_bad_syntax_does_not_spend_it()
        {
            var body = string.Concat(Enumerable.Repeat("[](revit:1) ", 300)) +
                string.Concat(Enumerable.Range(1, 301).Select(id => "[ok](revit:" + id + ") "));
            var links = MarkdownLiteParser.Parse(body).Links;
            Assert.Equal(601, links.Count);
            Assert.All(links.Take(300), link => Assert.Equal("bad_syntax", link.Reason));
            Assert.All(links.Skip(300).Take(300), link => Assert.Null(link.Reason));
            Assert.Equal("limit_exceeded", links[600].Reason);
        }

        [Fact]
        public void Id_budgets_count_deduplicated_ids_and_only_accepted_link_totals()
        {
            string Link(int ids) => "[x](revit:" + string.Join(",", Enumerable.Range(1, ids)) + ") ";
            var body = Link(501) + Link(500) + Link(500) + Link(500) + Link(400) + Link(200) + Link(100) + Link(1);
            var links = MarkdownLiteParser.Parse(body).Links;
            Assert.Equal(new[] { "limit_exceeded", null, null, null, null, "limit_exceeded", null, "limit_exceeded" },
                links.Select(link => link.Reason));
            var duplicate = Assert.Single(MarkdownLiteParser.Parse("[x](revit:" + string.Join(",", Enumerable.Repeat("1", 1000)) + ")").Links);
            Assert.Null(duplicate.Reason);
            Assert.Equal(new long[] { 1 }, duplicate.Syntax.Ids);
        }

        [Fact]
        public void Links_have_plain_balanced_labels_LF_positions_and_can_be_nested_in_emphasis()
        {
            var body = @"**[a[b] \] *plain*](ReViT:7,7,9)** [web](https://example.invalid) revit:1";
            var parsed = MarkdownLiteParser.Parse(body);
            var link = Assert.Single(parsed.Links);
            Assert.Equal(@"a[b] ] *plain*", link.Label);
            Assert.Equal("ReViT:7,7,9", link.Target);
            Assert.Null(link.Reason);
            Assert.Equal(new long[] { 7, 9 }, link.Syntax.Ids);
            Assert.Equal(2, link.Start);
            Assert.Equal(body.IndexOf("ReViT:", StringComparison.Ordinal), link.TargetStart);
            Assert.Equal(link.Target, parsed.Body.Substring(link.TargetStart, link.TargetLength));
            Assert.Equal(MarkdownInlineKind.Link, parsed.Blocks[0].Inlines[0].Children[0].Kind);
            Assert.Equal(@"a[b] ] *plain* web revit:1", parsed.ToPlainText());
        }

        [Theory]
        [InlineData("***both***", "both", MarkdownInlineKind.Strong, MarkdownInlineKind.Emphasis)]
        [InlineData("***x***", "x", MarkdownInlineKind.Strong, MarkdownInlineKind.Emphasis)]
        [InlineData("**a *b* c**", "a b c", MarkdownInlineKind.Strong, MarkdownInlineKind.Emphasis)]
        [InlineData("*a **b** c*", "a b c", MarkdownInlineKind.Emphasis, MarkdownInlineKind.Strong)]
        [InlineData("**a `b` c**", "a b c", MarkdownInlineKind.Strong, MarkdownInlineKind.Code)]
        public void Emphasis_nests_and_contains_code(string body, string plain, MarkdownInlineKind outer, MarkdownInlineKind inner)
        {
            var parsed = MarkdownLiteParser.Parse(body);
            var node = Assert.Single(Assert.Single(parsed.Blocks).Inlines);
            Assert.Equal(outer, node.Kind);
            Assert.Contains(node.Children, child => child.Kind == inner);
            Assert.Equal(plain, parsed.ToPlainText());
        }

        [Theory]
        [InlineData("**2*3")]
        [InlineData("word **open")]
        [InlineData("2*3")]
        public void Unmatched_emphasis_markers_remain_literal(string body) =>
            Assert.Equal(body, MarkdownLiteParser.Parse(body).ToPlainText());

        [Fact]
        public void Code_span_is_plain_and_unmatched_backtick_remains_literal()
        {
            var parsed = MarkdownLiteParser.Parse(@"`*raw* \[x](revit:1)` and `open");
            Assert.Equal(@"*raw* \[x](revit:1) and `open", parsed.ToPlainText());
            Assert.Equal(MarkdownInlineKind.Code, parsed.Blocks[0].Inlines[0].Kind);
            Assert.Empty(parsed.Links);
        }

        [Fact]
        public void Escapes_decode_only_supported_characters_and_underscores_are_literal()
        {
            var parsed = MarkdownLiteParser.Parse(@"M_Pipe_Fitting 2*3 \*lit\* \q \\ \[x\]");
            Assert.Equal(@"M_Pipe_Fitting 2*3 *lit* \q \ [x]", parsed.ToPlainText());
            Assert.Empty(parsed.Links);
            Assert.All(Assert.Single(parsed.Blocks).Inlines, node => Assert.Equal(MarkdownInlineKind.Text, node.Kind));
        }
    }
}
