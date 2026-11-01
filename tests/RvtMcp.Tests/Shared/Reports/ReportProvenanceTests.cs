using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Plugin.Reports.Binding;
using RvtMcp.Plugin.Reports.Changes;
using RvtMcp.Plugin.Reports.Model;
using RvtMcp.Plugin.Reports.Storage;
using Xunit;

namespace RvtMcp.Tests.Shared.Reports
{
    public sealed class ReportProvenanceTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "rvt-provenance-" + Guid.NewGuid().ToString("N"));
        private readonly ReportChangeBuffer _buffer = new ReportChangeBuffer();
        private readonly ReportStore _store;
        private static readonly string A = new string('a', 32), B = new string('b', 32);
        public ReportProvenanceTests() { _store = new ReportStore(_dir); }
        public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
        private string Push(string session, long element, string kind = "added", bool history = true)
        {
            var id = Guid.NewGuid().ToString("N");
            var document = new CommandDocumentChanges { Document = new ReportDocument { Token = "doc", ModelKey = new string('c', 64) } };
            var e = new ReportChangedElement { Id = element, UniqueId = "uid-" + element };
            if (kind == "added") document.Added.Add(e); else if (kind == "deleted") document.Deleted.Add(e); else document.Modified.Add(e);
            var command = new CommandChanges(); command.Documents.Add(document);
            _buffer.Push(command, new ReportCallContext { CallId = id, Session = session, HistoryRequested = history }, "synthetic");
            return id;
        }
        private ReportBuildResult Submit(string session, string task, params string[] ids) => ReportBuilder.BuildAndSave(task, "completed", "Synthetic", null, null,
            new ReportSource { ClientSession = session }, _buffer, _store, new ReportSubmissionScope { Session = session, CallIds = ids });
        private static long[] Added(ReportBuildResult report) => report.Report.Changes.Documents.SelectMany(d => d.Added).Select(e => e.Id).ToArray();

        [Fact] public void Two_clients_with_same_task_id_neither_mix_nor_replace()
        {
            Push(A, 1); Push(B, 2);
            var a = Submit(A, "same", null); var b = Submit(B, "same", null);
            Assert.Equal(new long[] { 1 }, Added(a)); Assert.Equal(new long[] { 2 }, Added(b));
            Assert.NotEqual(a.Report.ReportId, b.Report.ReportId); Assert.Equal(2, _store.LoadNewest().Count);
            Assert.Equal("client", a.Report.Changes.Scope);
        }
        [Fact] public void Explicit_jobs_interleave_on_one_client_and_resend_without_consuming_other_calls()
        {
            var a1 = Push(A, 1); var b1 = Push(A, 2); var a2 = Push(A, 3);
            var a = Submit(A, "job-a", a1, a2); Submit(A, "job-b", b1);
            var other = Push(A, 4);
            var resend = Submit(A, "job-a", null);
            Assert.Equal(new long[] { 1, 3 }, Added(resend)); Assert.Equal(a.Report.ReportId, resend.Report.ReportId);
            Assert.Equal(new long[] { 4 }, Added(Submit(A, "other", other)));
            Assert.Equal("calls", resend.Report.Changes.Scope); Assert.Equal(2, resend.Report.Changes.Calls.Count);
        }
        [Fact] public void Resends_accept_previously_owned_ids_and_append_only_newer_calls()
        {
            var a = Push(A, 1); var first = Submit(A, "a", a); var b = Push(A, 2, "modified");
            var next = Submit(A, "a", a, b);
            Assert.Equal(2, next.Report.Changes.Calls.Count); Assert.Equal(first.Report.ReportId, next.Report.ReportId);
            Assert.Single(next.Report.Changes.Documents.Single().Modified);
        }
        [Theory] [InlineData("foreign")] [InlineData("missing")] [InlineData("assigned")]
        public void Invalid_selection_is_atomic(string invalid)
        {
            var own = Push(A, 1); var bad = invalid == "missing" ? Guid.NewGuid().ToString("N") : Push(invalid == "foreign" ? B : A, 2);
            if (invalid == "assigned") Submit(A, "assigned", bad);
            Assert.Throws<ReportCaptureException>(() => Submit(A, "bad", own, bad));
            Assert.Equal(new long[] { 1 }, Added(Submit(A, "valid", own)));
        }
        [Fact] public void Native_sequence_controls_net_changes_and_rejects_late_older_calls()
        {
            var add = Push(A, 1); var delete = Push(A, 1, "deleted");
            Assert.Empty(Submit(A, "net", delete, add).Report.Changes.Documents);
            var older = Push(A, 2); var newer = Push(A, 3); Submit(A, "order", newer);
            Assert.Contains("REPORT_CAPTURE_ORDER", Assert.Throws<ReportCaptureException>(() => Submit(A, "order", older)).Message);
            Assert.Equal(new long[] { 2 }, Added(Submit(A, "separate", older)));
        }
        [Fact] public void Empty_explicit_scope_is_persisted_and_never_consumes_an_interval()
        {
            Submit(A, "empty", new string[0]); var other = Push(A, 9);
            Assert.Null(Submit(A, "empty", null).Report.Changes);
            Assert.Equal("calls", _store.LoadForTask("empty", A).Source.CaptureScope);
            Assert.Equal(new long[] { 9 }, Added(Submit(A, "other", other)));
        }
        [Fact] public void Interval_cannot_be_silently_relabelled_as_task_attributed()
        {
            Push(A, 1); Submit(A, "interval", null); var id = Push(A, 2);
            Assert.Contains("REPORT_SCOPE_CONFLICT", Assert.Throws<ReportCaptureException>(() => Submit(A, "interval", id)).Message);
            Assert.Equal(new long[] { 2 }, Added(Submit(A, "job", id)));
        }
        [Fact] public void Failed_checkpoint_read_reserves_selected_delta_across_other_jobs_and_clients()
        {
            var first = Push(A, 1); Submit(A, "a", first);
            var path = Directory.GetFiles(_dir).Single(); var json = File.ReadAllText(path); File.WriteAllText(path, "broken");
            var next = Push(A, 2); var other = Push(A, 3); var foreign = Push(B, 4);
            Assert.Throws<IOException>(() => Submit(A, "a", first, next));
            Submit(A, "other", other); Submit(B, "foreign", foreign);
            File.WriteAllText(path, json);
            Assert.Equal(new long[] { 1, 2 }, Added(Submit(A, "a", first, next)));
        }
        [Theory] [InlineData("foreign")] [InlineData("missing")] [InlineData("assigned")]
        public void Invalid_selection_during_checkpoint_failure_does_not_claim_valid_calls(string invalid)
        {
            var first = Push(A, 1); Submit(A, "a", first);
            var path = Directory.GetFiles(_dir).Single(); var json = File.ReadAllText(path); File.WriteAllText(path, "broken");
            var next = Push(A, 2);
            var bad = invalid == "missing" ? Guid.NewGuid().ToString("N") : Push(invalid == "foreign" ? B : A, 3);
            if (invalid == "assigned") Submit(A, "assigned", bad);
            Assert.Throws<ReportCaptureException>(() => Submit(A, "a", first, next, bad));
            Assert.Equal(new long[] { 2 }, Added(Submit(A, "valid", next)));
            File.WriteAllText(path, json);
            Assert.Equal(new long[] { 1 }, Added(Submit(A, "a", first)));
        }
        [Fact] public void Invalid_checkpoint_retry_preserves_prior_reservations_without_claiming_new_calls()
        {
            var first = Push(A, 1); Submit(A, "a", first);
            var path = Directory.GetFiles(_dir).Single(); var json = File.ReadAllText(path); File.WriteAllText(path, "broken");
            var reserved = Push(A, 2);
            Assert.Throws<IOException>(() => Submit(A, "a", first, reserved));
            var next = Push(A, 3);
            Assert.Throws<ReportCaptureException>(() => Submit(A, "a", first, next, Guid.NewGuid().ToString("N")));
            Assert.Equal(new long[] { 3 }, Added(Submit(A, "other", next)));
            Assert.Throws<ReportCaptureException>(() => Submit(A, "steal", reserved));
            File.WriteAllText(path, json);
            Assert.Equal(new long[] { 1, 2 }, Added(Submit(A, "a", first, reserved)));
        }
        [Fact] public void Checkpoint_failure_still_enforces_saved_reference_limit_before_claiming()
        {
            for (var batch = 0; batch < 4; batch++)
            {
                var ids = Enumerable.Range(batch * 256, 256).Select(i => Push(A, i + 1)).ToArray();
                Submit(A, "limit", ids);
            }
            var path = Directory.GetFiles(_dir).Single(); var json = File.ReadAllText(path); File.WriteAllText(path, "broken");
            var next = Push(A, 1025);
            Assert.Contains("REPORT_CAPTURE_LIMIT", Assert.Throws<ReportCaptureException>(() => Submit(A, "limit", next)).Message);
            Assert.Equal(new long[] { 1025 }, Added(Submit(A, "other", next)));
            File.WriteAllText(path, json);
            Assert.Equal(1024, Submit(A, "limit", null).Report.Changes.Calls.Count);
        }
        [Fact] public void Checkpoint_failure_still_rejects_older_unclaimed_calls()
        {
            var older = Push(A, 1); var newer = Push(A, 2); Submit(A, "a", newer);
            var path = Directory.GetFiles(_dir).Single(); File.WriteAllText(path, "broken");
            Assert.Contains("REPORT_CAPTURE_ORDER", Assert.Throws<ReportCaptureException>(() => Submit(A, "a", older)).Message);
            Assert.Equal(new long[] { 1 }, Added(Submit(A, "other", older)));
        }
        [Fact] public void Failed_checkpoint_read_without_ownership_metadata_leaves_calls_pending()
        {
            var id = Push(A, 1);
            Assert.Throws<IOException>(() => _buffer.TakeForReport("unknown", () => throw new IOException("Unreadable checkpoint"),
                new ReportSubmissionScope { Session = A, CallIds = new[] { id } }));
            Assert.Equal(new long[] { 1 }, Added(Submit(A, "other", id)));
        }
        [Fact] public void Failed_checkpoint_read_does_not_change_an_interval_to_explicit_scope()
        {
            Push(A, 1); Submit(A, "interval", null);
            File.WriteAllText(Directory.GetFiles(_dir).Single(), "broken");
            var next = Push(A, 2);
            Assert.Contains("REPORT_SCOPE_CONFLICT", Assert.Throws<ReportCaptureException>(() => Submit(A, "interval", next)).Message);
            Assert.Equal(new long[] { 2 }, Added(Submit(A, "other", next)));
        }
        [Fact] public void Failed_save_keeps_claimed_calls_for_retry_only_by_their_task()
        {
            var id = Push(A, 1); Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "block"), "x");
            var blocked = new ReportStore(Path.Combine(_dir, "block"));
            Assert.Throws<IOException>(() => ReportBuilder.BuildAndSave("a", "completed", "Synthetic", null, null,
                new ReportSource { ClientSession = A }, _buffer, blocked, new ReportSubmissionScope { Session = A, CallIds = new[] { id } }));
            Assert.Throws<ReportCaptureException>(() => Submit(A, "b", id));
            Assert.Equal(new long[] { 1 }, Added(Submit(A, "a", id)));
        }
        [Fact] public void Expired_calls_are_unavailable_and_client_interval_is_explicitly_incomplete()
        {
            var expired = Push(A, 1);
            for (var i = 0; i < ReportChangeBuffer.PendingCallLimit; i++) Push(A, i + 2);
            Assert.Throws<ReportCaptureException>(() => Submit(A, "expired", expired));
            var interval = Submit(A, "interval", null);
            Assert.False(interval.Report.Changes.Complete); Assert.True(interval.Report.Changes.Truncated);
            Assert.Equal(512, interval.Report.Changes.Calls.Count);
        }
        [Fact] public void History_disabled_still_captures_exact_ids_and_records_the_fact()
        {
            var id = Push(A, 1, history: false); var report = Submit(A, "a", id);
            var call = Assert.Single(report.Report.Changes.Calls);
            Assert.Equal(id, call.CallId); Assert.Equal(A, call.Session); Assert.False(call.HistoryRequested);
            Assert.Contains("client_session", ReportJson.Serialize(report.Report));
        }
        [Theory] [InlineData("{}")] [InlineData("[1]")] [InlineData("[\"bad\"]")]
        public void Call_ids_require_server_issued_guid_format(string json) => Assert.Throws<ArgumentException>(() => ReportCallContext.ParseCallIds(json));
        [Fact] public void Response_reference_preview_is_bounded_while_saved_report_keeps_all_references()
        {
            for (var i = 0; i < 20; i++) Push(A, i + 1);
            var report = Submit(A, "bounded", null);
            var response = JObject.FromObject(ReportBuilder.CreateResponse(report, report.Saved, false));
            Assert.Equal(20, report.Report.Changes.Calls.Count);
            Assert.Equal(10, response["provenance"]["calls"].Count());
            Assert.True(response["provenance"].Value<bool>("calls_truncated"));
            Assert.Equal(20, response["provenance"].Value<int>("total_call_references"));
        }
        [Fact] public void Capture_failure_is_visible_on_the_next_client_interval()
        {
            _buffer.MarkCaptureUnavailable(A);
            var report = Submit(A, "failure", null);
            Assert.False(report.Report.Changes.Complete); Assert.True(report.Report.Changes.Truncated);
        }
        [Fact] public void Execution_context_restores_on_exception()
        {
            Assert.Null(ReportExecutionContext.Current);
            try { using (ReportExecutionContext.Enter(new ReportCallContext { Session = A })) { throw new Exception(); } } catch { }
            Assert.Null(ReportExecutionContext.Current);
        }
    }
}
