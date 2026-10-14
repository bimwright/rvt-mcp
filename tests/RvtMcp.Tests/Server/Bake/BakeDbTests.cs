using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RvtMcp.Server.Bake;
using RvtMcp.Plugin.ToolBaker;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests
{
    public class BakeDbTests
    {
        [Fact]
        public void Migrate_creates_required_schema()
        {
            using var sandbox = new TempDir();
            var dbPath = Path.Combine(sandbox.Path, "bake.db");
            using var db = new BakeDb(dbPath);

            db.Migrate();

            using var connection = OpenRead(dbPath);
            Assert.Equal(new[] { "registry", "schema_version", "suggestions", "usage_events" }, TableNames(connection));
            Assert.Equal(
                new[]
                {
                    "body_hash", "id", "normalized_key", "payload_json", "source", "success", "tool", "ts_utc"
                },
                ColumnNames(connection, "usage_events"));
            Assert.Equal(
                new[]
                {
                    "cluster_key", "created_at", "description", "id", "never_reason", "payload_json", "score",
                    "snooze_until", "source", "state", "title", "updated_at", "version_history_blob"
                },
                ColumnNames(connection, "suggestions"));
            Assert.Equal(
                new[]
                {
                    "compat_map", "created_at", "created_from_suggestion_id", "description", "dll_bytes",
                    "failure_rate", "last_used_at", "lifecycle_state", "name", "params_schema", "reviewed_by_user", "source",
                    "source_code", "usage_count", "version_history_blob"
                },
                ColumnNames(connection, "registry"));
            Assert.Equal(1L, Scalar<long>(connection, "SELECT version FROM schema_version"));
        }

        [Fact]
        public void Migrate_is_idempotent()
        {
            using var sandbox = new TempDir();
            var dbPath = Path.Combine(sandbox.Path, "bake.db");
            using var db = new BakeDb(dbPath);

            db.Migrate();
            db.Migrate();

            using var connection = OpenRead(dbPath);
            Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM schema_version"));
        }

        [Fact]
        public async Task Read_registry_allows_concurrent_usage_write()
        {
            using var sandbox = new TempDir();
            var dbPath = Path.Combine(sandbox.Path, "bake.db");
            using var db = new BakeDb(dbPath);
            db.Migrate();
            Assert.True(db.TryInsertRegistryRecord(new BakedToolRecord
            {
                Name = "legacy_tool",
                Description = "Imported",
                Source = "legacy_pre_v0.3",
                ParamsSchema = "{}",
                CompatMap = "{}",
                DllBytes = new byte[] { 1, 2, 3 },
                SourceCode = "source",
                ReviewedByUser = true,
                CreatedAt = DateTime.UtcNow.ToString("o"),
                FailureRate = 0,
                VersionHistoryBlob = "{}"
            }));

            using var reader = OpenRead(dbPath);
            using var command = reader.CreateCommand();
            command.CommandText = "SELECT name FROM registry";
            using var result = command.ExecuteReader();
            Assert.True(result.Read());

            await Task.Run(() => db.InsertUsageEvent(
                id: Guid.NewGuid().ToString("N"),
                tsUtc: DateTime.UtcNow.ToString("o"),
                source: "test",
                tool: "legacy_tool",
                normalizedKey: "legacy_tool:{}",
                payloadJson: "{}",
                bodyHash: null,
                success: true));

            Assert.Equal("legacy_tool", result.GetString(0));
        }

        [Fact]
        public void UpsertSuggestion_updates_existing_cluster_key_instead_of_creating_duplicate()
        {
            using var sandbox = new TempDir();
            var dbPath = Path.Combine(sandbox.Path, "bake.db");
            using var db = new BakeDb(dbPath);
            db.Migrate();

            db.UpsertSuggestion(Suggestion("s1", "preset:create_level:elevation,name", "Original"));
            db.UpsertSuggestion(Suggestion("s2", "preset:create_level:elevation,name", "Updated"));

            var stored = Assert.Single(db.ListSuggestions());
            Assert.Equal("s1", stored.Id);
            Assert.Equal("Updated", stored.Title);
            Assert.Equal("preset:create_level:elevation,name", stored.ClusterKey);
        }

        [Fact]
        public void Migrate_deduplicates_suggestions_without_discarding_user_state()
        {
            using var sandbox = new TempDir();
            var dbPath = Path.Combine(sandbox.Path, "bake.db");
            CreateLegacyDuplicateSuggestionDb(dbPath);
            using var db = new BakeDb(dbPath);

            db.Migrate();

            var stored = Assert.Single(db.ListSuggestions());
            Assert.Equal("never-row", stored.Id);
            Assert.Equal("never", stored.State);
            Assert.Equal("user", stored.NeverReason);
            Assert.Equal(@"[{""event"":""user_never""}]", stored.VersionHistoryBlob);
        }

        [Fact]
        public void RecordRegistryRun_updates_success_and_failure_stats()
        {
            using var sandbox = new TempDir();
            var dbPath = Path.Combine(sandbox.Path, "bake.db");
            using var db = new BakeDb(dbPath);
            db.Migrate();
            Assert.True(db.TryInsertRegistryRecord(RegistryRecord("accepted_tool")));

            var first = DateTimeOffset.Parse("2026-04-27T01:00:00Z");
            var second = DateTimeOffset.Parse("2026-04-27T02:00:00Z");
            Assert.True(db.TryRecordRegistryRun("accepted_tool", "2026", success: true, error: null, now: first));
            Assert.True(db.TryRecordRegistryRun("accepted_tool", "2027", success: false, error: "Compile failed", now: second));

            var record = Assert.Single(db.ReadRegistryRecords());
            Assert.Equal(1, record.UsageCount);
            Assert.Equal(second.ToString("o"), record.LastUsedAt);
            Assert.Equal(0.5, record.FailureRate, precision: 3);

            var compat = JObject.Parse(record.CompatMap);
            Assert.True((bool)compat["tested"]!["2026"]!["ok"]!);
            Assert.False((bool)compat["tested"]!["2027"]!["ok"]!);
            Assert.Equal("Compile failed", (string)compat["tested"]!["2027"]!["last_error"]);

            var history = JArray.Parse(record.VersionHistoryBlob);
            Assert.Equal(new[] { "run_succeeded", "run_failed" }, history.Select(e => (string)e!["event"]).ToArray());
        }

        private static SqliteConnection OpenRead(string dbPath)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout=5000;";
            command.ExecuteNonQuery();
            return connection;
        }

        private static string[] TableNames(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var reader = command.ExecuteReader();
            return ReadStrings(reader).ToArray();
        }

        private static string[] ColumnNames(SqliteConnection connection, string table)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM pragma_table_info($table) ORDER BY name";
            command.Parameters.AddWithValue("$table", table);
            using var reader = command.ExecuteReader();
            return ReadStrings(reader).ToArray();
        }

        private static T Scalar<T>(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (T)Convert.ChangeType(command.ExecuteScalar(), typeof(T));
        }

        private static void CreateLegacyDuplicateSuggestionDb(string dbPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath));
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = @"
CREATE TABLE suggestions(
    id TEXT PRIMARY KEY,
    cluster_key TEXT NOT NULL,
    source TEXT NOT NULL,
    title TEXT NOT NULL,
    description TEXT NOT NULL,
    state TEXT NOT NULL,
    score REAL NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    snooze_until TEXT NULL,
    never_reason TEXT NULL,
    payload_json TEXT NOT NULL,
    version_history_blob TEXT NOT NULL
)";
            create.ExecuteNonQuery();
            InsertLegacySuggestion(connection, "open-row", "open", "2026-04-27T00:00:00.0000000+00:00", null, "[]");
            InsertLegacySuggestion(connection, "never-row", "never", "2026-04-27T01:00:00.0000000+00:00", "user", @"[{""event"":""user_never""}]");
        }

        private static void InsertLegacySuggestion(
            SqliteConnection connection,
            string id,
            string state,
            string updatedAt,
            string neverReason,
            string versionHistory)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO suggestions(
    id, cluster_key, source, title, description, state, score, created_at, updated_at,
    snooze_until, never_reason, payload_json, version_history_blob
) VALUES (
    $id, 'preset:create_level:elevation,name', 'preset', $title, 'Suggestion', $state, 1,
    '2026-04-27T00:00:00.0000000+00:00', $updated_at, NULL, $never_reason, '{}', $version_history
)";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$title", id);
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$updated_at", updatedAt);
            command.Parameters.AddWithValue("$never_reason", string.IsNullOrEmpty(neverReason) ? DBNull.Value : neverReason);
            command.Parameters.AddWithValue("$version_history", versionHistory);
            command.ExecuteNonQuery();
        }

        private static System.Collections.Generic.IEnumerable<string> ReadStrings(SqliteDataReader reader)
        {
            while (reader.Read())
                yield return reader.GetString(0);
        }

        private static BakeSuggestionRecord Suggestion(string id, string clusterKey, string title)
        {
            return new BakeSuggestionRecord
            {
                Id = id,
                ClusterKey = clusterKey,
                Source = "preset",
                Title = title,
                Description = "Suggestion",
                State = "open",
                Score = 1,
                CreatedAt = "2026-04-27T00:00:00.0000000+00:00",
                UpdatedAt = "2026-04-27T00:00:00.0000000+00:00",
                PayloadJson = "{}",
                VersionHistoryBlob = "[]"
            };
        }

        private static BakedToolRecord RegistryRecord(string name)
        {
            return new BakedToolRecord
            {
                Name = name,
                Description = "Accepted",
                Source = "send_code",
                ParamsSchema = "{}",
                CompatMap = @"{""origin"":""2026"",""tested"":{}}",
                ReviewedByUser = true,
                CreatedAt = "2026-04-27T00:00:00.0000000+00:00",
                FailureRate = 0,
                VersionHistoryBlob = "[]",
                LifecycleState = "accepted"
            };
        }

        private sealed class TempDir : IDisposable
        {
            public TempDir()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bimwright-bakedb-test-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public void Dispose()
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
        }
    }
}
