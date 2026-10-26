using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RvtMcp.Plugin.ToolBaker;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server.Bake
{
    public sealed partial class BakeDb : IDisposable
    {
        private readonly string _dbPath;

        public BakeDb(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath))
                throw new ArgumentException("Bake database path is required.", nameof(dbPath));

            _dbPath = dbPath;
        }

        public BakeDb(BakePaths paths)
            : this(paths?.BakeDb)
        {
        }

        public string DbPath => _dbPath;

        public void Migrate()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath) ?? ".");
            using var connection = OpenModuleConnection(readOnly: false, create: true);
            ExecutePragma(connection, "PRAGMA journal_mode=WAL;");
            using var transaction = connection.BeginTransaction(deferred: false);
            var version = ReadSchemaVersion(connection, transaction);
            // Current, future (binary rollback) and inconsistent module schemas are left untouched so preset/macro
            // tables stay usable; CodeModuleStore validates the module schema on every access and refuses there.
            if (version >= ModuleSchemaVersion || HasModuleTables(connection, transaction))
            {
                transaction.Commit();
                return;
            }
            BackupBeforeModuleMigration(connection, transaction);

            Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS schema_version(version INTEGER NOT NULL)");
            Execute(connection, transaction, @"
CREATE TABLE IF NOT EXISTS usage_events(
    id TEXT PRIMARY KEY,
    ts_utc TEXT NOT NULL,
    source TEXT NOT NULL,
    tool TEXT NOT NULL,
    normalized_key TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    body_hash TEXT NULL,
    success INTEGER NOT NULL
)");
            Execute(connection, transaction, @"
CREATE TABLE IF NOT EXISTS suggestions(
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
)");
            EnsureColumn(connection, transaction, "suggestions", "version_history_blob", "TEXT NOT NULL DEFAULT '[]'");
            DeduplicateSuggestionClusterKeys(connection, transaction);
            Execute(connection, transaction, "CREATE UNIQUE INDEX IF NOT EXISTS ux_suggestions_cluster_key ON suggestions(cluster_key)");
            Execute(connection, transaction, @"
CREATE TABLE IF NOT EXISTS registry(
    name TEXT PRIMARY KEY,
    description TEXT NOT NULL,
    source TEXT NOT NULL,
    params_schema TEXT NOT NULL,
    compat_map TEXT NOT NULL,
    dll_bytes BLOB NULL,
    source_code TEXT NULL,
    created_from_suggestion_id TEXT NULL,
    reviewed_by_user INTEGER NOT NULL,
    created_at TEXT NOT NULL,
    last_used_at TEXT NULL,
    usage_count INTEGER NOT NULL DEFAULT 0,
    failure_rate REAL NOT NULL,
    lifecycle_state TEXT NOT NULL DEFAULT 'accepted',
    version_history_blob TEXT NOT NULL
)");
            EnsureColumn(connection, transaction, "registry", "usage_count", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, transaction, "registry", "lifecycle_state", "TEXT NOT NULL DEFAULT 'accepted'");
            CreateModuleSchema(connection, transaction);
            Execute(connection, transaction, "DELETE FROM schema_version; INSERT INTO schema_version(version) VALUES(2)");
            transaction.Commit();
        }

        public bool TryInsertRegistryRecord(BakedToolRecord record)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));
            if (string.IsNullOrWhiteSpace(record.Name))
                throw new ArgumentException("Baked tool name is required.", nameof(record));

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT OR IGNORE INTO registry(
    name,
    description,
    source,
    params_schema,
    compat_map,
    dll_bytes,
    source_code,
    created_from_suggestion_id,
    reviewed_by_user,
    created_at,
    last_used_at,
    usage_count,
    failure_rate,
    lifecycle_state,
    version_history_blob
) VALUES (
    $name,
    $description,
    $source,
    $params_schema,
    $compat_map,
    $dll_bytes,
    $source_code,
    $created_from_suggestion_id,
    $reviewed_by_user,
    $created_at,
    $last_used_at,
    $usage_count,
    $failure_rate,
    $lifecycle_state,
    $version_history_blob
)";
            AddText(command, "$name", record.Name);
            AddText(command, "$description", record.Description ?? string.Empty);
            AddText(command, "$source", record.Source ?? string.Empty);
            AddText(command, "$params_schema", string.IsNullOrWhiteSpace(record.ParamsSchema) ? "{}" : record.ParamsSchema);
            AddText(command, "$compat_map", string.IsNullOrWhiteSpace(record.CompatMap) ? "{}" : record.CompatMap);
            command.Parameters.Add("$dll_bytes", SqliteType.Blob).Value = record.DllBytes == null ? DBNull.Value : record.DllBytes;
            AddNullableText(command, "$source_code", record.SourceCode);
            AddNullableText(command, "$created_from_suggestion_id", record.CreatedFromSuggestionId);
            command.Parameters.AddWithValue("$reviewed_by_user", record.ReviewedByUser ? 1 : 0);
            AddText(command, "$created_at", string.IsNullOrWhiteSpace(record.CreatedAt) ? DateTime.UtcNow.ToString("o") : record.CreatedAt);
            AddNullableText(command, "$last_used_at", record.LastUsedAt);
            command.Parameters.AddWithValue("$usage_count", Math.Max(0, record.UsageCount));
            command.Parameters.AddWithValue("$failure_rate", record.FailureRate);
            AddText(command, "$lifecycle_state", string.IsNullOrWhiteSpace(record.LifecycleState) ? "accepted" : record.LifecycleState);
            AddText(command, "$version_history_blob", string.IsNullOrWhiteSpace(record.VersionHistoryBlob) ? "{}" : record.VersionHistoryBlob);
            return command.ExecuteNonQuery() == 1;
        }

        public void InsertUsageEvent(
            string id,
            string tsUtc,
            string source,
            string tool,
            string normalizedKey,
            string payloadJson,
            string bodyHash,
            bool success)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO usage_events(id, ts_utc, source, tool, normalized_key, payload_json, body_hash, success)
VALUES($id, $ts_utc, $source, $tool, $normalized_key, $payload_json, $body_hash, $success)";
            AddText(command, "$id", id);
            AddText(command, "$ts_utc", tsUtc);
            AddText(command, "$source", source);
            AddText(command, "$tool", tool);
            AddText(command, "$normalized_key", normalizedKey);
            AddText(command, "$payload_json", payloadJson ?? "{}");
            AddNullableText(command, "$body_hash", bodyHash);
            command.Parameters.AddWithValue("$success", success ? 1 : 0);
            command.ExecuteNonQuery();
        }

        public bool UpsertSuggestion(BakeSuggestionRecord record)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));
            if (string.IsNullOrWhiteSpace(record.Id))
                throw new ArgumentException("Suggestion id is required.", nameof(record));
            if (string.IsNullOrWhiteSpace(record.ClusterKey))
                throw new ArgumentException("Suggestion cluster key is required.", nameof(record));
            if (!BakeSuggestionStates.IsValid(record.State))
                throw new ArgumentException("Unknown suggestion state: " + record.State, nameof(record));

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var existingId = FindExistingSuggestionId(connection, transaction, record.Id, record.ClusterKey);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            if (string.IsNullOrWhiteSpace(existingId))
            {
                command.CommandText = @"
INSERT INTO suggestions(
    id, cluster_key, source, title, description, state, score, created_at, updated_at,
    snooze_until, never_reason, payload_json, version_history_blob
) VALUES (
    $id, $cluster_key, $source, $title, $description, $state, $score, $created_at, $updated_at,
    $snooze_until, $never_reason, $payload_json, $version_history_blob
)";
            }
            else
            {
                record.Id = existingId;
                command.CommandText = @"
UPDATE suggestions SET
    cluster_key = $cluster_key,
    source = $source,
    title = $title,
    description = $description,
    state = $state,
    score = $score,
    created_at = $created_at,
    updated_at = $updated_at,
    snooze_until = $snooze_until,
    never_reason = $never_reason,
    payload_json = $payload_json,
    version_history_blob = $version_history_blob
WHERE id = $id";
            }
            BindSuggestion(command, record);
            var changed = command.ExecuteNonQuery() > 0;
            transaction.Commit();
            return changed;
        }

        public IReadOnlyList<BakeSuggestionRecord> ListSuggestions()
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT id, cluster_key, source, title, description, state, score, created_at, updated_at,
       snooze_until, never_reason, payload_json, version_history_blob
FROM suggestions
ORDER BY created_at DESC, id";
            using var reader = command.ExecuteReader();
            var records = new List<BakeSuggestionRecord>();
            while (reader.Read())
                records.Add(ReadSuggestion(reader));
            return records;
        }

        public BakeSuggestionRecord GetSuggestion(string id)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT id, cluster_key, source, title, description, state, score, created_at, updated_at,
       snooze_until, never_reason, payload_json, version_history_blob
FROM suggestions
WHERE id = $id";
            AddText(command, "$id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadSuggestion(reader) : null;
        }

        public IReadOnlyList<BakedToolRecord> ReadRegistryRecords()
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT name, description, source, params_schema, compat_map, dll_bytes, source_code,
       created_from_suggestion_id, reviewed_by_user, created_at, last_used_at, failure_rate,
       version_history_blob, usage_count, lifecycle_state
FROM registry
ORDER BY name";
            using var reader = command.ExecuteReader();
            var records = new List<BakedToolRecord>();
            while (reader.Read())
            {
                records.Add(new BakedToolRecord
                {
                    Name = reader.GetString(0),
                    Description = reader.GetString(1),
                    Source = reader.GetString(2),
                    ParamsSchema = reader.GetString(3),
                    CompatMap = reader.GetString(4),
                    DllBytes = reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5),
                    SourceCode = reader.IsDBNull(6) ? null : reader.GetString(6),
                    CreatedFromSuggestionId = reader.IsDBNull(7) ? null : reader.GetString(7),
                    ReviewedByUser = reader.GetInt64(8) != 0,
                    CreatedAt = reader.GetString(9),
                    LastUsedAt = reader.IsDBNull(10) ? null : reader.GetString(10),
                    FailureRate = reader.GetDouble(11),
                    VersionHistoryBlob = reader.GetString(12),
                    UsageCount = reader.GetInt32(13),
                    LifecycleState = reader.GetString(14),
                });
            }

            return records;
        }

        public bool TryRecordRegistryRun(string name, string revitVersion, bool success, string error, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            var record = ReadRegistryRecords().FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));
            if (record == null)
                return false;

            var history = ParseHistory(record.VersionHistoryBlob);
            history.Add(new JObject
            {
                ["event"] = success ? "run_succeeded" : "run_failed",
                ["revit_version"] = string.IsNullOrWhiteSpace(revitVersion) ? "unknown" : revitVersion,
                ["at"] = now.ToString("o"),
                ["error"] = success ? null : error ?? string.Empty
            });

            var compat = ParseObject(record.CompatMap);
            if (!(compat["tested"] is JObject tested))
            {
                tested = new JObject();
                compat["tested"] = tested;
            }

            var version = string.IsNullOrWhiteSpace(revitVersion) ? "unknown" : revitVersion;
            var compatEntry = new JObject
            {
                ["ok"] = success,
                ["last_run"] = now.ToString("o")
            };
            if (!success && !string.IsNullOrWhiteSpace(error))
                compatEntry["last_error"] = error;
            tested[version] = compatEntry;

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
UPDATE registry
SET last_used_at = $last_used_at,
    usage_count = usage_count + $usage_delta,
    failure_rate = $failure_rate,
    compat_map = $compat_map,
    version_history_blob = $version_history_blob
WHERE name = $name";
            AddText(command, "$name", name);
            AddText(command, "$last_used_at", now.ToString("o"));
            command.Parameters.AddWithValue("$usage_delta", success ? 1 : 0);
            command.Parameters.AddWithValue("$failure_rate", ComputeFailureRate(history));
            AddText(command, "$compat_map", compat.ToString(Formatting.None));
            AddText(command, "$version_history_blob", history.ToString(Formatting.None));
            return command.ExecuteNonQuery() == 1;
        }

        public bool TryUpdateRegistryVersionHistory(string name, string versionHistoryBlob)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
UPDATE registry
SET version_history_blob = $version_history_blob
WHERE name = $name";
            AddText(command, "$name", name);
            AddText(command, "$version_history_blob", string.IsNullOrWhiteSpace(versionHistoryBlob) ? "[]" : versionHistoryBlob);
            return command.ExecuteNonQuery() == 1;
        }

        public void Dispose()
        {
        }

        private SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = false,
            }.ToString());
            connection.Open();
            ExecutePragma(connection, "PRAGMA journal_mode=WAL;");
            ExecutePragma(connection, "PRAGMA busy_timeout=5000;");
            return connection;
        }

        private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static void EnsureColumn(SqliteConnection connection, SqliteTransaction transaction, string table, string column, string definition)
        {
            using var check = connection.CreateCommand();
            check.Transaction = transaction;
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column";
            check.Parameters.AddWithValue("$table", table);
            check.Parameters.AddWithValue("$column", column);
            var exists = Convert.ToInt64(check.ExecuteScalar()) != 0;
            if (exists)
                return;

            Execute(connection, transaction, $"ALTER TABLE {table} ADD COLUMN {column} {definition}");
        }

        private static void DeduplicateSuggestionClusterKeys(SqliteConnection connection, SqliteTransaction transaction)
        {
            var rows = new List<SuggestionDedupeRow>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT rowid, cluster_key, state, created_at, updated_at
FROM suggestions
ORDER BY rowid";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new SuggestionDedupeRow
                    {
                        RowId = reader.GetInt64(0),
                        ClusterKey = reader.GetString(1),
                        State = reader.GetString(2),
                        CreatedAt = reader.GetString(3),
                        UpdatedAt = reader.GetString(4),
                    });
                }
            }

            var deleteIds = rows
                .GroupBy(r => r.ClusterKey, StringComparer.Ordinal)
                .SelectMany(g =>
                {
                    var keep = g
                        .OrderBy(r => StatePriority(r.State))
                        .ThenByDescending(r => ParseTimestamp(r.UpdatedAt))
                        .ThenByDescending(r => ParseTimestamp(r.CreatedAt))
                        .ThenByDescending(r => r.RowId)
                        .First();
                    return g.Where(r => r.RowId != keep.RowId).Select(r => r.RowId);
                })
                .ToArray();

            if (deleteIds.Length == 0)
                return;

            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM suggestions WHERE rowid = $rowid";
            var rowIdParam = delete.Parameters.Add("$rowid", SqliteType.Integer);
            foreach (var rowId in deleteIds)
            {
                rowIdParam.Value = rowId;
                delete.ExecuteNonQuery();
            }
        }

        private static int StatePriority(string state)
        {
            switch (state)
            {
                case BakeSuggestionStates.Accepted:
                    return 0;
                case BakeSuggestionStates.Never:
                    return 1;
                case BakeSuggestionStates.Snoozed:
                    return 2;
                case BakeSuggestionStates.Superseded:
                    return 3;
                case BakeSuggestionStates.Open:
                    return 4;
                case BakeSuggestionStates.Archived:
                    return 5;
                default:
                    return 6;
            }
        }

        private static DateTimeOffset ParseTimestamp(string value)
        {
            return DateTimeOffset.TryParse(value, out var timestamp) ? timestamp : DateTimeOffset.MinValue;
        }

        private static JArray ParseHistory(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new JArray();
            try
            {
                return JArray.Parse(json);
            }
            catch (JsonException)
            {
                return new JArray();
            }
        }

        private static JObject ParseObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new JObject();
            try
            {
                return JObject.Parse(json);
            }
            catch (JsonException)
            {
                return new JObject();
            }
        }

        private static double ComputeFailureRate(JArray history)
        {
            var runEvents = history
                .OfType<JObject>()
                .Select(e => e.Value<string>("event"))
                .Where(e => e == "run_succeeded" || e == "run_failed")
                .ToArray();
            if (runEvents.Length == 0)
                return 0;

            return runEvents.Count(e => e == "run_failed") / (double)runEvents.Length;
        }

        private static string FindExistingSuggestionId(SqliteConnection connection, SqliteTransaction transaction, string id, string clusterKey)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT id
FROM suggestions
WHERE cluster_key = $cluster_key OR id = $id
ORDER BY CASE WHEN cluster_key = $cluster_key THEN 0 ELSE 1 END
LIMIT 1";
            AddText(command, "$id", id);
            AddText(command, "$cluster_key", clusterKey);
            return command.ExecuteScalar() as string;
        }

        private static void ExecutePragma(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static void AddText(SqliteCommand command, string name, string value)
        {
            command.Parameters.AddWithValue(name, value ?? string.Empty);
        }

        private static void AddNullableText(SqliteCommand command, string name, string value)
        {
            command.Parameters.AddWithValue(name, string.IsNullOrEmpty(value) ? DBNull.Value : value);
        }

        private static void BindSuggestion(SqliteCommand command, BakeSuggestionRecord record)
        {
            AddText(command, "$id", record.Id);
            AddText(command, "$cluster_key", record.ClusterKey);
            AddText(command, "$source", record.Source);
            AddText(command, "$title", record.Title);
            AddText(command, "$description", record.Description);
            AddText(command, "$state", record.State);
            command.Parameters.AddWithValue("$score", record.Score);
            AddText(command, "$created_at", string.IsNullOrWhiteSpace(record.CreatedAt) ? DateTimeOffset.UtcNow.ToString("o") : record.CreatedAt);
            AddText(command, "$updated_at", string.IsNullOrWhiteSpace(record.UpdatedAt) ? DateTimeOffset.UtcNow.ToString("o") : record.UpdatedAt);
            AddNullableText(command, "$snooze_until", record.SnoozeUntil);
            AddNullableText(command, "$never_reason", record.NeverReason);
            AddText(command, "$payload_json", string.IsNullOrWhiteSpace(record.PayloadJson) ? "{}" : record.PayloadJson);
            AddText(command, "$version_history_blob", string.IsNullOrWhiteSpace(record.VersionHistoryBlob) ? "[]" : record.VersionHistoryBlob);
        }

        private static BakeSuggestionRecord ReadSuggestion(SqliteDataReader reader)
        {
            return new BakeSuggestionRecord
            {
                Id = reader.GetString(0),
                ClusterKey = reader.GetString(1),
                Source = reader.GetString(2),
                Title = reader.GetString(3),
                Description = reader.GetString(4),
                State = reader.GetString(5),
                Score = reader.GetDouble(6),
                CreatedAt = reader.GetString(7),
                UpdatedAt = reader.GetString(8),
                SnoozeUntil = reader.IsDBNull(9) ? null : reader.GetString(9),
                NeverReason = reader.IsDBNull(10) ? null : reader.GetString(10),
                PayloadJson = reader.GetString(11),
                VersionHistoryBlob = reader.GetString(12),
            };
        }

        private sealed class SuggestionDedupeRow
        {
            public long RowId { get; set; }
            public string ClusterKey { get; set; }
            public string State { get; set; }
            public string CreatedAt { get; set; }
            public string UpdatedAt { get; set; }
        }
    }
}
