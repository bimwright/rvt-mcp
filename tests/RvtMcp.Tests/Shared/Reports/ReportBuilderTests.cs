using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Reports.Binding;
using RvtMcp.Plugin.Reports.Changes;
using RvtMcp.Plugin.Reports.Model;
using RvtMcp.Plugin.Reports.Storage;
using Xunit;

namespace RvtMcp.Tests.Shared.Reports
{
    public class ReportBuilderTests
    {
        private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string C = "cccccccccccccccccccccccccccccccc";
        private const string Missing = "dddddddddddddddddddddddddddddddd";

        private sealed class FakeResolver : IReportResolver
        {
            public ReportDocument ActiveDocument { get; set; }
            public Dictionary<string, ReportDocument> Documents { get; } = new Dictionary<string, ReportDocument>();
            public Dictionary<string, Dictionary<long, ReportElement>> Elements { get; } = new Dictionary<string, Dictionary<long, ReportElement>>();
            public Dictionary<string, Dictionary<long, ReportView>> Views { get; } = new Dictionary<string, Dictionary<long, ReportView>>();

            public ReportDocument FindDocument(string token) => Documents.TryGetValue(token, out var document) ? document : null;
            public ReportElement FindElement(string token, long id) => Elements.TryGetValue(token, out var elements) && elements.TryGetValue(id, out var element) ? element : null;
            public ReportView FindView(string token, long id) => Views.TryGetValue(token, out var views) && views.TryGetValue(id, out var view) ? view : null;
        }

        private static ReportDocument Document(string token) => new ReportDocument { Token = token, Title = token, Kind = ReportDocumentKinds.Unsaved };
        private static ReportElement Element(long id) => new ReportElement { Id = id, UniqueId = "uid-" + id, Kind = ReportElementKinds.Element, Name = "Item " + id };
        private static FakeResolver Resolver()
        {
            var resolver = new FakeResolver { ActiveDocument = Document(A) };
            resolver.Documents.Add(A, resolver.ActiveDocument);
            resolver.Documents.Add(B, Document(B));
            resolver.Elements.Add(A, new Dictionary<long, ReportElement> { [1] = Element(1), [2] = Element(2) });
            resolver.Elements.Add(B, new Dictionary<long, ReportElement> { [3] = Element(3) });
            resolver.Views.Add(A, new Dictionary<long, ReportView> { [9] = new ReportView { Id = 9, UniqueId = "view-9" } });
            return resolver;
        }

        private static ReportBuildResult Build(string body, FakeResolver resolver = null, ReportChangeSet changes = null, string summary = "Summary") =>
            ReportBuilder.Build("task", "completed", summary, body, resolver ?? Resolver(), changes, new ReportSource { TargetId = "synthetic" });

        [Fact]
        public void Reports_all_reasons_and_counts_every_complete_occurrence()
        {
            var resolver = Resolver();
            resolver.ActiveDocument = null;
            var longLink = string.Join(",", Enumerable.Range(1, 501));
            var body = "[bad](revit:#0) [limit](revit:" + longLink + ") [none](revit:1) [closed](revit:1?doc=" + Missing + ") [missing](revit:99?doc=" + A + ")";

            var result = Build(body, resolver);

            Assert.Equal(5, result.LinkCheck.Total);
            Assert.Equal(0, result.LinkCheck.Resolved);
            Assert.Equal(new[] { "bad_syntax", "limit_exceeded", "no_document", "document_not_open", "element_not_found" }, result.LinkCheck.Unresolved.Select(x => x.Reason));
            Assert.All(result.LinkCheck.Unresolved, item => Assert.Null(item.Ids));
            Assert.Contains("[bad](revit-#0)", result.Report.Body);
            Assert.Contains("[none](revit-1)", result.Report.Body);
            Assert.Empty(result.Report.Links);
        }

        [Fact]
        public void Partial_link_counts_as_resolved_reports_missing_ids_and_preserves_order()
        {
            var body = "[first](revit:1) [partial](revit:2,88,99) [bad](revit:0) [last](revit:3?doc=" + B + ")";

            var result = Build(body);

            Assert.Equal(4, result.LinkCheck.Total);
            Assert.Equal(3, result.LinkCheck.Resolved);
            Assert.Equal(new long[] { 88, 99 }, result.LinkCheck.Unresolved[0].Ids);
            Assert.Equal("element_not_found", result.LinkCheck.Unresolved[0].Reason);
            Assert.Equal("partial", result.LinkCheck.Unresolved[0].Label);
            Assert.Equal("revit:2,88,99", result.LinkCheck.Unresolved[0].Target);
            Assert.Equal("bad_syntax", result.LinkCheck.Unresolved[1].Reason);
            Assert.Equal("[first](revit:#0) [partial](revit:#1) [bad](revit-0) [last](revit:#2)", result.Report.Body);
            Assert.Equal(new long[] { 1, 2, 3 }, result.Report.Links.Select(x => x.Elements.Single().Id));
            Assert.Equal("d1", result.Report.Links[2].Doc);
        }

        [Fact]
        public void View_hints_are_resolved_or_reported_as_warnings()
        {
            var body = "[valid](revit:1?view=9) [invalid](revit:2?view=10) [missing](revit:99?view=11)";

            var result = Build(body);

            Assert.Equal(9, result.Report.Links[0].View.Id);
            Assert.Equal("view-9", result.Report.Links[0].View.UniqueId);
            Assert.Null(result.Report.Links[1].View);
            Assert.Equal(2, result.LinkCheck.Warnings.Count);
            Assert.Equal("revit:2?view=10", result.LinkCheck.Warnings[0].Target);
            Assert.Equal("view_hint_invalid", result.LinkCheck.Warnings[0].Reason);
            Assert.Equal("revit:99?view=11", result.LinkCheck.Warnings[1].Target);
            Assert.Equal("view_hint_invalid", result.LinkCheck.Warnings[1].Reason);
            Assert.Equal("element_not_found", Assert.Single(result.LinkCheck.Unresolved).Reason);
        }

        [Fact]
        public void Masks_before_parsing_and_canonicalizing_summary_and_body()
        {
            var body = "sk-abcdefghijk(revit:1) [bad](revit:sk-abcdefghijk)";

            var result = Build(body, summary: "Secret sk-abcdefghijk");

            Assert.Equal("Secret [REDACTED]", result.Report.Summary);
            Assert.Equal(@"\[REDACTED\](revit:1) [bad](revit-[REDACTED])", result.Report.Body);
            Assert.Equal(1, result.LinkCheck.Total);
            Assert.Empty(result.Report.Links);
            Assert.Equal("revit:[REDACTED]", result.LinkCheck.Unresolved[0].Target);
            Assert.Equal("bad_syntax", result.LinkCheck.Unresolved[0].Reason);
        }

        [Fact]
        public void Masked_secret_before_parenthesis_stays_text_but_reads_as_redacted()
        {
            var result = Build("Token Bearer abc.def(revit:1) [kept](revit:2)");

            var stored = MarkdownLiteParser.Parse(result.Report.Body, ReportParseMode.Stored, result.Report.Links.Count);

            Assert.Equal(1, result.LinkCheck.Total);
            Assert.Equal("kept", Assert.Single(result.Report.Links).Label);
            Assert.Equal("kept", Assert.Single(stored.Links).Label);
            Assert.Equal("Token [REDACTED](revit:1) kept", stored.ToPlainText());
        }

        [Fact]
        public void Documents_are_unique_ordered_and_ref_changes_without_mutating_input()
        {
            var changes = new ReportChangeSet
            {
                Documents = new List<ReportDocument> { Document(C), Document(B), Document(A) },
                Changes = new ReportChanges
                {
                    Complete = false,
                    Truncated = true,
                    Documents = new List<ReportDocumentChanges>
                    {
                        new ReportDocumentChanges { Doc = C, Added = new List<ReportChangedElement> { new ReportChangedElement { Id = 7 } } },
                        new ReportDocumentChanges { Doc = B, Modified = new List<ReportChangedElement> { new ReportChangedElement { Id = 8 } } },
                        new ReportDocumentChanges { Doc = A, Deleted = new List<ReportChangedElement> { new ReportChangedElement { Id = 9 } } }
                    }
                }
            };
            var body = "[other](revit:3?doc=" + B + ") [again](revit:1?doc=" + A + ")";

            var result = Build(body, changes: changes);

            Assert.Equal(new[] { A, B, C }, result.Report.Documents.Select(x => x.Token));
            Assert.Equal(new[] { "d0", "d1", "d2" }, result.Report.Documents.Select(x => x.Ref));
            Assert.Equal(new[] { "d1", "d0" }, result.Report.Links.Select(x => x.Doc));
            Assert.Equal(new[] { "d2", "d1", "d0" }, result.Report.Changes.Documents.Select(x => x.Doc));
            Assert.Equal(new[] { C, B, A }, changes.Changes.Documents.Select(x => x.Doc));
            Assert.All(changes.Documents, document => Assert.Null(document.Ref));
            Assert.True(result.Report.Changes.Truncated);
        }

        [Fact]
        public void No_links_still_records_primary_document_and_null_body_stays_null()
        {
            var result = Build(null);

            Assert.Null(result.Report.Body);
            Assert.Single(result.Report.Documents);
            Assert.Equal(A, result.Report.Documents[0].Token);
            Assert.Equal("d0", result.Report.Documents[0].Ref);
            Assert.Equal(0, result.LinkCheck.Total);
            Assert.Null(result.Report.Changes);
            Assert.Equal("synthetic", result.Report.Source.TargetId);
        }

        [Fact]
        public void Result_json_distinguishes_null_empty_and_populated_changes()
        {
            var empty = new ReportChangeSet { Changes = new ReportChanges { Complete = true } };
            var populated = new ReportChangeSet
            {
                Documents = new List<ReportDocument> { Document(A) },
                Changes = new ReportChanges
                {
                    Complete = false,
                    Documents = new List<ReportDocumentChanges>
                    {
                        new ReportDocumentChanges { Doc = A, Added = new List<ReportChangedElement> { new ReportChangedElement { Id = 1 }, new ReportChangedElement { Id = 2 } }, Modified = new List<ReportChangedElement> { new ReportChangedElement { Id = 3 } }, Deleted = new List<ReportChangedElement> { new ReportChangedElement { Id = 4 } } }
                    }
                }
            };
            var saved = new ReportSaveResult { ReportId = "0123456789abcdef0123456789abcdef", Replaced = true };

            var nullJson = JObject.FromObject(ReportBuilder.CreateResponse(Build(null), saved, false));
            var emptyJson = JObject.FromObject(ReportBuilder.CreateResponse(Build(null, changes: empty), saved, true));
            var fullJson = JObject.FromObject(ReportBuilder.CreateResponse(Build("[one](revit:1) [partial](revit:2,88?view=10)", changes: populated), saved, true));

            Assert.Equal(JTokenType.Null, nullJson["changes_attached"].Type);
            Assert.Equal("disabled", nullJson.Value<string>("toast"));
            Assert.Equal(0, emptyJson["changes_attached"].Value<int>("documents"));
            Assert.True(emptyJson["changes_attached"].Value<bool>("complete"));
            Assert.NotNull(emptyJson["changes_attached"]["truncated"]);
            Assert.False(emptyJson["changes_attached"].Value<bool>("truncated"));
            Assert.Equal("process", emptyJson["changes_attached"].Value<string>("scope"));
            Assert.Equal("shown", emptyJson.Value<string>("toast"));
            Assert.Equal(1, fullJson["changes_attached"].Value<int>("documents"));
            Assert.Equal(2, fullJson["changes_attached"].Value<int>("added"));
            Assert.Equal(1, fullJson["changes_attached"].Value<int>("modified"));
            Assert.Equal(1, fullJson["changes_attached"].Value<int>("deleted"));
            Assert.False(fullJson["changes_attached"].Value<bool>("complete"));
            Assert.Equal(saved.ReportId, fullJson.Value<string>("report_id"));
            Assert.True(fullJson.Value<bool>("replaced"));
            Assert.Equal(2, fullJson["links"].Value<int>("resolved"));
            Assert.Equal(88, fullJson["links"]["unresolved"][0]["ids"][0].Value<long>());
            Assert.Equal("view_hint_invalid", fullJson["links"]["warnings"][0].Value<string>("reason"));
        }

        [Fact]
        public void Invalid_input_is_tolerated_by_builder()
        {
            var emptyChanges = new ReportChangeSet { Documents = null, Changes = new ReportChanges { Documents = null } };
            var malformedChanges = new ReportChangeSet
            {
                Documents = new List<ReportDocument> { null, Document(A) },
                Changes = new ReportChanges { Documents = new List<ReportDocumentChanges> { null, new ReportDocumentChanges { Doc = A, Added = null, Modified = null, Deleted = null } } }
            };

            var result = ReportBuilder.Build(null, null, null, "[bad](revit:#0)", null, emptyChanges, null);
            var malformed = Build(null, changes: malformedChanges);

            Assert.Null(result.Report.Summary);
            Assert.Equal("bad_syntax", Assert.Single(result.LinkCheck.Unresolved).Reason);
            Assert.Empty(result.Report.Changes.Documents);
            Assert.Equal("d0", Assert.Single(malformed.Report.Changes.Documents).Doc);
            Assert.Empty(malformed.Report.Changes.Documents[0].Added);
            Assert.Empty(malformed.Report.Changes.Documents[0].Modified);
            Assert.Empty(malformed.Report.Changes.Documents[0].Deleted);
        }
    }
}
