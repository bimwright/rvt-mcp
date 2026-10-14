using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using RvtMcp.Failures;
using Xunit;

namespace RvtMcp.Tests
{
    public class UserFailureAggregationTests
    {
        private static JObject Failure(string code) => new JObject {
            ["kind"] = "failure", ["code"] = code, ["severity"] = "Warning", ["text"] = "Sample " + code
        };

        [Fact]
        public void DifferentWarningAfterHundredsOfDuplicatesIsRetainedWithExactCounts()
        {
            var buffer = new UserDialogBuffer();
            var failures = Enumerable.Repeat("a", 150).Append("b").ToList();
            buffer.AddFailures("title:sample", "sample", failures, code => code, (_, code) => Failure(code), DateTimeOffset.UtcNow);
            var report = buffer.Take("title:sample");
            Assert.Equal(2, report["items"].Count());
            Assert.Equal(150, report["items"][0].Value<int>("count"));
            Assert.Equal("b", report["items"][1].Value<string>("code"));
            Assert.Null(report["omittedItems"]);
        }

        [Fact]
        public void ExpensiveMetadataIsReadOncePerFailureDefinition()
        {
            var buffer = new UserDialogBuffer();
            int reads = 0;
            var failures = Enumerable.Repeat("a", 150).Concat(Enumerable.Repeat("b", 100)).ToList();
            buffer.AddFailures("title:sample", "sample", failures, code => code, (_, code) => { reads++; return Failure(code); }, DateTimeOffset.UtcNow);
            Assert.Equal(2, reads);
        }

        [Fact]
        public void SameDefinitionWithVariableTextUsesOneRepresentativeSample()
        {
            var buffer = new UserDialogBuffer();
            int reads = 0;
            buffer.AddFailures("title:sample", "sample", Enumerable.Repeat("a", 150).ToList(), code => code, (_, code) => {
                var item = Failure(code); item["text"] = "Element " + (++reads); return item;
            }, DateTimeOffset.UtcNow);
            var report = buffer.Take("title:sample");
            Assert.Single(report["items"]);
            Assert.Equal(150, report["items"][0].Value<int>("count"));
            Assert.Equal(1, reads);
            Assert.True(report["items"][0].Value<bool>("textIsSample"));
            Assert.Equal("exact", report["items"][0].Value<string>("countKind"));
        }

        [Fact]
        public void IdCapMarksUnreadFailuresAndRetainedCountsAsLowerBounds()
        {
            var buffer = new UserDialogBuffer(); int ids = 0, metadata = 0;
            var failures = Enumerable.Repeat("a", UserDialogBuffer.MaxFailuresRead + 1).Append("b").ToList();
            buffer.AddFailures("title:sample", "sample", failures, code => { ids++; return code; },
                (_, code) => { metadata++; return Failure(code); }, DateTimeOffset.UtcNow);
            var report = buffer.Take("title:sample");
            Assert.Equal(UserDialogBuffer.MaxFailuresRead, ids); Assert.Equal(1, metadata);
            Assert.Equal(2, report.Value<int>("unreadFailures")); Assert.Equal(2, report.Value<int>("omittedItems"));
            Assert.Equal("at_least", report["items"][0].Value<string>("countKind"));
            Assert.Equal(UserDialogBuffer.MaxFailuresRead, report["items"][0].Value<int>("count"));
        }

        [Fact]
        public void OmittedDefinitionsKeepCodesAndWeightedCountsWithoutReadingText()
        {
            var buffer = new UserDialogBuffer(); int metadata = 0;
            var failures = Enumerable.Range(0, 25).SelectMany(i => Enumerable.Repeat("c" + i, i + 1)).ToList();
            buffer.AddFailures("title:sample", "sample", failures, code => code,
                (_, code) => { metadata++; return Failure(code); }, DateTimeOffset.UtcNow);
            var report = buffer.Take("title:sample");
            Assert.Equal(UserDialogBuffer.MaxItems, metadata);
            Assert.Equal(115, report.Value<int>("omittedItems"));
            Assert.Equal(5, report["omittedFailures"].Count());
            Assert.Equal("c20", report["omittedFailures"][0].Value<string>("code"));
            Assert.Equal(21, report["omittedFailures"][0].Value<int>("count"));
            Assert.True(Encoding.UTF8.GetByteCount(report.ToString(Newtonsoft.Json.Formatting.None)) <= UserDialogBuffer.MaxReportBytes);
        }

        [Fact]
        public void UnreadableIdDoesNotHideOtherDefinitions()
        {
            var buffer = new UserDialogBuffer();
            buffer.AddFailures("title:sample", "sample", new[] { "a", "bad", "b" },
                code => code == "bad" ? throw new InvalidOperationException() : code, (_, code) => Failure(code), DateTimeOffset.UtcNow);
            var report = buffer.Take("title:sample");
            Assert.Equal(2, report["items"].Count()); Assert.Equal(1, report.Value<int>("unreadFailures"));
            Assert.All(report["items"], item => Assert.Equal("at_least", item.Value<string>("countKind")));
        }

        [Fact]
        public void FailedMetadataReadReportsItsDefinitionCountAndKeepsOtherWarnings()
        {
            var buffer = new UserDialogBuffer();
            buffer.AddFailures("title:sample", "sample", Enumerable.Repeat("a", 150).Append("b").ToList(), code => code,
                (_, code) => code == "a" ? throw new InvalidOperationException() : Failure(code), DateTimeOffset.UtcNow);
            var report = buffer.Take("title:sample");
            Assert.Single(report["items"]); Assert.Equal("b", report["items"][0].Value<string>("code"));
            Assert.Equal(150, report.Value<int>("omittedItems"));
            Assert.Equal("a", report["omittedFailures"][0].Value<string>("code"));
            Assert.Equal(150, report["omittedFailures"][0].Value<int>("count"));
        }

        [Fact]
        public void OmissionDetailsAndPagedReportsStayBounded()
        {
            var buffer = new UserDialogBuffer();
            var failures = Enumerable.Range(0, 50).Select(i => "failure_" + i).ToList();
            buffer.AddFailures("title:sample", "sample", failures, code => code, (_, code) => {
                var item = Failure(code); item["text"] = new string('x', 320); return item;
            }, DateTimeOffset.UtcNow);
            var first = buffer.Take("title:sample");
            Assert.Equal(30, first.Value<int>("omittedItems"));
            Assert.Equal(UserDialogBuffer.MaxItems, first["omittedFailures"].Count());
            Assert.True(first.Value<bool>("omittedFailureDetailsTruncated"));
            int count = 0;
            for (var page = first; page != null; page = buffer.Take("title:sample"))
            {
                Assert.True(Encoding.UTF8.GetByteCount(page.ToString(Newtonsoft.Json.Formatting.None)) <= UserDialogBuffer.MaxReportBytes);
                count += page["items"].Count();
            }
            Assert.Equal(UserDialogBuffer.MaxItems, count);
        }

        [Fact]
        public void RekeyIntoFullBufferPreservesOmittedOccurrenceCounts()
        {
            var buffer = new UserDialogBuffer(); var now = DateTimeOffset.UtcNow;
            for (int i = 0; i < UserDialogBuffer.MaxItems; i++)
                buffer.Add("title:target", "target", new JObject { ["kind"] = "Dialog", ["code"] = "d" + i }, now);
            buffer.AddFailures("title:source", "source", Enumerable.Repeat("a", 150).ToList(), code => code, (_, code) => Failure(code), now);
            buffer.Rekey("title:source", "title:target", "target");
            var report = buffer.Take("title:target");
            Assert.Equal(150, report.Value<int>("omittedItems"));
            Assert.Equal(150, report["omittedFailures"][0].Value<int>("count"));
        }
    }
}
