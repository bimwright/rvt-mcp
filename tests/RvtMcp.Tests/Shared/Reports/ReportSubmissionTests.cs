using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Reports.Binding;
using RvtMcp.Plugin.Reports.Changes;
using RvtMcp.Plugin.Reports.Model;
using RvtMcp.Plugin.Reports.Storage;
using Xunit;

namespace RvtMcp.Tests.Shared.Reports
{
    public sealed class ReportSubmissionTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "rvt-report-submit-" + Guid.NewGuid().ToString("N"));
        private readonly ReportChangeBuffer _buffer = new ReportChangeBuffer();
        private readonly ReportStore _store;

        public ReportSubmissionTests() { _store = new ReportStore(_directory); }
        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
            else if (File.Exists(_directory)) File.Delete(_directory);
        }

        private void Push(string token, long id, string kind = "added", bool incomplete = false)
        {
            var document = new CommandDocumentChanges { Document = new ReportDocument { Token = token, Title = token } };
            var element = new ReportChangedElement { Id = id, UniqueId = "uid-" + id };
            if (kind == "added") document.Added.Add(element);
            else if (kind == "modified") document.Modified.Add(element);
            else document.Deleted.Add(element);
            var command = new CommandChanges { Incomplete = incomplete };
            command.Documents.Add(document);
            _buffer.Push(command);
        }

        private ReportBuildResult Submit(string task, IReportResolver resolver = null) =>
            ReportBuilder.BuildAndSave(task, "completed", "Synthetic report", null, resolver,
                new ReportSource { Pid = 123, ProcessStartUtc = "2026-10-31T00:00:00Z" }, _buffer, _store);

        [Fact]
        public void Resend_after_another_task_keeps_saved_evidence_and_remaps_document_refs()
        {
            Push("project", 1);
            var first = Submit("a");
            Push("project", 2);
            Submit("b");
            Push("family", 3);
            var updated = Submit("a", new Resolver("family"));

            Assert.True(updated.Saved.Replaced);
            Assert.Equal(first.Report.ReportId, updated.Report.ReportId);
            Assert.Equal(first.Report.CreatedUtc, updated.Report.CreatedUtc);
            var loaded = _store.LoadNewest().Single(r => r.TaskId == "a");
            Assert.Equal(new[] { "family", "project" }, loaded.Documents.Select(d => d.Token));
            var project = loaded.Documents.Single(d => d.Token == "project");
            Assert.Equal(1, Assert.Single(loaded.Changes.Documents.Single(d => d.Doc == project.Ref).Added).Id);
            Assert.DoesNotContain(loaded.Changes.Documents.SelectMany(d => d.Added), e => e.Id == 2);
            Assert.Equal(3, Assert.Single(loaded.Changes.Documents.Single(d => d.Doc == "d0").Added).Id);
            Assert.Equal(2, _store.LoadNewest().Count);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Failed_save_survives_an_interleaved_task_and_retry(bool previouslySaved, bool blockRead)
        {
            if (previouslySaved) { Push("project", 1); Submit("a"); }
            Push("project", 2);
            Directory.CreateDirectory(_directory);
            using (var blocker = new FileStream(BlockedPath(previouslySaved), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                blockRead ? FileShare.None : FileShare.Read))
                Assert.ThrowsAny<IOException>(() => Submit("a"));
            if (!previouslySaved) File.Delete(_directory);
            Push("project", 3);
            Submit("b");
            Push("project", 4);
            var retry = Submit("a");

            var ids = _store.LoadNewest().Single(r => r.TaskId == "a").Changes.Documents.SelectMany(d => d.Added).Select(e => e.Id);
            Assert.Equal(previouslySaved ? new long[] { 1, 2, 4 } : new long[] { 2, 4 }, ids);
            Assert.Equal(previouslySaved, retry.Saved.Replaced);
            Assert.Equal(3, Assert.Single(_store.LoadNewest().Single(r => r.TaskId == "b").Changes.Documents.Single().Added).Id);
            Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        }

        private string BlockedPath(bool previouslySaved)
        {
            // Blocking a new report's directory is deterministic without relying on Windows ACLs.
            if (previouslySaved) return Directory.GetFiles(_directory, "*.json").Single();
            Directory.Delete(_directory);
            return _directory;
        }

        [Fact]
        public void Interleaved_jobs_are_explicitly_process_scoped_in_stored_data_and_response()
        {
            Push("project", 1);
            Push("project", 2);
            var first = Submit("a");
            var json = JObject.FromObject(ReportBuilder.CreateResponse(first, first.Saved, false));

            Assert.Equal("process", json["changes_attached"].Value<string>("scope"));
            Assert.Equal("process", JObject.Parse(ReportJson.Serialize(first.Report))["changes"].Value<string>("scope"));
            Assert.Equal(2, json["changes_attached"].Value<int>("added"));
            Assert.Null(Submit("b").Report.Changes);
        }

        [Fact]
        public void Resends_merge_net_changes_without_replaying_the_saved_checkpoint_twice()
        {
            Push("project", 1);
            Push("project", 2);
            Submit("a");
            Push("other", 9);
            Submit("b");
            Push("project", 1, "deleted");
            Push("project", 2, "modified");
            var updated = Submit("a");
            var repeated = Submit("a");

            Assert.Equal(updated.Report.ReportId, repeated.Report.ReportId);
            var changes = Assert.Single(repeated.Report.Changes.Documents);
            Assert.Equal(2, Assert.Single(changes.Added).Id);
            Assert.Empty(changes.Modified);
            Assert.Empty(changes.Deleted);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Undo_invalidation_survives_a_saved_checkpoint_and_another_task(bool netEmpty)
        {
            Push("project", 1);
            if (netEmpty) Push("project", 1, "deleted");
            Submit("a");
            Push("other", 2);
            Submit("b");
            _buffer.MarkIncomplete("project");

            var updated = Submit("a");

            Assert.False(updated.Report.Changes.Complete);
            Assert.Equal(netEmpty ? 0 : 1, updated.Report.Changes.Documents.Count);
            Assert.True(_store.LoadNewest().Single(r => r.TaskId == "b").Changes.Complete);
        }

        [Fact]
        public void Corrupt_prior_report_is_not_overwritten_and_pending_capture_can_be_retried()
        {
            Push("project", 1);
            Submit("a");
            var path = Directory.GetFiles(_directory, "*.json").Single();
            var original = File.ReadAllText(path);
            File.WriteAllText(path, "not-json");
            Push("project", 2);

            Assert.Throws<IOException>(() => Submit("a"));
            Assert.Equal("not-json", File.ReadAllText(path));
            File.WriteAllText(path, original);

            Assert.Equal(new long[] { 1, 2 }, Submit("a").Report.Changes.Documents.Single().Added.Select(e => e.Id));
        }

        [Fact]
        public void Another_process_does_not_restore_or_replace_a_report_with_the_same_task_id()
        {
            Push("project", 1);
            var first = Submit("a");
            var second = ReportBuilder.BuildAndSave("a", "completed", "Other process", null, null,
                new ReportSource { Pid = 456 }, new ReportChangeBuffer(), new ReportStore(_directory));

            Assert.False(second.Saved.Replaced);
            Assert.NotEqual(first.Report.ReportId, second.Report.ReportId);
            Assert.Null(second.Report.Changes);
            Assert.Equal(2, _store.LoadNewest().Count);
        }

        [Fact]
        public void Legacy_report_changes_without_scope_read_as_process_scope()
        {
            Assert.True(ReportJson.TryDeserialize("{\"schema_version\":1,\"changes\":{\"complete\":true}}", out var report, out _));
            Assert.Equal("process", report.Changes.Scope);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Response_reports_truncation_independently_of_capture_completeness(bool incomplete)
        {
            for (var i = 1; i <= 5001; i++) Push("project", i, incomplete: incomplete);
            var result = Submit("a");
            var attached = JObject.FromObject(ReportBuilder.CreateResponse(result, result.Saved, false))["changes_attached"];

            Assert.True(attached.Value<bool>("truncated"));
            Assert.Equal(!incomplete, attached.Value<bool>("complete"));
            Assert.Equal(5000, attached.Value<int>("added"));
            Submit("b");
            var resent = Submit("a");
            Assert.True(resent.Report.Changes.Truncated);
            Assert.Equal(!incomplete, resent.Report.Changes.Complete);
            Assert.Equal(5000, resent.Report.Changes.Documents.Single().Added.Count);
        }

        private sealed class Resolver : IReportResolver
        {
            public Resolver(string token) { ActiveDocument = new ReportDocument { Token = token, Title = token }; }
            public ReportDocument ActiveDocument { get; }
            public ReportDocument FindDocument(string token) => null;
            public ReportElement FindElement(string token, long id) => null;
            public ReportView FindView(string token, long id) => null;
        }
    }
}
