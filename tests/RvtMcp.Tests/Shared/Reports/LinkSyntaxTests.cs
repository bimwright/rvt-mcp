using RvtMcp.Plugin.Reports.Model;
using Xunit;

namespace RvtMcp.Tests.Shared.Reports
{
    public sealed class LinkSyntaxTests
    {
        [Fact]
        public void Raw_target_accepts_one_positive_id()
        {
            var target = LinkSyntax.Parse("revit:123");

            Assert.True(target.IsValid);
            Assert.Null(target.Reason);
            Assert.Equal(new long[] { 123 }, target.Ids);
            Assert.Null(target.ViewId);
            Assert.Null(target.DocumentToken);
            Assert.Null(target.StoredIndex);
        }

        [Theory]
        [InlineData("revit:0")]
        [InlineData("revit:01")]
        [InlineData("revit:9223372036854775808")]
        [InlineData("revit:+1")]
        public void Raw_ids_must_be_positive_long_without_leading_zero(string text)
        {
            var target = LinkSyntax.Parse(text);

            Assert.False(target.IsValid);
            Assert.Equal("bad_syntax", target.Reason);
        }

        [Theory]
        [InlineData("REVIT:7,3,7,9223372036854775807?view=9&doc=ABCDEF0123456789ABCDEF0123456789")]
        [InlineData("revit:7,3,7,9223372036854775807?doc=ABCDEF0123456789ABCDEF0123456789&view=9")]
        public void Raw_target_keeps_distinct_id_order_and_normalizes_document_token(string text)
        {
            var target = LinkSyntax.Parse(text);

            Assert.True(target.IsValid);
            Assert.Equal(new long[] { 7, 3, 9223372036854775807 }, target.Ids);
            Assert.Equal(9, target.ViewId);
            Assert.Equal("abcdef0123456789abcdef0123456789", target.DocumentToken);
        }

        [Theory]
        [InlineData("revit:#0", 0)]
        [InlineData("REVIT:#2", 2)]
        [InlineData("revit:#002", 2)]
        public void Stored_target_accepts_only_an_existing_link_index(string text, int expected)
        {
            var target = LinkSyntax.Parse(text, ReportParseMode.Stored, 3);

            Assert.True(target.IsValid);
            Assert.Equal(expected, target.StoredIndex);
            Assert.Empty(target.Ids);
        }

        [Theory]
        [InlineData("revit:1,2")]
        [InlineData("revit:1?view=9")]
        [InlineData("revit:1?doc=abcdef0123456789abcdef0123456789")]
        public void Raw_optional_parts_are_independently_valid(string text)
        {
            Assert.True(LinkSyntax.Parse(text).IsValid);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("revit:")]
        [InlineData("https:1")]
        [InlineData("revit: 1")]
        [InlineData("revit:1 ")]
        [InlineData("revit:1\t")]
        [InlineData("revit:1\n")]
        [InlineData("revit:1, 2")]
        [InlineData("revit:1,,2")]
        [InlineData("revit:1,")]
        [InlineData("revit:-1")]
        [InlineData("revit:１")]
        [InlineData("revit:1?view=0")]
        [InlineData("revit:1?view=01")]
        [InlineData("revit:1?view=9223372036854775808")]
        [InlineData("revit:1?view=2&view=3")]
        [InlineData("revit:1?doc=abcdef0123456789abcdef0123456789&doc=abcdef0123456789abcdef0123456789")]
        [InlineData("revit:1?other=2")]
        [InlineData("revit:1?View=2")]
        [InlineData("revit:1?doc=abcdef0123456789abcdef012345678")]
        [InlineData("revit:1?doc=abcdef0123456789abcdef01234567890")]
        [InlineData("revit:1?doc=gbcdef0123456789abcdef0123456789")]
        [InlineData("revit:1?doc=abcdef0123456789abcdef012345678 ")]
        [InlineData("revit:1?")]
        [InlineData("revit:1?view=2&")]
        [InlineData("revit:1?&view=2")]
        [InlineData("revit:1?view=2?doc=abcdef0123456789abcdef0123456789")]
        [InlineData("revit:#0")]
        public void Invalid_raw_grammar_returns_bad_syntax_without_throwing(string text)
        {
            var target = LinkSyntax.Parse(text);

            Assert.False(target.IsValid);
            Assert.Equal("bad_syntax", target.Reason);
        }

        [Theory]
        [InlineData("revit:123", 3)]
        [InlineData("revit:#0", 0)]
        [InlineData("revit:#3", 3)]
        [InlineData("revit:#-1", 3)]
        [InlineData("revit:#+1", 3)]
        [InlineData("revit:#", 3)]
        [InlineData("revit:# 1", 3)]
        [InlineData("revit:#1?view=2", 3)]
        [InlineData("revit:#2147483648", 3)]
        public void Stored_mode_rejects_raw_targets_and_missing_or_malformed_indices(string text, int count)
        {
            var target = LinkSyntax.Parse(text, ReportParseMode.Stored, count);

            Assert.False(target.IsValid);
            Assert.Equal("bad_syntax", target.Reason);
        }
    }
}
