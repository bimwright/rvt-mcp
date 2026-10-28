using System.Linq;
using System.Text;
using RvtMcp.Plugin.Reports.Model;
using Xunit;

namespace RvtMcp.Tests.Shared.Reports
{
    public sealed class ReportLimitsTests
    {
        [Fact]
        public void Required_text_accepts_its_upper_bound_and_rejects_the_next_character()
        {
            Assert.Null(ReportLimits.Validate(new string('t', 80), "completed", new string('s', 120), null));
            Assert.NotNull(ReportLimits.Validate(new string('t', 81), "completed", "summary", null));
            Assert.NotNull(ReportLimits.Validate("task", "completed", new string('s', 121), null));
        }

        [Theory]
        [InlineData("completed", true)]
        [InlineData("failed", true)]
        [InlineData("cancelled", true)]
        [InlineData("Completed", false)]
        [InlineData("unknown", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Outcome_is_one_of_the_three_exact_wire_values(string outcome, bool valid)
        {
            Assert.Equal(valid, ReportLimits.Validate("task", outcome, "summary", null) == null);
        }

        [Theory]
        [InlineData("one\ntwo")]
        [InlineData("one\rtwo")]
        [InlineData("one\r\ntwo")]
        [InlineData("one\u0085two")]
        [InlineData("one\u2028two")]
        [InlineData("one\u2029two")]
        public void Summary_rejects_line_breaks(string summary)
        {
            Assert.NotNull(ReportLimits.Validate("task", "completed", summary, null));
        }

        [Theory]
        [InlineData(null, "summary")]
        [InlineData("", "summary")]
        [InlineData("task", null)]
        [InlineData("task", "")]
        public void Required_text_rejects_null_and_empty(string taskId, string summary)
        {
            Assert.NotNull(ReportLimits.Validate(taskId, "completed", summary, null));
        }

        [Fact]
        public void Minimum_lengths_and_optional_body_are_accepted_without_trimming()
        {
            Assert.Null(ReportLimits.Validate("t", "failed", "s", ""));
            Assert.Null(ReportLimits.Validate(" ", "cancelled", " ", null));
        }

        [Fact]
        public void Body_budget_is_32768_utf8_bytes_including_multibyte_vietnamese()
        {
            var exact = string.Concat(Enumerable.Repeat("ắ", 10922)) + "ab";
            Assert.Equal(32768, Encoding.UTF8.GetByteCount(exact));
            Assert.Null(ReportLimits.Validate("task", "completed", "summary", exact));
            Assert.NotNull(ReportLimits.Validate("task", "completed", "summary", exact + "a"));
        }

        [Fact]
        public void Body_budget_is_measured_before_line_ending_normalization()
        {
            var original = new string('a', 32767) + "\r\n";
            Assert.NotNull(ReportLimits.Validate("task", "completed", "summary", original));
            Assert.Null(ReportLimits.Validate("task", "completed", "summary", original.Replace("\r\n", "\n")));
        }
    }
}
