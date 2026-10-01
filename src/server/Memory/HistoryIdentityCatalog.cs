using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;

namespace RvtMcp.Server.Memory
{
    // Logical links only: model databases and their call/reason ownership never move.
    internal sealed class HistoryIdentityCatalog
    {
        private readonly string _path;
        public HistoryIdentityCatalog(string projects) { _path = Path.Combine(projects, ".history-identities.db"); }
        private SqliteConnection Open(bool write)
        {
            if (!write && !File.Exists(_path)) return null;
            if (write) Directory.CreateDirectory(Path.GetDirectoryName(_path));
            var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = _path, Mode = write ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString());
            try
            {
                db.Open();
                if (write) Execute(db, null, @"
CREATE TABLE IF NOT EXISTS models(model_key TEXT PRIMARY KEY,physical_key TEXT,lineage_key TEXT,title TEXT,first_seen TEXT NOT NULL,last_seen TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS models_physical ON models(physical_key);
CREATE INDEX IF NOT EXISTS models_lineage ON models(lineage_key);
CREATE TABLE IF NOT EXISTS links(model_key TEXT PRIMARY KEY,source_key TEXT NOT NULL,mode TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS decisions(id TEXT PRIMARY KEY,model_key TEXT NOT NULL,source_key TEXT NOT NULL,mode TEXT NOT NULL,reason TEXT NOT NULL,at TEXT NOT NULL);");
                return db;
            }
            catch { db.Dispose(); throw; }
        }
        public void Observe(JObject model, string at)
        {
            using var db = Open(true); using var tx = db.BeginTransaction();
            Put(db, tx, model, at);
            var key = Key(model);
            if (Link(db, tx, key) == null)
            {
                var resolved = Resolve(db, tx, model);
                if (resolved.Value<string>("status") == "same_file" && resolved.Value<string>("rootKey") != key)
                    SetLink(db, tx, key, resolved.Value<string>("rootKey"), "same_file", "Matched physical file identity.");
            }
            tx.Commit();
        }
        public JObject Resolve(JObject model)
        {
            Key(model);
            using var db = Open(false);
            using var tx = db?.BeginTransaction(deferred: true);
            return Resolve(db, tx, model);
        }
        public JObject Decide(JObject model, JObject source, string mode, string reason)
        {
            var key = Key(model); var sourceKey = Key(source);
            if (key == sourceKey) throw new ArgumentException("Select a different source history key.");
            if (mode != "continue" && mode != "separate") throw new ArgumentException("decision must be continue or separate.");
            if (string.IsNullOrWhiteSpace(reason) || reason.Length > 4096) throw new ArgumentException("Supply the owner's decision reason (1-4096 characters).");
            using var db = Open(true); using var tx = db.BeginTransaction();
            var at = DateTime.UtcNow.ToString("o");
            Put(db, tx, source, at); Put(db, tx, model, at);
            var current = Model(db, tx, key); var previous = Model(db, tx, sourceKey);
            if (mode == "separate" && current.Value<string>("physicalKey") != null
                && current.Value<string>("physicalKey") == previous.Value<string>("physicalKey"))
                throw new ArgumentException("These paths identify the same file. Create an independent file before choosing separate.");
            if (mode == "continue") Root(db, tx, sourceKey, forbidden: key);
            SetLink(db, tx, key, sourceKey, mode, BakeRedactor.RedactForBake(reason));
            var result = Resolve(db, tx, model);
            tx.Commit(); return result;
        }
        private static JObject Resolve(SqliteConnection db, SqliteTransaction tx, JObject model)
        {
            var key = Key(model); var stored = Model(db, tx, key); var link = Link(db, tx, key);
            var physical = model.Property("physicalKey") != null ? model.Value<string>("physicalKey") : stored?.Value<string>("physicalKey");
            var lineage = model.Property("lineageKey") != null ? model.Value<string>("lineageKey") : stored?.Value<string>("lineageKey");
            var root = Root(db, tx, key); var status = link?.Value<string>("mode") ?? (stored == null ? "unregistered" : "known");
            var candidates = new JArray();
            if (db != null && link == null && physical != null)
            {
                using var match = Command(db, tx, "SELECT model_key FROM models WHERE physical_key=$physical AND model_key<>$key AND ($lineage IS NULL OR lineage_key IS NULL OR lineage_key=$lineage) ORDER BY first_seen,model_key LIMIT 1",
                    ("$physical", physical), ("$key", key), ("$lineage", lineage));
                var same = match.ExecuteScalar() as string;
                if (same != null) { root = Root(db, tx, same); status = "same_file"; }
            }
            var keys = Members(db, tx, root);
            if (!keys.Contains(key)) keys.Add(key);
            if (keys.Count > 64) throw new InvalidOperationException("History identity group exceeds 64 members.");
            if (db != null && link == null && status != "same_file" && lineage != null)
            {
                using var query = Command(db, tx, "SELECT model_key,title FROM models WHERE lineage_key=$lineage AND model_key<>$key AND ($seen IS NULL OR first_seen<$seen) ORDER BY first_seen,model_key LIMIT 21",
                    ("$lineage", lineage), ("$key", key), ("$seen", stored?.Value<string>("firstSeen")));
                using var reader = query.ExecuteReader();
                while (reader.Read())
                    if (!keys.Contains(reader.GetString(0))) candidates.Add(new JObject
                    { ["modelKey"] = reader.GetString(0), ["title"] = reader.IsDBNull(1) ? null : reader.GetString(1) });
                if (candidates.Count > 0) status = "needs_choice";
            }
            var more = candidates.Count > 20; if (more) candidates.RemoveAt(20);
            return new JObject { ["status"] = status, ["modelKey"] = key, ["rootKey"] = root,
                ["historyKeys"] = new JArray(keys), ["candidates"] = candidates, ["moreCandidates"] = more,
                ["note"] = status == "needs_choice" ? "Matching lineage is not proof of the same model. Ask the owner to continue or keep an independent history, then use revit_resolve_history_identity."
                    : status == "unregistered" ? "No registered file identity. Legacy history can be listed and linked explicitly; do not infer that missing calls mean no prior history."
                    : "History links do not move calls or assert that changes were saved." };
        }
        private static List<string> Members(SqliteConnection db, SqliteTransaction tx, string root)
        {
            if (db == null) return new List<string> { root };
            using var command = Command(db, tx, @"WITH RECURSIVE members(k) AS (
SELECT $root UNION SELECT l.model_key FROM links l JOIN members m ON l.source_key=m.k WHERE l.mode IN ('continue','same_file'))
SELECT k FROM members LIMIT 65", ("$root", root));
            using var reader = command.ExecuteReader(); var keys = new List<string>();
            while (reader.Read()) keys.Add(reader.GetString(0));
            if (keys.Count > 64) throw new InvalidOperationException("History identity group exceeds 64 members.");
            return keys;
        }
        private static string Root(SqliteConnection db, SqliteTransaction tx, string key, string forbidden = null)
        {
            var visited = new HashSet<string>();
            while (true)
            {
                if (key == forbidden || !visited.Add(key) || visited.Count > 64)
                    throw new ArgumentException("History continuation would create a cycle or exceed 64 members.");
                var link = Link(db, tx, key);
                if (link == null || link.Value<string>("mode") == "separate") return key;
                key = link.Value<string>("sourceKey");
            }
        }
        private static JObject Model(SqliteConnection db, SqliteTransaction tx, string key)
        {
            if (db == null) return null;
            using var command = Command(db, tx, "SELECT physical_key,lineage_key,title,first_seen FROM models WHERE model_key=$key", ("$key", key));
            using var reader = command.ExecuteReader();
            return !reader.Read() ? null : new JObject { ["key"] = key, ["physicalKey"] = reader.IsDBNull(0) ? null : reader.GetString(0),
                ["lineageKey"] = reader.IsDBNull(1) ? null : reader.GetString(1), ["title"] = reader.IsDBNull(2) ? null : reader.GetString(2), ["firstSeen"] = reader.GetString(3) };
        }
        private static JObject Link(SqliteConnection db, SqliteTransaction tx, string key)
        {
            if (db == null) return null;
            using var command = Command(db, tx, "SELECT source_key,mode FROM links WHERE model_key=$key", ("$key", key));
            using var reader = command.ExecuteReader();
            return !reader.Read() ? null : new JObject { ["sourceKey"] = reader.GetString(0), ["mode"] = reader.GetString(1) };
        }
        private static void Put(SqliteConnection db, SqliteTransaction tx, JObject model, string at)
        {
            var title = model.Value<string>("title") ?? "";
            Execute(db, tx, @"INSERT INTO models VALUES($key,$physical,$lineage,$title,$first,$at)
ON CONFLICT(model_key) DO UPDATE SET physical_key=COALESCE(excluded.physical_key,models.physical_key),lineage_key=COALESCE(excluded.lineage_key,models.lineage_key),title=excluded.title,last_seen=excluded.last_seen WHERE excluded.last_seen>=models.last_seen",
                ("$key", Key(model)), ("$physical", Fingerprint(model, "physicalKey")), ("$lineage", Fingerprint(model, "lineageKey")),
                ("$title", BakeRedactor.RedactForBake(title.Substring(0, Math.Min(256, title.Length)))),
                ("$first", DateTime.UtcNow.ToString("o")), ("$at", at));
        }
        private static string Fingerprint(JObject model, string property)
        {
            var value = model.Value<string>(property);
            if (value?.Length > 128) throw new ArgumentException("Invalid history fingerprint.");
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        private static string Key(JObject model)
        {
            var key = model?.Value<string>("key");
            if (!ChangeHistoryStore.IsModelKey(key)) throw new ArgumentException("A server-issued modelKey is required.");
            return key;
        }
        private static void SetLink(SqliteConnection db, SqliteTransaction tx, string key, string source, string mode, string reason)
        {
            Execute(db, tx, "INSERT INTO links VALUES($key,$source,$mode) ON CONFLICT(model_key) DO UPDATE SET source_key=excluded.source_key,mode=excluded.mode", ("$key", key), ("$source", source), ("$mode", mode));
            Execute(db, tx, "INSERT INTO decisions VALUES($id,$key,$source,$mode,$reason,$at)", ("$id", Guid.NewGuid().ToString("N")), ("$key", key), ("$source", source), ("$mode", mode), ("$reason", reason), ("$at", DateTime.UtcNow.ToString("o")));
        }
        private static SqliteCommand Command(SqliteConnection db, SqliteTransaction tx, string sql, params (string, object)[] args)
        {
            var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
            foreach (var arg in args) command.Parameters.AddWithValue(arg.Item1, arg.Item2 ?? DBNull.Value);
            return command;
        }
        private static void Execute(SqliteConnection db, SqliteTransaction tx, string sql, params (string, object)[] args)
        { using var command = Command(db, tx, sql, args); command.ExecuteNonQuery(); }
    }
}
