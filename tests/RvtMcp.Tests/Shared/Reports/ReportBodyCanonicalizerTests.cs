using System;
using System.Collections.Generic;
using RvtMcp.Plugin.Reports.Model;
using Xunit;

namespace RvtMcp.Tests.Shared.Reports
{
    public sealed class ReportBodyCanonicalizerTests
    {
        [Fact]
        public void Resolved_link_rewrites_only_its_target_to_the_stored_index()
        {
            var links = new[]
            {
                new MarkdownLinkOccurrence { TargetStart = 4, TargetLength = 8 }
            };

            var body = ReportBodyCanonicalizer.Canonicalize("[a](revit:12)", links,
                new Dictionary<int, int> { [0] = 3 });

            Assert.Equal("[a](revit:#3)", body);
        }

        [Fact]
        public void Unresolved_and_rejected_links_use_inert_targets()
        {
            const string body = "[a](revit:12)[b](revit:34)[bad](revit:0)[late](revit:56)";
            var links = new[]
            {
                Occurrence(body, "revit:12"), Occurrence(body, "revit:34"),
                Occurrence(body, "revit:0", "bad_syntax"),
                Occurrence(body, "revit:56", "limit_exceeded")
            };

            var actual = ReportBodyCanonicalizer.Canonicalize(body, links,
                new Dictionary<int, int> { [1] = 0, [2] = 1, [3] = 2 });

            Assert.Equal("[a](revit-12)[b](revit:#0)[bad](revit-0)[late](revit-56)", actual);
        }

        [Fact]
        public void Unresolved_targets_preserve_every_character_after_the_scheme()
        {
            const string target = "ReViT:01?view=0&doc=BAD]([|*\\`#0";
            const string body = "before [x](ReViT:01?view=0&doc=BAD]([|*\\`#0) after";

            var actual = ReportBodyCanonicalizer.Canonicalize(body,
                new[] { Occurrence(body, target, "bad_syntax") }, null);

            Assert.Equal("before [x](revit-01?view=0&doc=BAD]([|*\\`#0) after", actual);
        }

        [Fact]
        public void Raw_stored_index_cannot_become_live_after_another_link_resolves()
        {
            var raw = MarkdownLiteParser.Parse("[A](revit:5) [B](revit:#0)");
            Assert.Equal("bad_syntax", raw.Links[1].Reason);

            var body = ReportBodyCanonicalizer.Canonicalize(raw.Body, raw.Links,
                new Dictionary<int, int> { [0] = 0 });

            Assert.Equal("[A](revit:#0) [B](revit-#0)", body);
            var stored = MarkdownLiteParser.Parse(body, ReportParseMode.Stored, 1);
            Assert.Equal("A", Assert.Single(stored.Links).Label);
            Assert.Equal("A B", stored.ToPlainText());
            Assert.Single(Assert.Single(stored.Blocks).Inlines.FindAll(node => node.Kind == MarkdownInlineKind.Link));
        }

        [Fact]
        public void Rebalanced_label_cannot_activate_an_agent_supplied_stored_index()
        {
            var raw = MarkdownLiteParser.Parse("[A](revit:5) [Q [X](revit:])](revit:#0)");
            Assert.Equal(2, raw.Links.Count);
            Assert.Equal("X", raw.Links[1].Label);

            var body = ReportBodyCanonicalizer.Canonicalize(raw.Body, raw.Links,
                new Dictionary<int, int> { [0] = 0 });

            var stored = MarkdownLiteParser.Parse(body, ReportParseMode.Stored, 1);
            Assert.Equal("A", Assert.Single(stored.Links).Label);
            Assert.Equal("[A](revit:#0) [Q [X](revit-])](revit:#0)", body);
        }

        [Fact]
        public void Canonicalization_preserves_code_bare_targets_and_plain_link_labels()
        {
            const string prefix = "revit:#0\n\n`[span](revit:#0)`\n\n```\n[fence](revit:#0)\n```\n\n";
            var raw = MarkdownLiteParser.Parse(prefix + "[L [nested](revit:#0)](revit:5)");
            Assert.Single(raw.Links);

            var body = ReportBodyCanonicalizer.Canonicalize(raw.Body, raw.Links,
                new Dictionary<int, int> { [0] = 0 });

            Assert.Equal(prefix + "[L [nested](revit:#0)](revit:#0)", body);
            var stored = MarkdownLiteParser.Parse(body, ReportParseMode.Stored, 1);
            Assert.Equal("L [nested](revit:#0)", Assert.Single(stored.Links).Label);
        }

        [Fact]
        public void Missing_resolution_map_neutralizes_all_raw_occurrences()
        {
            var raw = MarkdownLiteParser.Parse("[A](revit:5) [B](revit:#0)");

            var body = ReportBodyCanonicalizer.Canonicalize(raw.Body, raw.Links, null);

            Assert.Equal("[A](revit-5) [B](revit-#0)", body);
            var stored = MarkdownLiteParser.Parse(body, ReportParseMode.Stored, 1);
            Assert.Empty(stored.Links);
            Assert.Equal("A B", stored.ToPlainText());
        }

        [Fact]
        public void Adjacent_links_at_end_use_original_UTF16_offsets_after_multibyte_text()
        {
            const string body = "Tiếng Việt 😀\n[a](revit:123456789)[b](revit:2)";
            var links = new[] { Occurrence(body, "revit:123456789"), Occurrence(body, "revit:2") };

            var actual = ReportBodyCanonicalizer.Canonicalize(body, links,
                new Dictionary<int, int> { [0] = 12, [1] = 0 });

            Assert.Equal("Tiếng Việt 😀\n[a](revit:#12)[b](revit:#0)", actual);
        }

        [Fact]
        public void CRLF_and_CR_input_uses_parser_normalized_body_and_link_positions()
        {
            var parsed = MarkdownLiteParser.Parse("mở 😀\r\n[x](revit:10)\r[y](revit:20)");
            Assert.Equal(2, parsed.Links.Count);

            var actual = ReportBodyCanonicalizer.Canonicalize(parsed.Body, parsed.Links,
                new Dictionary<int, int> { [0] = 0, [1] = 1 });

            Assert.Equal("mở 😀\n[x](revit:#0)\n[y](revit:#1)", actual);
        }

        private static MarkdownLinkOccurrence Occurrence(string body, string target, string reason = null)
            => new MarkdownLinkOccurrence
            {
                Target = target, TargetStart = body.IndexOf(target, StringComparison.Ordinal),
                TargetLength = target.Length, Reason = reason
            };
    }
}
