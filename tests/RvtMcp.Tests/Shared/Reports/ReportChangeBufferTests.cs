using System.Linq;
using RvtMcp.Plugin.Reports.Changes;
using RvtMcp.Plugin.Reports.Model;
using Xunit;

namespace RvtMcp.Tests.Shared.Reports
{
    public class ReportChangeBufferTests
    {
        private static CommandDocumentChanges Document(string token, string kind = null, long id = 1, string unique = null, string category = null)
        {
            var doc = new CommandDocumentChanges { Document = new ReportDocument { Token = token, Title = token } };
            var element = new ReportChangedElement { Id = id, UniqueId = unique, Category = category };
            if (kind == "added") doc.Added.Add(element);
            if (kind == "modified") doc.Modified.Add(element);
            if (kind == "deleted") doc.Deleted.Add(element);
            return doc;
        }

        private static CommandChanges Command(params CommandDocumentChanges[] docs)
        {
            var command = new CommandChanges();
            command.Documents.AddRange(docs);
            return command;
        }

        [Theory]
        [InlineData(null, "added", "added")]
        [InlineData(null, "modified", "modified")]
        [InlineData(null, "deleted", "deleted")]
        [InlineData("added", "added", "added")]
        [InlineData("added", "modified", "added")]
        [InlineData("added", "deleted", null)]
        [InlineData("modified", "added", "modified")]
        [InlineData("modified", "modified", "modified")]
        [InlineData("modified", "deleted", "deleted")]
        [InlineData("deleted", "added", "modified")]
        [InlineData("deleted", "modified", "modified")]
        [InlineData("deleted", "deleted", "deleted")]
        public void Merge_table_across_commands(string first, string second, string expected)
        {
            var buffer = new ReportChangeBuffer();
            if (first != null) buffer.Push(Command(Document("a", first, 42, "old", "old category")));
            buffer.Push(Command(Document("a", second, 42, null, "new category")));

            var result = buffer.TakeForReport("task");

            if (expected == null)
            {
                Assert.Empty(result.Documents);
                Assert.Empty(result.Changes.Documents);
                return;
            }
            var doc = Assert.Single(result.Changes.Documents);
            Assert.Single(result.Documents);
            Assert.Equal(expected == "added" ? 1 : 0, doc.Added.Count);
            Assert.Equal(expected == "modified" ? 1 : 0, doc.Modified.Count);
            Assert.Equal(expected == "deleted" ? 1 : 0, doc.Deleted.Count);
            var element = doc.Added.Concat(doc.Modified).Concat(doc.Deleted).SingleOrDefault();
            if (element != null)
            {
                Assert.Equal(42, element.Id);
                Assert.Equal(first == null ? null : "old", element.UniqueId);
                Assert.Equal("new category", element.Category);
            }
        }

        [Fact]
        public void Empty_documents_are_hidden_but_remain_tracked_for_invalidation()
        {
            var buffer = new ReportChangeBuffer();
            buffer.Push(Command(Document("cancelled", "added", 1)));
            buffer.Push(Command(Document("cancelled", "deleted", 1)));
            var incomplete = Document("incomplete", "added", 2);
            incomplete.Incomplete = true;
            buffer.Push(Command(incomplete, Document("kept", "modified", 3)));

            var result = buffer.TakeForReport("task");

            Assert.False(result.Changes.Complete);
            Assert.Equal(new[] { "kept" }, result.Documents.Select(x => x.Token));
            Assert.Equal(new[] { "kept" }, result.Changes.Documents.Select(x => x.Doc));
            Assert.Single(result.Changes.Documents[0].Modified);

            var cancelled = new ReportChangeBuffer();
            cancelled.Push(Command(Document("empty", "added", 1)));
            cancelled.Push(Command(Document("empty", "deleted", 1)));
            cancelled.MarkIncomplete("empty");
            var pending = cancelled.TakeForReport("task");
            Assert.Empty(pending.Documents);
            Assert.Empty(pending.Changes.Documents);
            Assert.False(pending.Changes.Complete);

            var retained = new ReportChangeBuffer();
            retained.Push(Command(Document("empty", "added", 1)));
            retained.Push(Command(Document("empty", "deleted", 1)));
            var empty = retained.TakeForReport("task");
            Assert.True(empty.Changes.Complete);
            Assert.Empty(empty.Documents);
            retained.MarkIncomplete("empty");
            var invalidated = retained.TakeForReport("task");
            Assert.False(invalidated.Changes.Complete);
            Assert.Empty(invalidated.Documents);
            Assert.Empty(invalidated.Changes.Documents);
        }
        [Fact]
        public void Documents_merge_by_token_with_latest_metadata_and_ordered_elements()
        {
            var buffer = new ReportChangeBuffer();
            var family = Document("family", "added", 9);
            family.Document.IsFamily = true;
            buffer.Push(Command(Document("project", "modified", 10), family));
            var project = Document("project", "added", 2);
            project.Document.Title = "renamed";
            project.Document.ModelKey = "key";
            project.Document.Kind = "central";
            project.Added.Add(new ReportChangedElement { Id = 1 });
            buffer.Push(Command(project));

            var result = buffer.TakeForReport("task");

            Assert.Equal(new[] { "project", "family" }, result.Documents.Select(x => x.Token));
            Assert.Equal("renamed", result.Documents[0].Title);
            Assert.Equal("key", result.Documents[0].ModelKey);
            Assert.Equal("central", result.Documents[0].Kind);
            Assert.True(result.Documents[1].IsFamily);
            Assert.All(result.Documents, x => Assert.Null(x.Ref));
            Assert.Equal(new[] { 1L, 2L }, result.Changes.Documents[0].Added.Select(x => x.Id));
            Assert.Equal(new[] { "project", "family" }, result.Changes.Documents.Select(x => x.Doc));
        }

        [Fact]
        public void Limit_drops_new_ids_but_updates_existing_and_counts_current_ids()
        {
            var buffer = new ReportChangeBuffer();
            var doc = Document("a");
            for (long i = 1; i <= 5001; i++) doc.Added.Add(new ReportChangedElement { Id = i });
            buffer.Push(Command(doc));
            buffer.Push(Command(Document("a", "modified", 1, "updated")));
            buffer.Push(Command(Document("a", "deleted", 2)));
            buffer.Push(Command(Document("a", "added", 5002)));

            var result = buffer.TakeForReport("task");
            var elements = result.Changes.Documents.Single().Added;

            Assert.True(result.Changes.Truncated);
            Assert.Equal(5000, elements.Count);
            Assert.Equal("updated", elements.Single(x => x.Id == 1).UniqueId);
            Assert.DoesNotContain(elements, x => x.Id == 5001 || x.Id == 2);
            Assert.Contains(elements, x => x.Id == 5002);
        }

        [Fact]
        public void Incomplete_command_and_document_keep_previous_evidence_only()
        {
            var buffer = new ReportChangeBuffer();
            buffer.Push(Command(Document("a", "added", 1)));
            var bad = Document("a", "deleted", 1);
            bad.Incomplete = true;
            var command = Command(bad, Document("b", "modified", 2));
            command.Incomplete = true;
            buffer.Push(command);

            var result = buffer.TakeForReport("task");

            Assert.False(result.Changes.Complete);
            Assert.Equal(1, result.Changes.Documents[0].Added.Single().Id);
            Assert.Empty(result.Changes.Documents[0].Deleted);
            Assert.Single(result.Changes.Documents[1].Modified);
        }

        [Fact]
        public void Empty_commands_and_repeated_tasks_have_distinct_lifetimes()
        {
            var buffer = new ReportChangeBuffer();

            Assert.Null(buffer.TakeForReport("a"));
            buffer.Push(null);
            Assert.Null(buffer.TakeForReport("a"));
            buffer.Push(Command());
            var empty = buffer.TakeForReport("a");
            Assert.True(empty.Changes.Complete);
            Assert.Empty(empty.Documents);
            buffer.Push(Command(Document("a", "added", 1)));
            Assert.Single(buffer.TakeForReport("a").Changes.Documents.Single().Added);
            Assert.Single(buffer.TakeForReport("a").Changes.Documents.Single().Added);
            Assert.Null(buffer.TakeForReport("b"));
            Assert.Null(buffer.TakeForReport("a"));
        }

        [Fact]
        public void Retained_and_pending_parts_merge_with_same_rules_and_copy_is_isolated()
        {
            var buffer = new ReportChangeBuffer();
            buffer.Push(Command(Document("a", "added", 1, "first")));
            var first = buffer.TakeForReport("task");
            first.Documents[0].Title = "changed";
            first.Changes.Documents[0].Added[0].UniqueId = "changed";
            buffer.Push(Command(Document("a", "modified", 1, "second"), Document("b", "deleted", 2)));

            var result = buffer.TakeForReport("task");

            Assert.Equal("a", result.Documents[0].Title);
            Assert.Equal("second", result.Changes.Documents[0].Added.Single().UniqueId);
            Assert.Single(result.Changes.Documents[1].Deleted);
        }

        [Fact]
        public void Invalidation_marks_retained_and_pending_independently()
        {
            var buffer = new ReportChangeBuffer();
            buffer.Push(Command(Document("retained", "added")));
            buffer.TakeForReport("task");
            buffer.Push(Command(Document("pending", "added")));
            buffer.MarkIncomplete("retained");

            var retained = buffer.TakeForReport("task");

            Assert.False(retained.Changes.Complete);
            var other = new ReportChangeBuffer();
            other.Push(Command(Document("retained", "added")));
            other.TakeForReport("task");
            other.Push(Command(Document("pending", "added")));
            other.MarkIncomplete("pending");
            var pending = other.TakeForReport("different");
            Assert.False(pending.Changes.Complete);
        }

        [Fact]
        public void Mark_incomplete_marks_pending_and_retained_for_known_tokens_only()
        {
            var buffer = new ReportChangeBuffer();
            buffer.Push(Command(Document("a", "added")));
            buffer.TakeForReport("task");
            buffer.Push(Command(Document("a", "modified"), Document("b", "added")));
            buffer.MarkIncomplete(null);
            buffer.MarkIncomplete("unknown");
            Assert.True(buffer.TakeForReport("other").Changes.Complete);
            buffer.Push(Command(Document("a", "modified")));
            buffer.MarkIncomplete("a");

            Assert.False(buffer.TakeForReport("other").Changes.Complete);
            buffer.Push(Command(Document("b", "modified")));
            buffer.MarkIncomplete("b");
            Assert.False(buffer.TakeForReport("new").Changes.Complete);
        }
    }
}
