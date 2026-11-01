using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using RvtMcp.Plugin.Reports.Changes;
using RvtMcp.Plugin.Reports.Model;
using RvtMcp.Plugin.Reports.Storage;

namespace RvtMcp.Plugin.Reports.Binding
{
    public sealed class ReportBuildResult
    {
        public AgentReport Report { get; set; }
        public ReportLinkCheck LinkCheck { get; set; }
        public ReportSaveResult Saved { get; set; }
    }

    public sealed class ReportLinkCheck
    {
        [JsonProperty("total")]
        public int Total { get; set; }
        [JsonProperty("resolved")]
        public int Resolved { get; set; }
        [JsonProperty("unresolved")]
        public List<ReportUnresolvedLink> Unresolved { get; set; } = new List<ReportUnresolvedLink>();
        [JsonProperty("warnings")]
        public List<ReportLinkWarning> Warnings { get; set; } = new List<ReportLinkWarning>();
    }

    public sealed class ReportUnresolvedLink
    {
        [JsonProperty("label")]
        public string Label { get; set; }
        [JsonProperty("target")]
        public string Target { get; set; }
        [JsonProperty("reason")]
        public string Reason { get; set; }
        [JsonProperty("ids", NullValueHandling = NullValueHandling.Ignore)]
        public List<long> Ids { get; set; }
    }

    public sealed class ReportLinkWarning
    {
        [JsonProperty("target")]
        public string Target { get; set; }
        [JsonProperty("reason")]
        public string Reason { get; set; }
    }

    public static class ReportBuilder
    {
        public static ReportBuildResult BuildAndSave(string taskId, string outcome, string summary, string body,
            IReportResolver resolver, ReportSource source, ReportChangeBuffer buffer, ReportStore store, ReportSubmissionScope scope = null)
        {
            scope = scope ?? new ReportSubmissionScope { Session = source?.ClientSession };
            var changes = buffer.TakeForReport(taskId, () => store.LoadForTask(taskId, scope.Session), scope);
            if (scope.Session != null)
            {
                source = source ?? new ReportSource();
                source.ClientSession = scope.Session;
                source.CaptureScope = buffer.ScopeFor(taskId, scope.Session, scope.Kind);
            }
            var result = Build(taskId, outcome, summary, body, resolver, changes, source);
            result.Saved = store.Save(result.Report);
            buffer.ConfirmSaved(taskId, scope.Session);
            return result;
        }

        public static ReportBuildResult Build(string taskId, string outcome, string summary, string body,
            IReportResolver resolver, ReportChangeSet changes, ReportSource source)
        {
            var report = new AgentReport
            {
                TaskId = taskId,
                Outcome = outcome,
                Summary = SecretMasker.Mask(summary),
                Source = source
            };
            var check = new ReportLinkCheck();
            var references = new Dictionary<string, string>();
            var active = resolver?.ActiveDocument;
            AddDocument(report, references, active);

            if (body != null)
            {
                var parsed = MarkdownLiteParser.Parse(MaskBody(body), ReportParseMode.Raw);
                var indices = new Dictionary<int, int>();
                for (var i = 0; i < parsed.Links.Count; i++)
                {
                    var occurrence = parsed.Links[i];
                    check.Total++;
                    if (occurrence.Reason != null)
                    {
                        AddUnresolved(check, occurrence, occurrence.Reason);
                        continue;
                    }

                    var syntax = occurrence.Syntax;
                    var document = syntax.DocumentToken == null ? active : resolver?.FindDocument(syntax.DocumentToken);
                    if (document == null || document.Token == null)
                    {
                        AddUnresolved(check, occurrence, syntax.DocumentToken == null ? "no_document" : "document_not_open");
                        continue;
                    }

                    var link = new ReportLink { Label = occurrence.Label };
                    var missing = new List<long>();
                    foreach (var id in syntax.Ids)
                    {
                        var element = resolver?.FindElement(document.Token, id);
                        if (element == null) missing.Add(id);
                        else link.Elements.Add(element);
                    }
                    if (syntax.ViewId.HasValue)
                    {
                        link.View = resolver?.FindView(document.Token, syntax.ViewId.Value);
                        if (link.View == null)
                            check.Warnings.Add(new ReportLinkWarning { Target = occurrence.Target, Reason = "view_hint_invalid" });
                    }
                    if (link.Elements.Count == 0)
                    {
                        AddUnresolved(check, occurrence, "element_not_found");
                        continue;
                    }
                    if (missing.Count > 0) AddUnresolved(check, occurrence, "element_not_found", missing);
                    link.Doc = AddDocument(report, references, document);
                    indices.Add(i, report.Links.Count);
                    report.Links.Add(link);
                    check.Resolved++;
                }
                report.Body = ReportBodyCanonicalizer.Canonicalize(parsed.Body, parsed.Links, indices);
            }

            if (changes?.Changes != null)
            {
                foreach (var document in changes.Documents ?? new List<ReportDocument>())
                    AddDocument(report, references, document);
                report.Changes = new ReportChanges { Complete = changes.Changes.Complete, Truncated = changes.Changes.Truncated,
                    Scope = changes.Changes.Scope, Calls = changes.Changes.Calls?.ToList(), CaptureSequence = changes.Changes.CaptureSequence };
                foreach (var item in changes.Changes.Documents ?? new List<ReportDocumentChanges>())
                {
                    if (item == null || item.Doc == null || !references.TryGetValue(item.Doc, out var reference)) continue;
                    report.Changes.Documents.Add(new ReportDocumentChanges
                    {
                        Doc = reference,
                        Added = new List<ReportChangedElement>(item.Added ?? new List<ReportChangedElement>()),
                        Modified = new List<ReportChangedElement>(item.Modified ?? new List<ReportChangedElement>()),
                        Deleted = new List<ReportChangedElement>(item.Deleted ?? new List<ReportChangedElement>())
                    });
                }
            }
            return new ReportBuildResult { Report = report, LinkCheck = check };
        }

        public static object CreateResponse(ReportBuildResult result, ReportSaveResult save, bool toastEnabled)
        {
            var changes = result.Report.Changes;
            object attached = null;
            if (changes != null)
            {
                var added = 0;
                var modified = 0;
                var deleted = 0;
                foreach (var document in changes.Documents)
                {
                    added += document.Added.Count;
                    modified += document.Modified.Count;
                    deleted += document.Deleted.Count;
                }
                attached = new { documents = changes.Documents.Count, added, modified, deleted, complete = changes.Complete,
                    truncated = changes.Truncated, scope = changes.Scope };
            }
            return new
            {
                report_id = save.ReportId,
                replaced = save.Replaced,
                links = result.LinkCheck,
                changes_attached = attached,
                provenance = new { scope = result.Report.Source?.CaptureScope ?? changes?.Scope ?? "process",
                    client_session = result.Report.Source?.ClientSession, total_call_references = changes?.Calls?.Count ?? 0,
                    calls = changes?.Calls?.Take(10).ToList(), calls_truncated = (changes?.Calls?.Count ?? 0) > 10 },
                toast = toastEnabled ? "shown" : "disabled"
            };
        }

        // SecretMasker writes "[REDACTED]"; followed by "(" it would read as link markup the agent never wrote.
        private static string MaskBody(string body)
        {
            return SecretMasker.Mask(body).Replace("[REDACTED](", @"\[REDACTED\](");
        }

        private static void AddUnresolved(ReportLinkCheck check, MarkdownLinkOccurrence occurrence, string reason, List<long> ids = null)
        {
            check.Unresolved.Add(new ReportUnresolvedLink { Label = occurrence.Label, Target = occurrence.Target, Reason = reason, Ids = ids });
        }

        private static string AddDocument(AgentReport report, Dictionary<string, string> references, ReportDocument document)
        {
            if (document?.Token == null) return null;
            if (references.TryGetValue(document.Token, out var reference)) return reference;
            reference = "d" + report.Documents.Count.ToString(CultureInfo.InvariantCulture);
            references.Add(document.Token, reference);
            report.Documents.Add(new ReportDocument
            {
                Ref = reference,
                Token = document.Token,
                ModelKey = document.ModelKey,
                Title = document.Title,
                IsFamily = document.IsFamily,
                Kind = document.Kind
            });
            return reference;
        }
    }
}
