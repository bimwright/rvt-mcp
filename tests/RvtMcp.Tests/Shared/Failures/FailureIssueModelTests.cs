using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using RvtMcp.Failures;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class FailureIssueModelTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rvt-issue-models-" + Guid.NewGuid().ToString("N"));
        private string Db => Path.Combine(_root, "ledger.db");

        private FailureModel Model(FailureLedgerStore store, string physical, string title, bool? isFamily = null)
            => store.ResolveModel(new FailureModelIdentity { PhysicalKey = physical, Title = title, IsFamilyDocument = isFamily });

        private FailureOperation Operation(FailureLedgerStore store, FailureModel model, int year)
        {
            var operation = new FailureOperation { Execution = "completed", Outcome = "rolled_back", RevitYear = year };
            operation.ModelScopeKeys.Add(model.ModelScopeKey);
            Assert.True(store.SaveOperation(operation).Recorded);
            return operation;
        }

        private FailureIncident Incident(FailureLedgerStore store, FailureModel model, FailureOperation operation,
            string state = "open", int year = 0, string title = null, int messageCount = 0)
        {
            var incident = new FailureIncident { ModelScopeKey = model.ModelScopeKey, OperationId = operation.OperationId,
                State = state, RevitYear = year, ModelTitle = title, MessageCount = messageCount };
            Assert.True(store.RecordIncident(incident).Recorded);
            return incident;
        }

        [Fact]
        public void Empty_read_only_queries_create_an_empty_database_and_reject_invalid_state()
        {
            var reader = new FailureLedgerStore(Db, readOnly: true);
            Assert.Throws<ArgumentException>(() => reader.GetIssueModels("invalid"));
            Assert.False(Directory.Exists(_root));
            Assert.Empty(reader.GetIssueModels());
            Assert.True(File.Exists(Db));
        }

        [Fact]
        public void State_counts_follow_decisions_and_exclude_models_without_matching_incidents()
        {
            var store = new FailureLedgerStore(Db);
            var project = Model(store, "project", "Sample", false);
            var family = Model(store, "family", "Sample", true);
            var empty = Model(store, "empty", "No issues", false);
            Operation(store, empty, 2026); // An operation alone is not an issue.
            var operation = Operation(store, project, 2026);
            var first = Incident(store, project, operation);
            var second = Incident(store, project, operation);
            Incident(store, family, Operation(store, family, 2027), "observed");
            var row = Assert.Single(store.GetIssueModels("open"));
            Assert.Equal(project.ModelScopeKey, row.ModelScopeKey);
            Assert.Equal(2, row.IssueCount);
            Assert.Equal(false, row.IsFamilyDocument);
            foreach (var incident in new[] { first, second })
                Assert.True(store.ApplyDecision(new FailureDecision { ModelScopeKey = project.ModelScopeKey,
                    IncidentId = incident.IncidentId, Action = "rework", WindowsUser = "tester" }).Recorded);
            Assert.Empty(store.GetIssueModels("open"));
            Assert.Equal(2, Assert.Single(store.GetIssueModels("resolved_rework")).IssueCount);
            Assert.Equal(true, Assert.Single(store.GetIssueModels("observed")).IsFamilyDocument);
            Assert.Equal(2, store.GetIssueModels().Count);
        }

        [Fact]
        public void Host_year_groups_match_record_queries_and_are_part_of_the_cursor()
        {
            var store = new FailureLedgerStore(Db);
            var model = Model(store, "same_file", "Shared name", false);
            var operation = Operation(store, model, 2026);
            Incident(store, model, operation); // Legacy incident: year falls back to operation.
            Incident(store, model, operation, year: 2026);
            Incident(store, model, operation, year: 2027);
            Incident(store, model, operation, year: 2027);
            Incident(store, model, Operation(store, model, 0));
            var rows = store.GetIssueModels("open");
            Assert.Equal(new[] { 2027, 2026, 0 }, rows.Select(r => r.RevitYear));
            Assert.Equal(new long[] { 2, 2, 1 }, rows.Select(r => r.IssueCount));
            foreach (var row in rows)
                Assert.Equal(row.IssueCount, store.Query(new FailureRecordQuery {
                    ModelScopeKey = row.ModelScopeKey, RevitYear = row.RevitYear, State = "open" }).Records.Count);
            var query = new FailureRecordQuery { ModelScopeKey = model.ModelScopeKey, RevitYear = 2026, Limit = 1 };
            var firstPage = store.Query(query);
            Assert.NotNull(firstPage.NextCursor);
            query.Cursor = firstPage.NextCursor;
            Assert.Single(store.Query(query).Records);
            query.RevitYear = 2027;
            Assert.Throws<ArgumentException>(() => store.Query(query));
            Assert.Equal(5, store.Query(new FailureRecordQuery { ModelScopeKey = model.ModelScopeKey }).Records.Count);
        }

        [Fact]
        public void Legacy_model_kind_stays_unknown_until_observed_and_migration_preserves_issues()
        {
            var store = new FailureLedgerStore(Db);
            var model = Model(store, "legacy", "Family-looking-name");
            Incident(store, model, Operation(store, model, 2026));
            using (var connection = new SqliteConnection("Data Source=" + Db + ";Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "ALTER TABLE models DROP COLUMN is_family_document;";
                command.ExecuteNonQuery();
            }
            Assert.Null(Assert.Single(new FailureLedgerStore(Db, readOnly: true).GetIssueModels()).IsFamilyDocument);
            store.Initialize();
            Assert.Null(Assert.Single(store.GetIssueModels()).IsFamilyDocument);
            var known = store.ResolveModel(new FailureModelIdentity { KnownScopeKey = model.ModelScopeKey,
                IsFamilyDocument = true });
            Assert.Equal(model.ModelScopeKey, known.ModelScopeKey);
            Assert.Equal(true, Assert.Single(store.GetIssueModels()).IsFamilyDocument);
            Model(store, "legacy", "Family-looking-name"); // Missing metadata must not erase captured evidence.
            Assert.Equal(true, Assert.Single(store.GetIssueModels()).IsFamilyDocument);
            Assert.Single(store.Query(new FailureRecordQuery { ModelScopeKey = model.ModelScopeKey }).Records);
        }

        [Fact]
        public void Unnamed_models_with_issues_are_retained_and_use_captured_titles_when_available()
        {
            var store = new FailureLedgerStore(Db);
            var named = Model(store, "named_incident", " ");
            var unnamed = Model(store, "unnamed_incident", "");
            Model(store, "no_incident", "");
            Incident(store, named, Operation(store, named, 2026), title: "Captured model");
            Incident(store, unnamed, Operation(store, unnamed, 2027));
            var rows = store.GetIssueModels("open");
            Assert.Equal(2, rows.Count);
            Assert.Equal("Captured model", rows.Single(r => r.ModelScopeKey == named.ModelScopeKey).Title);
            Assert.Null(rows.Single(r => r.ModelScopeKey == unnamed.ModelScopeKey).Title);
        }

        [Fact]
        public void Counts_are_issue_counts_and_are_not_limited_by_the_500_row_UI_cap()
        {
            var store = new FailureLedgerStore(Db);
            var model = Model(store, "large", "Large issue list", false);
            var operation = Operation(store, model, 2026);
            for (var index = 0; index < 505; index++) Incident(store, model, operation, messageCount: 7);
            Assert.Equal(505, Assert.Single(store.GetIssueModels("open")).IssueCount);
            var page = store.Query(new FailureRecordQuery { ModelScopeKey = model.ModelScopeKey, RevitYear = 2026, Limit = 100 });
            Assert.Equal(100, page.Records.Count);
            Assert.NotNull(page.NextCursor);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
}
