using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;

namespace RvtMcp.Server.Memory
{
    /// <summary>Server-owned local history. Public results are constructed explicitly, never exported DB rows.</summary>
    public sealed class ChangeHistoryStore
    {
        private readonly string _projects;
        private readonly HistoryIdentityCatalog _identities;
        public ChangeHistoryTransfer Transfer { get; }
        public ChangeHistoryStore(string dataRoot = null)
        {
            dataRoot ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bimwright", "rvt-mcp");
            _projects = Path.Combine(dataRoot, "projects");
            _identities = new HistoryIdentityCatalog(_projects);
            Transfer = new ChangeHistoryTransfer(dataRoot);
        }
        public static bool IsModelKey(string key) => key?.Length == 64 && key.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
        private string DatabasePath(string key)
        {
            if (!IsModelKey(key)) throw new ArgumentException("A server-issued modelKey is required.");
            return Path.Combine(_projects, key + ".db");
        }
        private SqliteConnection Open(string key, bool create)
        {
            var path = DatabasePath(key);
            if (create) Directory.CreateDirectory(_projects);
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly, DefaultTimeout = 5, Pooling = false }.ToString());
            try
            {
                connection.Open();
                Execute(connection, null, "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;");
                if (create)
                {
                    Execute(connection, null, "PRAGMA journal_mode=WAL;");
                    Execute(connection, null, @"
CREATE TABLE IF NOT EXISTS model_info(model_key TEXT PRIMARY KEY,identity TEXT NOT NULL,metadata_json TEXT NOT NULL,first_seen TEXT NOT NULL,last_seen TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS change_records(record_id TEXT PRIMARY KEY,at TEXT NOT NULL,user_name TEXT NOT NULL,machine TEXT NOT NULL,reason_known INTEGER NOT NULL,context_json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS change_calls(call_id TEXT PRIMARY KEY,record_id TEXT REFERENCES change_records(record_id),at TEXT NOT NULL,tool TEXT NOT NULL,session TEXT NOT NULL,success INTEGER NOT NULL,revit_year TEXT,user_name TEXT,machine TEXT,complete INTEGER NOT NULL,transactions_json TEXT NOT NULL,element_count INTEGER NOT NULL,capture_hash TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS change_call_elements(call_id TEXT NOT NULL REFERENCES change_calls(call_id),element_id INTEGER NOT NULL,unique_id TEXT,kind TEXT NOT NULL,category TEXT,before_json TEXT,after_json TEXT,PRIMARY KEY(call_id,element_id));
CREATE INDEX IF NOT EXISTS change_elements_id ON change_call_elements(element_id);
CREATE INDEX IF NOT EXISTS change_elements_uid ON change_call_elements(unique_id);
CREATE INDEX IF NOT EXISTS change_calls_date ON change_calls(at);");
                }
                return connection;
            }
            catch { connection.Dispose(); throw; }
        }

        public JObject Consume(string id)
        {
            var payload = Transfer.Read(id);
            return Consume(id, payload);
        }
        private JObject Consume(string id, JObject payload)
        {
            var result = Ingest(payload);
            // Old captures may contain unresolvable documents. Preserve them once outside
            // the retry queue; re-reading cannot manufacture a stable identity.
            if ((payload["documents"] as JArray)?.OfType<JObject>().Any(d => !(d["model"] is JObject)) == true)
                Transfer.Quarantine(id);
            else Transfer.Remove(id);
            return result;
        }
        public void RecoverPending(CancellationToken cancellationToken = default, int maxFiles = 16, TimeSpan? minimumAge = null)
        {
            if (maxFiles < 1 || maxFiles > 16) throw new ArgumentOutOfRangeException(nameof(maxFiles));
            var timer = Stopwatch.StartNew();
            long bytes = 0;
            int visited = 0;
            try
            {
                foreach (var file in Transfer.PendingFiles())
                {
                    if (cancellationToken.IsCancellationRequested || visited++ >= maxFiles || timer.Elapsed > TimeSpan.FromSeconds(5)) break;
                    var id = Path.GetFileNameWithoutExtension(file.Name);
                    try
                    {
                        var age = DateTime.UtcNow - file.LastWriteTimeUtc;
                        if (age < (minimumAge ?? TimeSpan.Zero)) continue;
                        if (file.Length > ChangeHistoryTransfer.MaxBytes || age > TimeSpan.FromDays(7))
                        { Transfer.Quarantine(id); continue; }
                        bytes += file.Length;
                        if (bytes > 256L * 1024 * 1024) break;
                        // Do not steal receipts from a live gateway awaiting its response.
                        if (HistoryCallLease.IsActive(id)) continue;
                        var payload = Transfer.Read(id);
                        Consume(id, payload);
                    }
                    catch (FileNotFoundException) { } // A different consumer won the race.
                    catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is InvalidDataException || ex is InvalidOperationException)
                    {
                        HistoryDiagnostics.Report("history_quarantine", ex, id);
                        try { Transfer.Quarantine(id); }
                        catch (FileNotFoundException) { }
                        catch (Exception moveError) { HistoryDiagnostics.Report("history_quarantine_move", moveError, id); }
                    }
                    catch (Exception ex) { HistoryDiagnostics.Report("history_recovery_retry", ex, id); }
                }
            }
            catch (Exception ex) { HistoryDiagnostics.Report("history_recovery_scan", ex); }
        }
        public JObject Ingest(JObject payload)
        {
            var id = payload.Value<string>("callId");
            var session = payload.Value<string>("session");
            if (!ChangeHistoryTransfer.IsId(id) || !ChangeHistoryTransfer.IsId(session)) throw new ArgumentException("Invalid change capture identifiers.");
            var at = Timestamp(payload.Value<string>("at"));
            var docs = payload["documents"] as JArray ?? throw new ArgumentException("Invalid change capture documents.");
            if (docs.Count > 8) throw new ArgumentException("Too many changed documents.");
            var models = new JArray();
            var unresolved = payload.Value<int?>("skippedDocuments") ?? 0;
            foreach (var item in docs.OfType<JObject>())
            {
                var model = item["model"] as JObject;
                if (model == null) { unresolved++; continue; }
                var key = model.Value<string>("key");
                var elements = item["elements"] as JArray ?? new JArray();
                if (elements.Count > DocumentChangeAccumulator.TrackingLimit) throw new ArgumentException("Too many history elements.");
                using var db = Open(key, true);
                using var tx = db.BeginTransaction();
                var identity = model.Value<string>("identity");
                if (string.IsNullOrWhiteSpace(identity)) throw new ArgumentException("Model identity is unavailable.");
                var existingIdentity = Scalar(db, tx, "SELECT identity FROM model_info WHERE model_key=$key", ("$key", key)) as string;
                if (existingIdentity != null && existingIdentity != identity) throw new InvalidOperationException("Model identity mismatch.");
                Execute(db, tx, @"INSERT INTO model_info VALUES($key,$identity,$json,$at,$at)
ON CONFLICT(model_key) DO UPDATE SET metadata_json=$json,last_seen=$at;",
                    ("$key", key), ("$identity", identity), ("$json", model.ToString(Formatting.None)), ("$at", at));
                var hash = Hash(item.ToString(Formatting.None));
                var existingHash = Scalar(db, tx, "SELECT capture_hash FROM change_calls WHERE call_id=$id", ("$id", id)) as string;
                if (existingHash != null && existingHash != hash) throw new InvalidOperationException("Call identifier already has different captured changes.");
                var complete = payload.Value<bool?>("complete") == true && item["summary"]?.Value<string>("status") == "complete";
                if (existingHash == null)
                {
                    Execute(db, tx, @"INSERT INTO change_calls VALUES($id,NULL,$at,$tool,$session,$success,$year,$user,$machine,$complete,$transactions,$count,$hash)",
                        ("$id", id), ("$at", at), ("$tool", Text(payload["tool"])), ("$session", session), ("$success", payload.Value<bool>("success") ? 1 : 0),
                        ("$year", Text(payload["revitYear"])), ("$user", Text(payload["user"])), ("$machine", Text(payload["machine"])), ("$complete", complete ? 1 : 0),
                        ("$transactions", (item["summary"]?["transactions"] ?? new JArray()).ToString(Formatting.None)), ("$count", elements.Count), ("$hash", hash));
                    var updates = (docs.Count == 1 ? payload["parameterValues"]?["updated"] as JArray : null)?.OfType<JObject>()
                        .GroupBy(x => x.Value<long>("elementId")).ToDictionary(g => g.Key, g => g.First());
                    foreach (var element in elements.OfType<JObject>())
                    {
                        var elementId = element.Value<long>("elementId");
                        var kind = element.Value<string>("kind");
                        if (kind != "added" && kind != "modified" && kind != "deleted") throw new ArgumentException("Invalid captured change kind.");
                        JObject value = null;
                        if (kind == "modified" && updates != null) updates.TryGetValue(elementId, out value);
                        Execute(db, tx, "INSERT INTO change_call_elements VALUES($id,$element,$unique,$kind,$category,$before,$after)",
                            ("$id", id), ("$element", elementId), ("$unique", element.Value<string>("uniqueId")), ("$kind", kind), ("$category", Text(element["category"])),
                            ("$before", ParameterValue(payload["parameterValues"]?["parameter"], value, "old")),
                            ("$after", ParameterValue(payload["parameterValues"]?["parameter"], value, "new")));
                    }
                }
                tx.Commit();
                _identities.Observe(model, at);
                models.Add(new JObject { ["modelKey"] = key, ["title"] = Text(model["title"]), ["complete"] = complete,
                    ["elementCount"] = elements.Count, ["identity"] = _identities.Resolve(model) });
            }
            var active = payload["activeModel"] as JObject;
            return new JObject
            {
                ["status"] = unresolved > 0 ? "partial" : docs.Count == 0 ? "no_changes" : "recorded",
                ["callId"] = docs.Count > 0 ? id : null,
                ["modelKey"] = IsModelKey(active?.Value<string>("key")) ? active.Value<string>("key") : null,
                ["models"] = models,
                ["identityStatus"] = models.Any(m => m["identity"]?.Value<string>("status") == "needs_choice") ? "needs_choice" : "resolved",
                ["complete"] = payload.Value<bool?>("complete") == true && unresolved == 0,
                ["skippedDocuments"] = unresolved,
                ["note"] = unresolved > 0 ? "Some changed documents have no stable model identity and were skipped. Do not replay the model operation."
                    : "History records observed MCP changes, not Save/Sync or later manual Undo."
            };
        }

        public JObject Record(string modelKey, string[] callIds, JObject context)
        {
            if (callIds == null || callIds.Length == 0 || callIds.Length > 100 || callIds.Distinct().Count() != callIds.Length || callIds.Any(id => !ChangeHistoryTransfer.IsId(id)))
                throw new ArgumentException("Supply 1-100 distinct server-issued callIds.");
            if (!File.Exists(DatabasePath(modelKey))) throw new ArgumentException("No local history exists for this modelKey.");
            var allowed = new HashSet<string> { "request", "goal", "reason", "selectedOption", "alternatives", "survey", "remainingWork" };
            if (context == null || context.Properties().Any(p => !allowed.Contains(p.Name)) || context.ToString(Formatting.None).Length > 32768)
                throw new ArgumentException("Reason context is invalid or exceeds 32768 characters.");
            var safe = (JObject)Redact(context);
            safe["textTruncated"] = context.Descendants().OfType<JValue>().Any(x => x.Type == JTokenType.String && x.ToString().Length > 4096);
            var known = !string.IsNullOrWhiteSpace(safe.Value<string>("reason"));
            using var db = Open(modelKey, true);
            using var tx = db.BeginTransaction();
            foreach (var id in callIds)
            {
                using var command = Command(db, tx, "SELECT record_id FROM change_calls WHERE call_id=$id", ("$id", id));
                using var reader = command.ExecuteReader();
                if (!reader.Read()) throw new ArgumentException("A callId does not belong to this model's stored history.");
                if (!reader.IsDBNull(0)) throw new ArgumentException("A callId already has a reason record. Query it before making another record.");
            }
            var record = Guid.NewGuid().ToString("N");
            Execute(db, tx, "INSERT INTO change_records VALUES($id,$at,$user,$machine,$known,$context)",
                ("$id", record), ("$at", DateTime.UtcNow.ToString("o")), ("$user", Environment.UserName), ("$machine", Environment.MachineName),
                ("$known", known ? 1 : 0), ("$context", safe.ToString(Formatting.None)));
            foreach (var id in callIds) Execute(db, tx, "UPDATE change_calls SET record_id=$record WHERE call_id=$id AND record_id IS NULL", ("$record", record), ("$id", id));
            tx.Commit();
            return new JObject { ["recordId"] = record, ["modelKey"] = modelKey, ["callIds"] = new JArray(callIds), ["reasonKnown"] = known,
                ["note"] = known ? null : "Reason is unknown. Ask the user rather than inventing one." };
        }

        public JObject Query(string modelKey, long? elementId = null, string uniqueId = null, string from = null, string until = null, int limit = 50, JObject model = null)
        {
            if (model != null && model.Value<string>("key") != modelKey) throw new ArgumentException("History context key mismatch.");
            var identity = _identities.Resolve(model ?? new JObject { ["key"] = modelKey });
            var all = new List<JObject>(); var more = false;
            foreach (var key in identity["historyKeys"].Values<string>())
            {
                var part = QueryOne(key, elementId, uniqueId, from, until, limit);
                more |= part.Value<bool?>("hasMore") == true;
                foreach (var call in part["calls"].OfType<JObject>())
                { call["modelKey"] = key; all.Add(call); }
            }
            return new JObject { ["modelKey"] = modelKey, ["identity"] = identity,
                ["calls"] = new JArray(all.OrderByDescending(c => c.Value<string>("at"), StringComparer.Ordinal)
                    .ThenBy(c => c.Value<string>("modelKey"), StringComparer.Ordinal).ThenBy(c => c.Value<string>("callId"), StringComparer.Ordinal).Take(limit)),
                ["hasMore"] = more || all.Count > limit,
                ["note"] = "Local observed MCP history only. Use each call's modelKey to assign its reason. Missing entries do not prove an element was unchanged; history does not confirm Save/Sync or later Undo." };
        }

        public JObject ResolveIdentity(JObject model, string sourceModelKey, string decision, string reason)
        {
            var source = ReadModel(sourceModelKey);
            if (source == null) throw new ArgumentException("The source modelKey has no local history. List local models first.");
            return _identities.Decide(model, source, decision, reason);
        }

        private JObject ReadModel(string key)
        {
            if (!File.Exists(DatabasePath(key))) return null;
            using var db = Open(key, false);
            using var command = Command(db, null, "SELECT metadata_json FROM model_info WHERE model_key=$key", ("$key", key));
            return command.ExecuteScalar() is string json ? JObject.Parse(json) : null;
        }

        public JObject ListModels(string afterModelKey = null, int limit = 50)
        {
            if (limit < 1 || limit > 100 || afterModelKey != null && !IsModelKey(afterModelKey)) throw new ArgumentException("Invalid model list cursor or limit (1-100).");
            var keys = Directory.Exists(_projects) ? Directory.EnumerateFiles(_projects, "*.db")
                .Select(Path.GetFileNameWithoutExtension).Where(IsModelKey)
                .Where(k => afterModelKey == null || string.CompareOrdinal(k, afterModelKey) > 0).OrderBy(k => k, StringComparer.Ordinal).Take(limit + 1).ToArray() : Array.Empty<string>();
            var models = new JArray();
            foreach (var key in keys.Take(limit))
            {
                var model = ReadModel(key);
                if (model != null) models.Add(new JObject { ["modelKey"] = key, ["title"] = Text(model["title"]), ["identity"] = _identities.Resolve(model) });
            }
            return new JObject { ["models"] = models, ["nextAfterModelKey"] = keys.Length > limit ? keys[limit - 1] : null };
        }

        private JObject QueryOne(string modelKey, long? elementId, string uniqueId, string from, string until, int limit)
        {
            if (limit < 1 || limit > 100) throw new ArgumentException("limit must be between 1 and 100.");
            if (elementId <= 0 || uniqueId?.Length > 128) throw new ArgumentException("Invalid element identifier.");
            var start = from == null ? null : Timestamp(from);
            var end = until == null ? null : Timestamp(until);
            if (start != null && end != null && string.CompareOrdinal(start, end) > 0) throw new ArgumentException("from must not follow until.");
            var result = new JObject { ["modelKey"] = modelKey, ["calls"] = new JArray(), ["note"] = "Local observed MCP history only. Missing entries do not prove an element was unchanged; deleted UniqueIds and incomplete captures may be unavailable." };
            if (!File.Exists(DatabasePath(modelKey))) return result;
            using var db = Open(modelKey, false);
            using var command = Command(db, null, @"SELECT c.call_id,c.at,c.tool,c.success,c.complete,c.element_count,c.transactions_json,c.record_id,r.reason_known,r.context_json
FROM change_calls c LEFT JOIN change_records r ON c.record_id=r.record_id
WHERE ($from IS NULL OR c.at >= $from) AND ($until IS NULL OR c.at <= $until)
AND (($element IS NULL AND $unique IS NULL) OR EXISTS(SELECT 1 FROM change_call_elements e WHERE e.call_id=c.call_id AND ($element IS NULL OR e.element_id=$element) AND ($unique IS NULL OR e.unique_id=$unique)))
ORDER BY c.at DESC,c.call_id LIMIT $limit", ("$from", start), ("$until", end), ("$element", elementId), ("$unique", uniqueId), ("$limit", limit + 1));
            var calls = (JArray)result["calls"];
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    if (calls.Count == limit) { result["hasMore"] = true; break; }
                    calls.Add(new JObject { ["callId"] = reader.GetString(0), ["at"] = reader.GetString(1), ["tool"] = reader.GetString(2), ["success"] = reader.GetInt32(3) != 0,
                        ["complete"] = reader.GetInt32(4) != 0, ["elementCount"] = reader.GetInt32(5), ["transactions"] = JToken.Parse(reader.GetString(6)),
                        ["recordId"] = reader.IsDBNull(7) ? null : reader.GetString(7), ["reasonKnown"] = !reader.IsDBNull(8) && reader.GetInt32(8) != 0,
                        ["reasonStatus"] = reader.IsDBNull(7) ? "unassigned" : reader.GetInt32(8) == 0 ? "unknown" : "recorded",
                        ["context"] = reader.IsDBNull(9) ? null : JToken.Parse(reader.GetString(9)) });
                }
            foreach (var call in calls.OfType<JObject>())
            {
                using var elements = Command(db, null, @"SELECT element_id,unique_id,kind,category,before_json,after_json FROM change_call_elements
WHERE call_id=$call AND ($element IS NULL OR element_id=$element) AND ($unique IS NULL OR unique_id=$unique) ORDER BY element_id LIMIT 201",
                    ("$call", call.Value<string>("callId")), ("$element", elementId), ("$unique", uniqueId));
                var rows = new JArray();
                using var reader = elements.ExecuteReader();
                while (reader.Read())
                {
                    if (rows.Count == 200) { call["elementsTruncated"] = true; break; }
                    rows.Add(new JObject { ["elementId"] = reader.GetInt64(0), ["uniqueId"] = reader.IsDBNull(1) ? null : reader.GetString(1), ["kind"] = reader.GetString(2),
                        ["category"] = reader.IsDBNull(3) ? null : reader.GetString(3), ["before"] = reader.IsDBNull(4) ? null : JToken.Parse(reader.GetString(4)),
                        ["after"] = reader.IsDBNull(5) ? null : JToken.Parse(reader.GetString(5)) });
                }
                call["elements"] = rows;
            }
            return result;
        }

        private static string ParameterValue(JToken parameter, JObject value, string prefix)
        {
            if (value == null || value[prefix + "Value"] == null) return null;
            var original = new JObject { ["parameter"] = parameter?.DeepClone(), ["value"] = value[prefix + "Value"].DeepClone(), ["display"] = value[prefix + "DisplayValue"]?.DeepClone() };
            var safe = (JObject)Redact(original);
            safe["truncated"] = original.Descendants().OfType<JValue>().Any(x => x.Type == JTokenType.String && x.ToString().Length > 4096);
            return safe.ToString(Formatting.None);
        }
        private static JToken Redact(JToken value)
        {
            if (value is JObject obj)
            {
                var result = new JObject();
                foreach (var property in obj.Properties())
                {
                    var name = Text(new JValue(property.Name));
                    if (result.Property(name) != null) name += "#" + result.Count;
                    var secret = property.Name.Replace("_", "").Replace("-", "").ToLowerInvariant();
                    result[name] = secret == "password" || secret == "apikey" || secret == "authorization"
                        ? new JValue("[REDACTED]") : Redact(property.Value);
                }
                return result;
            }
            if (value is JArray array) return new JArray(array.Select(Redact));
            return value?.Type == JTokenType.String ? new JValue(Text(value)) : value?.DeepClone() ?? JValue.CreateNull();
        }
        private static string Text(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return null;
            var text = BakeRedactor.RedactForBake(value.ToString());
            return text.Length > 4096 ? text.Substring(0, 4096) : text;
        }
        private static string Timestamp(string value)
        {
            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)) throw new ArgumentException("Use a valid ISO date/time.");
            return time.UtcDateTime.ToString("o");
        }
        private static string Hash(string value)
        {
            using var hash = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value)));
        }
        private static SqliteCommand Command(SqliteConnection db, SqliteTransaction tx, string sql, params (string name, object value)[] values)
        {
            var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
            foreach (var value in values) command.Parameters.AddWithValue(value.name, value.value ?? DBNull.Value);
            return command;
        }
        private static int Execute(SqliteConnection db, SqliteTransaction tx, string sql, params (string name, object value)[] values)
        { using var command = Command(db, tx, sql, values); return command.ExecuteNonQuery(); }
        private static object Scalar(SqliteConnection db, SqliteTransaction tx, string sql, params (string name, object value)[] values)
        { using var command = Command(db, tx, sql, values); return command.ExecuteScalar(); }
    }
}
