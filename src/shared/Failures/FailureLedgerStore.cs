using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Failures
{
    // The plugin is the only writer. Constructing/querying this store never initializes it.
    public sealed class FailureLedgerStore
    {
        private readonly bool _readOnly;
        private readonly HashSet<string> _uncertainScopes = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _sync = new object();
        /// <summary>Receives the type and message of a swallowed write failure. The plugin points it at its local debug log.</summary>
        public static Action<string> Diagnostic;
        public string DatabasePath { get; }
        public static string DefaultPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bimwright", "rvt-mcp", "failures", "ledger.db"); } }
        private string PendingPath { get { return Path.Combine(Path.GetDirectoryName(DatabasePath), "pending"); } }
        public FailureLedgerStore(string dbPath = null, bool readOnly = false)
        { DatabasePath = Path.GetFullPath(dbPath ?? DefaultPath); _readOnly = readOnly; }

        private void Writer() { if (_readOnly) throw new InvalidOperationException("Failure ledger is read-only."); }
        private SqliteConnection Open(bool write)
        {
            SQLitePCL.Batteries_V2.Init();
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = DatabasePath, Mode = write ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly,
                Pooling = false, DefaultTimeout = 1 }.ToString());
            connection.Open();
            Execute(connection, null, "PRAGMA busy_timeout=500; PRAGMA foreign_keys=ON;");
            return connection;
        }
        public void Initialize()
        {
            Writer(); Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath));
            using (var c = Open(true))
            {
                if (Convert.ToInt32(Scalar(c, null, "PRAGMA user_version;")) > 1) throw new InvalidOperationException("Unsupported failure ledger version.");
                Execute(c, null, "PRAGMA journal_mode=WAL;");
                Execute(c, null, @"
CREATE TABLE IF NOT EXISTS models(scope TEXT PRIMARY KEY,physical_key TEXT UNIQUE,model_key TEXT,title TEXT,transient INTEGER NOT NULL,revision INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS model_aliases(scope TEXT NOT NULL REFERENCES models(scope),model_key TEXT NOT NULL,PRIMARY KEY(scope,model_key));
CREATE TABLE IF NOT EXISTS physical_aliases(physical_key TEXT PRIMARY KEY,scope TEXT NOT NULL REFERENCES models(scope));
CREATE TABLE IF NOT EXISTS identity_candidates(scope TEXT NOT NULL REFERENCES models(scope),candidate_scope TEXT NOT NULL REFERENCES models(scope),PRIMARY KEY(scope,candidate_scope));
CREATE TABLE IF NOT EXISTS operations(id TEXT PRIMARY KEY,host TEXT,execution TEXT NOT NULL,outcome TEXT NOT NULL,review_required INTEGER NOT NULL DEFAULT 1,updated TEXT NOT NULL,payload TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS operation_scopes(operation_id TEXT NOT NULL REFERENCES operations(id),scope TEXT NOT NULL REFERENCES models(scope),PRIMARY KEY(operation_id,scope));
CREATE TABLE IF NOT EXISTS incidents(seq INTEGER PRIMARY KEY AUTOINCREMENT,id TEXT NOT NULL UNIQUE,operation_id TEXT NOT NULL REFERENCES operations(id),scope TEXT NOT NULL REFERENCES models(scope),state TEXT NOT NULL,created TEXT NOT NULL,payload TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_incidents_scope ON incidents(scope,seq);
CREATE TABLE IF NOT EXISTS failure_messages(incident_id TEXT NOT NULL REFERENCES incidents(id),sequence INTEGER NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(incident_id,sequence));
CREATE TABLE IF NOT EXISTS failure_elements(incident_id TEXT NOT NULL REFERENCES incidents(id),element_id INTEGER NOT NULL,PRIMARY KEY(incident_id,element_id));
CREATE INDEX IF NOT EXISTS ix_failure_elements ON failure_elements(element_id,incident_id);
CREATE TABLE IF NOT EXISTS decisions(id TEXT PRIMARY KEY,scope TEXT NOT NULL REFERENCES models(scope),incident_id TEXT,created TEXT NOT NULL,payload TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS operation_reviews(operation_id TEXT NOT NULL REFERENCES operations(id),scope TEXT NOT NULL REFERENCES models(scope),decision_id TEXT NOT NULL,PRIMARY KEY(operation_id,scope));
CREATE TABLE IF NOT EXISTS rules(id TEXT PRIMARY KEY,scope TEXT NOT NULL REFERENCES models(scope),enabled INTEGER NOT NULL,payload TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS grants(id TEXT PRIMARY KEY,scope TEXT NOT NULL REFERENCES models(scope),enabled INTEGER NOT NULL,consumed_operation TEXT,payload TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS recovered(id TEXT PRIMARY KEY);
PRAGMA user_version=1;");
                bool hasReview = false;
                using (var columns = Command(c, null, "PRAGMA table_info(operations);"))
                using (var reader = columns.ExecuteReader()) while (reader.Read()) if (reader.GetString(1) == "review_required") hasReview = true;
                if (!hasReview) Execute(c, null, "ALTER TABLE operations ADD COLUMN review_required INTEGER NOT NULL DEFAULT 1;");
            }
        }
        private void EnsureInitialized() { Writer(); if (!File.Exists(DatabasePath)) Initialize(); }

        public FailureModel ResolveModel(FailureModelIdentity identity)
        {
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            EnsureInitialized();
            var physical = identity.IsTransient ? null : identity.PhysicalKey;
            if (physical != null) FailureCapture.RequireKey(physical, "physical key");
            string scope;
            using (var c = Open(true))
            using (var tx = c.BeginTransaction())
            {
                if (physical == null && !string.IsNullOrEmpty(identity.KnownScopeKey))
                {
                    // Lost identity evidence must not turn a known unresolved model into a fresh scope.
                    FailureCapture.RequireKey(identity.KnownScopeKey, "known scope key");
                    var known = ReadModel(c, tx, identity.KnownScopeKey);
                    if (known != null)
                    {
                        if (known.IsTransient && !identity.IsTransient) LinkUncertainIdentity(c, tx, known.ModelScopeKey, identity);
                        tx.Commit(); return known;
                    }
                }
                if (physical != null)
                    scope = Scalar(c, tx, "SELECT scope FROM physical_aliases WHERE physical_key=$p;", "$p", physical) as string
                        ?? FailureCapture.Fingerprint("physical:" + physical);
                else
                {
                    FailureCapture.RequireKey(identity.HostInstanceId, "host instance");
                    FailureCapture.RequireKey(identity.LiveDocumentId, "live document");
                    scope = FailureCapture.Fingerprint("session:" + identity.HostInstanceId + ":" + identity.LiveDocumentId);
                }
                Execute(c, tx, "INSERT OR IGNORE INTO models(scope,physical_key,model_key,title,transient) VALUES($s,$p,$m,$t,$x);",
                    "$s", scope, "$p", physical, "$m", SafeKey(identity.ModelKey), "$t", FailureCapture.SafeText(identity.Title, 256), "$x", physical == null ? 1 : 0);
                Execute(c, tx, "UPDATE models SET title=$t,model_key=COALESCE($m,model_key) WHERE scope=$s;",
                    "$s", scope, "$m", SafeKey(identity.ModelKey), "$t", FailureCapture.SafeText(identity.Title, 256));
                if (!string.IsNullOrEmpty(identity.ModelKey))
                    Execute(c, tx, "INSERT OR IGNORE INTO model_aliases(scope,model_key) VALUES($s,$m);", "$s", scope, "$m", SafeKey(identity.ModelKey));
                if (physical == null && !identity.IsTransient) LinkUncertainIdentity(c, tx, scope, identity);
                var model = ReadModel(c, tx, scope); tx.Commit(); return model;
            }
        }
        private void LinkUncertainIdentity(SqliteConnection c, SqliteTransaction tx, string scope, FailureModelIdentity identity)
        {
            if (string.IsNullOrEmpty(identity.ModelKey)) return;
            var candidates = new List<string>();
            using (var cmd = Command(c, tx, @"SELECT DISTINCT a.scope FROM model_aliases a JOIN models m ON m.scope=a.scope
WHERE a.model_key=$k AND m.transient=0 AND a.scope!=$s;", "$k", identity.ModelKey, "$s", scope))
            using (var reader = cmd.ExecuteReader()) while (reader.Read()) candidates.Add(reader.GetString(0));
            foreach (var candidate in candidates)
                Execute(c, tx, "INSERT OR IGNORE INTO identity_candidates(scope,candidate_scope) VALUES($s,$c);", "$s", scope, "$c", candidate);
            var unresolved = candidates.Where(candidate => HasDatabaseBlocker(c, tx, candidate, null) || PendingFor(candidate) || IsUncertain(candidate)).ToList();
            if (unresolved.Count == 0) return;
            var operationId = FailureCapture.Fingerprint("identity-uncertain:" + scope);
            if (Scalar(c, tx, "SELECT 1 FROM operations WHERE id=$i;", "$i", operationId) != null) return;
            var operation = new FailureOperation { OperationId = operationId, HostInstanceId = identity.HostInstanceId, Tool = "identity_resolution",
                ModelScopeKeys = new List<string> { scope }, Execution = "completed", Outcome = "unknown", Complete = false, RequiresReview = true };
            WriteOperation(c, tx, operation);
            WriteIncident(c, tx, new FailureIncident { IncidentId = FailureCapture.Fingerprint("identity-incident:" + scope), OperationId = operationId,
                ModelScopeKey = scope, ModelTitle = FailureCapture.SafeText(identity.Title, 256), Source = "identity_resolution", Action = "identity_uncertain",
                State = "open", Complete = false, RelatedModelScopeKeys = unresolved,
                NativeContextDescription = "Physical file identity is unavailable. This document may match a model with unresolved work. Restore file identity or review the related model issues before continuing; no rules were inherited." });
        }
        private bool IsUncertain(string scope) { lock (_sync) return _uncertainScopes.Contains(scope); }
        private static List<string> CandidateScopes(SqliteConnection c, SqliteTransaction tx, string scope)
        {
            var scopes = new List<string> { scope };
            using (var cmd = Command(c, tx, "SELECT candidate_scope FROM identity_candidates WHERE scope=$s;", "$s", scope))
            using (var reader = cmd.ExecuteReader()) while (reader.Read()) scopes.Add(reader.GetString(0));
            return scopes;
        }
        private static List<string> InverseCandidateScopes(SqliteConnection c, SqliteTransaction tx, string scope)
        {
            var scopes = new List<string>();
            using (var cmd = Command(c, tx, "SELECT scope FROM identity_candidates WHERE candidate_scope=$s;", "$s", scope))
            using (var reader = cmd.ExecuteReader()) while (reader.Read()) scopes.Add(reader.GetString(0));
            return scopes;
        }
        private static List<string> ConflictingScopes(SqliteConnection c, SqliteTransaction tx, string scope)
        {
            // Ambiguity is not transitive identity. Two operations conflict when their possible-file sets overlap.
            // Q={K1,K2} conflicts with either file, but proven K1 and K2 remain independent of each other.
            // Candidate edges are always transient -> persistent, so inverse lookup finds every overlapping sibling.
            var possibleFiles = CandidateScopes(c, tx, scope);
            var conflicts = new HashSet<string>(possibleFiles, StringComparer.Ordinal);
            foreach (var file in possibleFiles)
                foreach (var possibleDocument in InverseCandidateScopes(c, tx, file)) conflicts.Add(possibleDocument);
            return conflicts.OrderBy(s => s, StringComparer.Ordinal).ToList();
        }
        // A complete Revit rollback left the model unchanged: it stays open for the user but does not gate the model.
        // Only uncertainty (unknown outcome, incomplete capture, storage or identity doubt) gates it.
        private const string CleanRollback = "(json_extract(payload,'$.action') IN ('rolled_back','cancelled') AND json_extract(payload,'$.complete')=1)";
        private const string BlockingIncident = "state='open' AND NOT " + CleanRollback;
        private static bool HasDatabaseBlocker(SqliteConnection c, SqliteTransaction tx, string scope, string excludedOperation, bool excludeDerivedIdentity = false)
        {
            return Scalar(c, tx, @"SELECT 1 FROM incidents WHERE scope=$s AND " + BlockingIncident + @" AND ($d IS NULL OR operation_id!=$d)
UNION SELECT 1 FROM operations o JOIN operation_scopes s ON s.operation_id=o.id
WHERE s.scope=$s AND ($x IS NULL OR o.id!=$x) AND (o.execution!='completed' OR o.review_required=1)
AND ($d IS NULL OR o.id!=$d)
AND NOT ((o.execution='completed' OR o.execution='interrupted_unknown') AND EXISTS
(SELECT 1 FROM operation_reviews r WHERE r.operation_id=o.id AND r.scope=s.scope)) LIMIT 1;", "$s", scope, "$x", excludedOperation,
                "$d", excludeDerivedIdentity ? FailureCapture.Fingerprint("identity-uncertain:" + scope) : null) != null;
        }
        private static string SafeKey(string value) { if (value != null) FailureCapture.RequireKey(value, "identifier"); return value; }
        // Only the native successful ordinary-save callback can supply same-live-document/same-path proof.
        // A SaveAs/copy must never call this method; a path or lineage match alone is not proof.
        public FailurePersistenceResult RecordPhysicalAlias(string existingScope, string newPhysicalKey)
        {
            FailureCapture.RequireKey(existingScope, "existing model scope"); FailureCapture.RequireKey(newPhysicalKey, "physical key");
            return Persist("physical_alias", new { ModelScopeKey = existingScope, PhysicalKey = newPhysicalKey },
                new[] { existingScope, FailureCapture.Fingerprint("physical:" + newPhysicalKey) },
                (c, tx) => WritePhysicalAlias(c, tx, existingScope, newPhysicalKey));
        }
        private static void WritePhysicalAlias(SqliteConnection c, SqliteTransaction tx, string scope, string physical)
        {
            var model = ReadModel(c, tx, scope);
            if (model == null || model.IsTransient) throw new InvalidOperationException("A persistent existing model is required for a physical alias.");
            var existing = Scalar(c, tx, "SELECT scope FROM models WHERE physical_key=$p UNION SELECT scope FROM physical_aliases WHERE physical_key=$p;", "$p", physical) as string;
            if (existing != null && existing != scope) throw new InvalidOperationException("Physical file already belongs to another model scope.");
            Execute(c, tx, "INSERT OR IGNORE INTO physical_aliases(physical_key,scope) VALUES($p,$s);", "$p", physical, "$s", scope);
            Execute(c, tx, "UPDATE models SET revision=revision+1 WHERE scope=$s;", "$s", scope);
        }
        private static FailureModel ReadModel(SqliteConnection c, SqliteTransaction tx, string scope)
        {
            using (var command = Command(c, tx, "SELECT scope,model_key,title,transient,revision FROM models WHERE scope=$s;", "$s", scope))
            using (var reader = command.ExecuteReader()) return reader.Read() ? Model(reader) : null;
        }
        private static FailureModel Model(SqliteDataReader r)
        { return new FailureModel { ModelScopeKey = r.GetString(0), ModelKey = r.IsDBNull(1) ? null : r.GetString(1), Title = r.IsDBNull(2) ? null : r.GetString(2), IsTransient = r.GetInt32(3) != 0, Revision = r.GetInt64(4) }; }
        public List<FailureModel> GetModels()
        {
            var results = new List<FailureModel>(); if (!File.Exists(DatabasePath)) return results;
            using (var c = Open(false)) using (var cmd = Command(c, null, "SELECT scope,model_key,title,transient,revision FROM models ORDER BY title,scope;"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) results.Add(Model(r));
            return results;
        }

        public FailurePersistenceResult SaveOperation(FailureOperation operation)
        {
            var safe = PrepareOperation(operation);
            return Persist("operation", safe, safe.ModelScopeKeys, (c, tx) => WriteOperation(c, tx, safe));
        }
        // Atomic per-model reservation closes the check-then-dispatch race across local Revit hosts.
        // Repeating it for the same operation is allowed when a new native document is first touched.
        public FailurePersistenceResult ReserveOperation(FailureOperation operation)
        {
            var safe = PrepareOperation(operation);
            return Persist("operation", safe, safe.ModelScopeKeys, (c, tx) =>
            {
                foreach (var scope in safe.ModelScopeKeys)
                {
                    foreach (var candidate in ConflictingScopes(c, tx, scope))
                        if (IsUncertain(candidate) || PendingFor(candidate) || HasDatabaseBlocker(c, tx, candidate, safe.OperationId, candidate != scope))
                            throw new GateDeniedException();
                }
                WriteOperation(c, tx, safe);
            });
        }
        private sealed class GateDeniedException : Exception { }
        private static FailureOperation PrepareOperation(FailureOperation operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            FailureCapture.RequireKey(operation.OperationId, "operation ID");
            operation.ModelScopeKeys.ForEach(s => FailureCapture.RequireKey(s, "model scope key"));
            var safe = FailureJson.Deserialize<FailureOperation>(FailureJson.Serialize(operation));
            safe.Tool = FailureCapture.SafeText(safe.Tool, 128); safe.RequestHash = SafeKey(safe.RequestHash);
            // Never store an arbitrary command result. Only the explicit receipts/status fields are retained.
            if (safe.FinalResult != null)
            {
                var result = new JObject();
                foreach (var name in new[] { "success", "error", "_failure", "_operation", "_changes", "_history" })
                    if (safe.FinalResult[name] != null) result[name] = safe.FinalResult[name].DeepClone();
                RedactResult(result);
                var json = result.ToString(Newtonsoft.Json.Formatting.None);
                safe.FinalResult = Encoding.UTF8.GetByteCount(json) <= 4096 ? result : CompactFinalResult(result, safe);
            }
            return safe;
        }
        private static void RedactResult(JToken token)
        {
            if (token is JObject obj)
                foreach (var property in obj.Properties().ToList())
                {
                    if (new[] { "code", "arguments", "args", "token", "path", "physicalKey", "identity" }.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) property.Remove();
                    else RedactResult(property.Value);
                }
            else if (token is JArray array) foreach (var child in array) RedactResult(child);
            else if (token is JValue value && value.Type == JTokenType.String) value.Value = FailureCapture.SafeText((string)value.Value);
        }
        private static JObject CompactFinalResult(JObject result, FailureOperation op)
        {
            var compact = new JObject { ["complete"] = false, ["operationId"] = op.OperationId, ["outcome"] = op.Outcome };
            if (result["success"] is JValue success) compact["success"] = success.DeepClone();
            if (result["error"] != null) compact["error"] = FailureCapture.SafeText(result["error"].ToString(), 256);
            foreach (var receiptName in new[] { "_failure", "_operation", "_changes", "_history" })
            {
                if (!(result[receiptName] is JObject receipt)) continue;
                var retained = new JObject();
                foreach (var field in new[] { "schemaVersion", "operationId", "incidentIds", "modelScopeKey", "action", "outcome", "execution", "gate", "persistence", "complete", "mutationApplied", "messageCount", "countKind", "id", "callId", "recorded", "modelKey" })
                {
                    if (receipt[field] is JArray ids) retained[field] = new JArray(ids.Take(20));
                    else if (receipt[field] is JValue value) retained[field] = value.Type == JTokenType.String ? (JToken)FailureCapture.SafeText(value.Value<string>(), 64) : value.DeepClone();
                }
                retained["compacted"] = true; compact[receiptName] = retained;
            }
            // At most four compact receipts. Preserve failure/operation first if unusually long IDs remain.
            if (Encoding.UTF8.GetByteCount(compact.ToString(Newtonsoft.Json.Formatting.None)) > 4096) { compact.Remove("_history"); compact.Remove("_changes"); }
            foreach (var name in new[] { "_failure", "_operation" })
                if (compact[name] is JObject receipt && receipt["incidentIds"] is JArray ids
                    && Encoding.UTF8.GetByteCount(compact.ToString(Newtonsoft.Json.Formatting.None)) > 4096)
                { receipt["incidentIds"] = new JArray(ids.Take(5)); receipt["incidentIdsComplete"] = false; }
            return compact;
        }
        private static void WriteOperation(SqliteConnection c, SqliteTransaction tx, FailureOperation op)
        {
            var requiresReview = op.RequiresReview || !op.Complete || op.Outcome == "unknown" && op.ObservationCoverage != "dynamic_code_unverified";
            Execute(c, tx, @"INSERT INTO operations(id,host,execution,outcome,review_required,updated,payload) VALUES($id,$h,$e,$o,$r,$u,$p)
ON CONFLICT(id) DO UPDATE SET execution=$e,outcome=$o,review_required=$r,updated=$u,payload=$p WHERE operations.updated <= $u;",
                "$id", op.OperationId, "$h", op.HostInstanceId, "$e", op.Execution, "$o", op.Outcome, "$r", requiresReview ? 1 : 0, "$u", Stamp(op.UpdatedUtc), "$p", FailureJson.Serialize(op));
            foreach (var scope in op.ModelScopeKeys.Distinct())
                Execute(c, tx, "INSERT OR IGNORE INTO operation_scopes(operation_id,scope) VALUES($i,$s);", "$i", op.OperationId, "$s", scope);
        }
        public FailurePersistenceResult RecordIncident(FailureIncident incident, IEnumerable<string> matchedGrantIds = null)
        {
            FailureCapture.RequireKey(incident?.IncidentId, "incident ID");
            FailureCapture.RequireKey(incident.OperationId, "operation ID");
            FailureCapture.RequireKey(incident.ModelScopeKey, "model scope key");
            var bounded = FailureCapture.Bound(incident);
            bounded.NativeContextDescription = FailureCapture.SafeText(bounded.NativeContextDescription, 512);
            var grants = (matchedGrantIds ?? bounded.GrantIds).Distinct().ToList();
            return Persist("incident", bounded, new[] { bounded.ModelScopeKey }, (c, tx) =>
            {
                // A callback may repeat. The initial event and grant consumption are one durable transaction.
                if (Scalar(c, tx, "SELECT 1 FROM incidents WHERE id=$i;", "$i", bounded.IncidentId) != null) return;
                var priorBytes = Convert.ToInt64(Scalar(c, tx, "SELECT COALESCE(SUM(length(CAST(payload AS BLOB))),0) FROM incidents WHERE operation_id=$o;", "$o", bounded.OperationId));
                if (priorBytes + Encoding.UTF8.GetByteCount(FailureJson.Serialize(bounded)) > FailureCapture.MaxOperationBytes)
                {
                    bounded.OmittedMessageCount += bounded.Messages.Count; bounded.Messages.Clear(); bounded.Complete = false;
                    bounded.CountKind = "at_least"; bounded.State = "open"; bounded.Action = "capture_incomplete";
                    if (grants.Count > 0 || bounded.RuleIds.Count > 0) throw new InvalidOperationException("Acceptance capture limit exceeded.");
                }
                if (bounded.RuleIds.Count > 0 || grants.Count > 0 && string.IsNullOrEmpty(bounded.NativeAction))
                {
                    var rules = bounded.RuleIds.Select(id => ReadPayload<FailureRule>(c, tx, "rules", id)).ToList();
                    bounded.RuleRevisions = rules.Where(r => r != null).ToDictionary(r => r.RuleId, r => r.Revision);
                    var availableGrants = grants.Select(id => ReadPayload<FailureGrant>(c, tx, "grants", id)).ToList();
                    var policy = EvaluateAcceptance(bounded, rules, availableGrants);
                    if (policy.Action != "continue") throw new InvalidOperationException("Acceptance no longer covers the captured failures.");
                }
                foreach (var grantId in grants)
                {
                    var grant = ReadPayload<FailureGrant>(c, tx, "grants", grantId);
                    if (grant == null || !grant.Enabled || grant.ConsumedOperationId != null || grant.ExpiresUtc <= DateTimeOffset.UtcNow
                        || grant.ModelScopeKey != bounded.ModelScopeKey) throw new InvalidOperationException("Grant is no longer valid.");
                    if (!string.IsNullOrEmpty(bounded.NativeAction) && FailurePolicy.MatchNativeGrant(new[] { grant }, bounded.ModelScopeKey,
                        bounded.RevitYear, bounded.NativeAction, bounded.NativeContextHash, bounded.RequestHash, bounded.DocumentRevision) == null)
                        throw new InvalidOperationException("Native grant no longer matches this attempt.");
                    grant.ConsumedOperationId = bounded.OperationId;
                    Execute(c, tx, "UPDATE grants SET consumed_operation=$o,payload=$p WHERE id=$i AND consumed_operation IS NULL;",
                        "$o", bounded.OperationId, "$p", FailureJson.Serialize(grant), "$i", grantId);
                }
                WriteIncident(c, tx, bounded);
            });
        }
        private static void WriteIncident(SqliteConnection c, SqliteTransaction tx, FailureIncident incident)
        {
            Execute(c, tx, "INSERT OR IGNORE INTO incidents(id,operation_id,scope,state,created,payload) VALUES($i,$o,$s,$st,$t,$p);",
                "$i", incident.IncidentId, "$o", incident.OperationId, "$s", incident.ModelScopeKey,
                "$st", incident.State, "$t", Stamp(incident.CreatedUtc), "$p", FailureJson.Serialize(incident));
            for (var i = 0; i < incident.Messages.Count; i++)
                Execute(c, tx, "INSERT OR IGNORE INTO failure_messages(incident_id,sequence,payload) VALUES($i,$n,$p);",
                    "$i", incident.IncidentId, "$n", i, "$p", FailureJson.Serialize(incident.Messages[i]));
            foreach (var id in incident.Messages.SelectMany(m => m.FailingElementIds.Concat(m.AdditionalElementIds)).Distinct())
                Execute(c, tx, "INSERT OR IGNORE INTO failure_elements(incident_id,element_id) VALUES($i,$e);", "$i", incident.IncidentId, "$e", id);
            Execute(c, tx, "UPDATE models SET revision=revision+1 WHERE scope=$s;", "$s", incident.ModelScopeKey);
        }

        public FailureOperation GetOperation(string operationId)
        {
            FailureCapture.RequireKey(operationId, "operation ID"); if (!File.Exists(DatabasePath)) return null;
            using (var c = Open(false)) return ReadPayload<FailureOperation>(c, null, "operations", operationId);
        }
        public List<FailureRule> GetRules(string scope) { return Policies<FailureRule>("rules", scope); }
        public List<FailureGrant> GetGrants(string scope) { return Policies<FailureGrant>("grants", scope); }
        private List<T> Policies<T>(string table, string scope)
        {
            FailureCapture.RequireKey(scope, "model scope key"); var result = new List<T>(); if (!File.Exists(DatabasePath)) return result;
            using (var c = Open(false)) using (var cmd = Command(c, null, "SELECT payload FROM " + table + " WHERE scope=$s ORDER BY id;", "$s", scope))
            using (var r = cmd.ExecuteReader()) while (r.Read()) result.Add(FailureJson.Deserialize<T>(r.GetString(0)));
            return result;
        }
        public FailureGate GetGate(string scope, string excludingOperationId = null)
        {
            FailureCapture.RequireKey(scope, "model scope key");
            var gate = new FailureGate();
            lock (_sync) if (_uncertainScopes.Contains(scope)) { gate.Blocked = true; gate.Reason = "FAILURE_STORAGE_FAILED"; }
            if (PendingFor(scope)) { gate.Blocked = true; gate.Reason = "FAILURE_STORAGE_FAILED"; }
            if (!File.Exists(DatabasePath)) return gate;
            using (var c = Open(false))
            {
                var model = ReadModel(c, null, scope); gate.Revision = model?.Revision ?? 0;
                foreach (var candidate in ConflictingScopes(c, null, scope))
                {
                    if (candidate != scope && (HasDatabaseBlocker(c, null, candidate, excludingOperationId, true) || PendingFor(candidate) || IsUncertain(candidate)))
                    { gate.Blocked = true; gate.Reason = "FAILURE_IDENTITY_UNCERTAIN"; }
                    var derivedOperation = candidate == scope ? null : FailureCapture.Fingerprint("identity-uncertain:" + candidate);
                    using (var cmd = Command(c, null, "SELECT id FROM incidents WHERE scope=$s AND " + BlockingIncident + " AND ($d IS NULL OR operation_id!=$d) ORDER BY seq;", "$s", candidate, "$d", derivedOperation))
                    using (var r = cmd.ExecuteReader()) while (r.Read()) gate.IncidentIds.Add(r.GetString(0));
                    using (var cmd = Command(c, null, @"SELECT o.id FROM operations o JOIN operation_scopes s ON s.operation_id=o.id
WHERE s.scope=$s AND ($d IS NULL OR o.id!=$d) AND ($x IS NULL OR o.id!=$x) AND (o.execution!='completed' OR o.review_required=1)
AND NOT ((o.execution='completed' OR o.execution='interrupted_unknown') AND EXISTS
(SELECT 1 FROM operation_reviews r WHERE r.operation_id=o.id AND r.scope=s.scope));", "$s", candidate, "$d", derivedOperation, "$x", excludingOperationId))
                    using (var r = cmd.ExecuteReader()) while (r.Read()) gate.OperationIds.Add(r.GetString(0));
                }
            }
            gate.Blocked |= gate.IncidentIds.Count > 0 || gate.OperationIds.Count > 0;
            if (gate.Blocked && gate.Reason == null) gate.Reason = "REVIT_MODEL_BLOCKED";
            return gate;
        }
        // The same request was already rolled back by Revit and the user has not decided yet.
        public List<string> GetRepeatedRequestIncidents(string scope, string requestHash)
        {
            FailureCapture.RequireKey(scope, "model scope key"); var result = new List<string>();
            if (string.IsNullOrEmpty(requestHash) || !File.Exists(DatabasePath)) return result;
            using (var c = Open(false))
            using (var cmd = Command(c, null, "SELECT id FROM incidents WHERE scope=$s AND state='open' AND " + CleanRollback
                + " AND json_extract(payload,'$.requestHash')=$h ORDER BY seq;", "$s", scope, "$h", requestHash))
            using (var r = cmd.ExecuteReader()) while (r.Read()) result.Add(r.GetString(0));
            return result;
        }
        public FailureRecordPage Query(FailureRecordQuery query)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            foreach (var key in new[] { query.ModelScopeKey, query.IncidentId, query.OperationId }) if (key != null) FailureCapture.RequireKey(key, "query identifier");
            if (query.Limit < 1 || query.Limit > 100) throw new ArgumentOutOfRangeException(nameof(query.Limit));
            if (query.State != null && !new[] { "observed", "open", "resolved_rework", "resolved_abandoned", "accepted_once", "accepted_by_rule" }.Contains(query.State))
                throw new ArgumentException("Invalid incident state.");
            long after = 0, ceiling = long.MaxValue;
            var filter = FailureCapture.Fingerprint(FailureJson.Serialize(new { query.ModelScopeKey, query.IncidentId, query.OperationId, query.State, query.ElementId, query.From, query.Until }));
            if (query.Cursor != null)
            {
                var parts = query.Cursor.Split('.');
                if (parts.Length != 3 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out after)
                    || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out ceiling) || parts[2] != filter)
                    throw new ArgumentException("Invalid cursor or changed query filters.");
            }
            var page = new FailureRecordPage(); if (!File.Exists(DatabasePath)) return page;
            using (var c = Open(false))
            using (var tx = c.BeginTransaction(deferred: true))
            {
                if (query.Cursor == null) ceiling = Convert.ToInt64(Scalar(c, tx, "SELECT COALESCE(MAX(seq),0) FROM incidents;"));
                using (var cmd = Command(c, tx, @"SELECT seq,payload FROM incidents i WHERE seq>$a AND seq<=$z
AND ($s IS NULL OR scope=$s) AND ($i IS NULL OR id=$i) AND ($o IS NULL OR operation_id=$o)
AND ($st IS NULL OR state=$st) AND ($f IS NULL OR created >= $f) AND ($u IS NULL OR created <= $u)
AND ($e IS NULL OR EXISTS(SELECT 1 FROM failure_elements f WHERE f.incident_id=i.id AND f.element_id=$e)) ORDER BY seq LIMIT $n;",
                    "$a", after, "$z", ceiling, "$s", query.ModelScopeKey, "$i", query.IncidentId, "$o", query.OperationId,
                    "$st", query.State, "$f", query.From.HasValue ? Stamp(query.From.Value) : null,
                    "$u", query.Until.HasValue ? Stamp(query.Until.Value) : null, "$e", query.ElementId, "$n", query.Limit + 1))
                using (var r = cmd.ExecuteReader()) while (r.Read())
                {
                    if (page.Records.Count == query.Limit) { page.NextCursor = after + "." + ceiling + "." + filter; break; }
                    after = r.GetInt64(0); var record = FailureJson.Deserialize<FailureIncident>(r.GetString(1));
                    page.Complete &= record.Complete;
                    if (!query.IncludeDetails) record.Messages.Clear();
                    page.Records.Add(record);
                }
                page.Available = true; tx.Commit();
            }
            return page;
        }

        public FailurePersistenceResult ApplyDecision(FailureDecision decision)
        {
            Writer(); ValidateDecision(decision);
            // Decisions are never placed in fallback: a failed save must leave the old policy/gate intact.
            try
            {
                EnsureInitialized();
                using (var c = Open(true)) using (var tx = c.BeginTransaction())
                {
                    if (Scalar(c, tx, "SELECT 1 FROM decisions WHERE id=$i;", "$i", decision.DecisionId) != null) return Recorded();
                    var incident = decision.Action == "revoke_rule" ? new FailureIncident { ModelScopeKey = decision.ModelScopeKey }
                        : ReadPayload<FailureIncident>(c, tx, "incidents", decision.IncidentId);
                    if (incident == null || incident.ModelScopeKey != decision.ModelScopeKey) throw new ArgumentException("Incident does not belong to this model.");
                    if (decision.Action != "revoke_rule" && incident.State != "open"
                        && !(decision.Action == "create_rule" && incident.State == "observed" && IsSupportedDialog(incident)))
                        throw new ArgumentException("This incident is no longer open.");
                    var operation = incident.OperationId == null ? null : ReadPayload<FailureOperation>(c, tx, "operations", incident.OperationId);
                    if ((operation?.Outcome == "partial" || operation?.Outcome == "unknown" || operation?.RequiresReview == true || operation?.Complete == false) && !decision.VerifiedModel)
                        throw new ArgumentException("The model must be checked before closing a partial or unknown outcome.");
                    if (operation != null && operation.Execution != "completed" && operation.Execution != "interrupted_unknown")
                        throw new ArgumentException("The operation is still executing.");
                    if (decision.VerifiedModel) decision.Verification = "human_asserted";
                    var rules = decision.Rules.Concat(decision.Rule == null ? Enumerable.Empty<FailureRule>() : new[] { decision.Rule }).ToList();
                    var grants = decision.Grants.Concat(decision.Grant == null ? Enumerable.Empty<FailureGrant>() : new[] { decision.Grant }).ToList();
                    var model = ReadModel(c, tx, decision.ModelScopeKey);
                    if (model == null) throw new ArgumentException("Model scope was not found.");
                    if (decision.Action == "create_rule" || decision.Action == "accept_once")
                    {
                        if (!incident.Complete || incident.Messages.Count == 0 || incident.Messages.Any(m => !m.Complete)
                            || incident.Messages.Any(m => m.Severity != "Warning") && !IsSupportedDialog(incident))
                            throw new ArgumentException("Only completely captured warnings or supported exact dialogs may be accepted.");
                        foreach (var rule in rules.Cast<FailureRule>().Concat(grants)) ValidateWarning(rule, incident, model.Revision + 1);
                        if (decision.Action == "create_rule" && (rules.Count == 0 || grants.Count != 0)
                            || decision.Action == "accept_once" && (grants.Count == 0 || rules.Count != 0)) throw new ArgumentException("Missing acceptance constraints.");
                        foreach (var grant in grants) ValidateGrant(grant, incident, false, decision.DocumentRevision, decision.VerifiedModel);
                        var evaluation = EvaluateAcceptance(incident, rules, grants, decision.DocumentRevision);
                        if (evaluation.Action != "continue") throw new ArgumentException("Acceptance must cover every warning.");
                    }
                    if (decision.Action == "native_grant")
                    {
                        if (grants.Count != 1 || rules.Count != 0) throw new ArgumentException("One exact native grant is required.");
                        ValidateGrant(grants[0], incident, true, decision.DocumentRevision, decision.VerifiedModel);
                    }
                    if (decision.Action == "revoke_rule")
                    {
                        if (rules.Count != 1) throw new ArgumentException("One rule is required.");
                        var old = ReadPayload<FailureRule>(c, tx, "rules", rules[0].RuleId);
                        if (old == null || old.ModelScopeKey != incident.ModelScopeKey) throw new ArgumentException("Rule does not belong to this model.");
                        old.Enabled = false; old.Revision = model.Revision + 1; rules = new List<FailureRule> { old };
                    }
                    foreach (var rule in rules)
                    {
                        var old = ReadPayload<FailureRule>(c, tx, "rules", rule.RuleId);
                        if (old != null && old.ModelScopeKey != decision.ModelScopeKey) throw new ArgumentException("Rule ID belongs to another model.");
                        Execute(c, tx, "INSERT INTO rules(id,scope,enabled,payload) VALUES($i,$s,$e,$p) ON CONFLICT(id) DO UPDATE SET enabled=$e,payload=$p;",
                            "$i", rule.RuleId, "$s", rule.ModelScopeKey, "$e", rule.Enabled ? 1 : 0, "$p", FailureJson.Serialize(rule));
                    }
                    foreach (var grant in grants)
                        Execute(c, tx, "INSERT INTO grants(id,scope,enabled,payload) VALUES($i,$s,1,$p);", "$i", grant.GrantId, "$s", grant.ModelScopeKey, "$p", FailureJson.Serialize(grant));
                    if (decision.Action != "revoke_rule")
                    {
                        incident.State = decision.Action == "rework" ? "resolved_rework" : decision.Action == "abandon" ? "resolved_abandoned"
                            : decision.Action == "create_rule" ? "accepted_by_rule" : "accepted_once";
                        Execute(c, tx, "UPDATE incidents SET state=$s,payload=$p WHERE id=$i;", "$s", incident.State, "$p", FailureJson.Serialize(incident), "$i", incident.IncidentId);
                    }
                    // Review is per model. It does not rewrite execution/outcome facts or clear another touched model.
                    if (operation != null && decision.VerifiedModel && (operation.Outcome == "unknown" || operation.Execution == "interrupted_unknown" || operation.RequiresReview || !operation.Complete))
                    {
                        Execute(c, tx, "INSERT OR REPLACE INTO operation_reviews(operation_id,scope,decision_id) VALUES($o,$s,$d);",
                            "$o", operation.OperationId, "$s", decision.ModelScopeKey, "$d", decision.DecisionId);
                    }
                    Execute(c, tx, "INSERT INTO decisions(id,scope,incident_id,created,payload) VALUES($i,$s,$n,$t,$p);", "$i", decision.DecisionId,
                        "$s", decision.ModelScopeKey, "$n", decision.IncidentId, "$t", Stamp(decision.CreatedUtc), "$p", FailureJson.Serialize(decision));
                    Execute(c, tx, "UPDATE models SET revision=revision+1 WHERE scope=$s;", "$s", decision.ModelScopeKey);
                    tx.Commit();
                    if (decision.VerifiedModel && !PendingFor(decision.ModelScopeKey))
                        lock (_sync) _uncertainScopes.Remove(decision.ModelScopeKey);
                    return Recorded();
                }
            }
            catch (ArgumentException) { throw; }
            catch (Exception) { return new FailurePersistenceResult { Persistence = "failed", ErrorCode = "FAILURE_STORAGE_FAILED" }; }
        }
        private static void ValidateDecision(FailureDecision d)
        {
            if (d == null) throw new ArgumentNullException(nameof(d));
            FailureCapture.RequireKey(d.DecisionId, "decision ID"); FailureCapture.RequireKey(d.ModelScopeKey, "model scope key");
            if (d.Action != "revoke_rule") FailureCapture.RequireKey(d.IncidentId, "incident ID");
            if (d.Channel != "revit_ui" || string.IsNullOrWhiteSpace(d.WindowsUser) || string.IsNullOrWhiteSpace(d.Reason)
                || !new[] { "rework", "abandon", "accept_once", "create_rule", "revoke_rule", "native_grant" }.Contains(d.Action))
                throw new ArgumentException("A reason and Revit UI actor are required.");
            d.Reason = FailureCapture.SafeText(d.Reason); d.WindowsUser = FailureCapture.SafeText(d.WindowsUser, 256); d.RevitUser = FailureCapture.SafeText(d.RevitUser, 256);
            if ((d.Action == "rework" || d.Action == "abandon") && (d.Rule != null || d.Grant != null || d.Rules.Count != 0 || d.Grants.Count != 0))
                throw new ArgumentException("Resolution cannot also create a rule or grant.");
        }
        private static void ValidateWarning(FailureRule r, FailureIncident i, long revision)
        {
            FailureCapture.RequireKey(r.RuleId, "rule ID");
            var dialog = IsSupportedDialog(i);
            if (r.ModelScopeKey != i.ModelScopeKey || r.Severity != (dialog ? "TaskDialog" : "Warning") || r.RevitYear != i.RevitYear
                || r.OperationContext != i.OperationContext || r.MaxCount < 1 || r.MaxCount > FailureCapture.MaxMessages
                || !i.Messages.Any(m => m.DefinitionId == r.DefinitionId) || !r.Enabled
                || dialog && (r.MaxCount != 1 || r.ExistingElementIds.Count != 0))
                throw new ArgumentException("Warning constraints do not match the observed model and context.");
            r.Revision = revision;
        }
        private static bool IsSupportedDialog(FailureIncident i)
        {
            return i.Complete && i.Messages.Count == 1 && i.Messages[0].Complete && i.Messages[0].Severity == "TaskDialog"
                && i.Messages[0].FailingElementIds.Count == 0 && i.Messages[0].AdditionalElementIds.Count == 0 && i.Messages[0].ExistingElementIds.Count == 0
                && FailureDialogAdapters.Get(i.Messages[0].DefinitionId, i.RevitYear, i.OperationContext) != null;
        }
        private static FailurePolicyResult EvaluateAcceptance(FailureIncident i, IEnumerable<FailureRule> rules, IEnumerable<FailureGrant> grants, string currentRevision = null)
        {
            return IsSupportedDialog(i)
                ? FailurePolicy.EvaluateDialog(i.Messages[0].DefinitionId, i.RevitYear, i.OperationContext, i.ModelScopeKey, i.RequestHash, currentRevision ?? i.DocumentRevision, rules, grants)
                : FailurePolicy.Evaluate(i.Messages, i.Complete, i.ModelScopeKey, i.RevitYear, i.OperationContext, i.RequestHash, currentRevision ?? i.DocumentRevision, rules, grants);
        }
        private static void ValidateGrant(FailureGrant g, FailureIncident i, bool native, string currentRevision, bool verified)
        {
            FailureCapture.RequireKey(g.GrantId, "grant ID");
            var expectedRevision = verified && !string.IsNullOrEmpty(currentRevision) ? currentRevision : i.DocumentRevision;
            if (g.ModelScopeKey != i.ModelScopeKey || g.RevitYear != i.RevitYear || g.RequestHash != i.RequestHash
                || g.DocumentRevision != expectedRevision || string.IsNullOrEmpty(g.RequestHash) || string.IsNullOrEmpty(g.DocumentRevision)
                || g.ConsumedOperationId != null || g.ExpiresUtc <= DateTimeOffset.UtcNow || g.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(5).AddSeconds(1)
                || !g.Enabled || (native && (string.IsNullOrEmpty(i.NativeAction) || g.NativeAction != i.NativeAction || g.NativeContextHash != i.NativeContextHash || string.IsNullOrEmpty(i.NativeContextHash)))
                || (!native && !string.IsNullOrEmpty(g.NativeAction))) throw new ArgumentException("Grant does not match this exact attempt.");
        }
        public FailurePersistenceResult InvalidateGrants(string scope)
        {
            FailureCapture.RequireKey(scope, "model scope key"); Writer();
            if (!File.Exists(DatabasePath)) return Recorded();
            return Persist("invalidate_grants", new { ModelScopeKey = scope }, new[] { scope }, (c, tx) => WriteInvalidateGrants(c, tx, scope));
        }
        private static void WriteInvalidateGrants(SqliteConnection c, SqliteTransaction tx, string scope)
        {
            var grants = new List<FailureGrant>();
            using (var cmd = Command(c, tx, "SELECT payload FROM grants WHERE scope=$s AND enabled=1;", "$s", scope))
            using (var r = cmd.ExecuteReader()) while (r.Read()) grants.Add(FailureJson.Deserialize<FailureGrant>(r.GetString(0)));
            foreach (var g in grants) { g.Enabled = false; Execute(c, tx, "UPDATE grants SET enabled=0,payload=$p WHERE id=$i;", "$p", FailureJson.Serialize(g), "$i", g.GrantId); }
            Execute(c, tx, "UPDATE models SET revision=revision+1 WHERE scope=$s;", "$s", scope);
        }
        public int MarkInterruptedHosts(ISet<string> liveHostIds)
        {
            Writer(); if (liveHostIds == null) throw new ArgumentNullException(nameof(liveHostIds));
            if (!File.Exists(DatabasePath)) return 0; var count = 0;
            using (var c = Open(true)) using (var tx = c.BeginTransaction())
            {
                var pending = new List<FailureOperation>();
                using (var cmd = Command(c, tx, "SELECT payload FROM operations WHERE execution!='completed' AND execution!='interrupted_unknown';"))
                using (var r = cmd.ExecuteReader()) while (r.Read()) pending.Add(FailureJson.Deserialize<FailureOperation>(r.GetString(0)));
                foreach (var op in pending.Where(o => !liveHostIds.Contains(o.HostInstanceId ?? "")))
                {
                    op.Execution = "interrupted_unknown"; op.Outcome = "unknown"; op.MutationApplied = null; op.Complete = false; op.RequiresReview = true; op.UpdatedUtc = DateTimeOffset.UtcNow;
                    WriteOperation(c, tx, op);
                    foreach (var scope in op.ModelScopeKeys)
                        WriteIncident(c, tx, new FailureIncident { IncidentId = FailureCapture.Fingerprint("interrupted:" + op.OperationId + ":" + scope),
                            OperationId = op.OperationId, ModelScopeKey = scope, Source = "host_recovery", Action = "outcome_unknown", Complete = false,
                            RequestHash = op.RequestHash, RevitYear = op.RevitYear, OperationContext = op.OperationContext, DocumentRevision = op.DocumentRevision });
                    count++;
                }
                tx.Commit();
            }
            return count;
        }

        private FailurePersistenceResult Persist(string kind, object payload, IEnumerable<string> scopes, Action<SqliteConnection, SqliteTransaction> write)
        {
            Writer(); var membership = scopes.Distinct().ToList();
            try { EnsureInitialized(); using (var c = Open(true)) using (var tx = c.BeginTransaction()) { write(c, tx); tx.Commit(); } return Recorded(); }
            catch (GateDeniedException)
            { return new FailurePersistenceResult { Persistence = "failed", ErrorCode = "REVIT_MODEL_BLOCKED" }; }
            catch (Exception ex)
            {
                Diagnostic?.Invoke("Failure ledger " + kind + " write failed: " + ex.GetType().Name + ": " + ex.Message);
                lock (_sync) foreach (var scope in membership) _uncertainScopes.Add(scope);
                var id = Guid.NewGuid().ToString("N"); string temporary = null;
                try
                {
                    Directory.CreateDirectory(PendingPath);
                    var envelope = new JObject { ["id"] = id, ["kind"] = kind, ["scopes"] = new JArray(membership), ["payload"] = JObject.Parse(FailureJson.Serialize(payload)) };
                    var data = Encoding.UTF8.GetBytes(envelope.ToString(Newtonsoft.Json.Formatting.None));
                    if (data.Length > FailureCapture.MaxOperationBytes) throw new IOException("Capture exceeds pending limit.");
                    temporary = Path.Combine(PendingPath, id + ".tmp");
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(data, 0, data.Length); stream.Flush(true); }
                    File.Move(temporary, Path.Combine(PendingPath, id + ".json"));
                    return new FailurePersistenceResult { Persistence = "fallback", Durable = true, ErrorCode = "FAILURE_STORAGE_FAILED" };
                }
                catch (Exception fallback)
                {
                    Diagnostic?.Invoke("Failure ledger " + kind + " fallback failed: " + fallback.GetType().Name + ": " + fallback.Message);
                    return new FailurePersistenceResult { Persistence = "failed", Durable = false, ErrorCode = "FAILURE_STORAGE_FAILED" };
                }
                finally { if (temporary != null && File.Exists(temporary)) { try { File.Delete(temporary); } catch (IOException) { } } }
            }
        }
        private bool PendingFor(string scope)
        {
            if (!Directory.Exists(PendingPath)) return false;
            foreach (var file in Directory.EnumerateFiles(PendingPath, "*.json"))
            {
                try { var envelope = ReadPending(file); if (((JArray)envelope["scopes"]).Values<string>().Contains(scope)) return true; }
                catch (Exception) { return true; } // An unreadable pending record cannot safely clear any model gate.
            }
            return false;
        }
        private static JObject ReadPending(string file)
        { if (new FileInfo(file).Length > FailureCapture.MaxOperationBytes) throw new IOException("Pending capture is too large."); return FailureJson.Deserialize<JObject>(File.ReadAllText(file)); }
        public int RecoverPending()
        {
            Writer(); if (!Directory.Exists(PendingPath)) return 0; EnsureInitialized(); int count = 0;
            // Operations precede incidents for FK consistency; updates are ordered by durable timestamp.
            var files = Directory.EnumerateFiles(PendingPath, "*.json").Select(f => new { File = f, Envelope = ReadPending(f) })
                .OrderBy(e => e.Envelope.Value<string>("kind") == "operation" ? 1 : e.Envelope.Value<string>("kind") == "incident" ? 2 : 0)
                .ThenBy(e => e.Envelope["payload"]?.Value<DateTimeOffset?>("updatedUtc") ?? DateTimeOffset.MinValue).ToList();
            foreach (var entry in files)
            {
                using (var c = Open(true)) using (var tx = c.BeginTransaction())
                {
                    var id = entry.Envelope.Value<string>("id"); FailureCapture.RequireKey(id, "pending ID");
                    if (Scalar(c, tx, "SELECT 1 FROM recovered WHERE id=$i;", "$i", id) == null)
                    {
                        if (entry.Envelope.Value<string>("kind") == "physical_alias")
                            WritePhysicalAlias(c, tx, entry.Envelope["payload"].Value<string>("modelScopeKey"), entry.Envelope["payload"].Value<string>("physicalKey"));
                        else if (entry.Envelope.Value<string>("kind") == "invalidate_grants")
                            WriteInvalidateGrants(c, tx, entry.Envelope["payload"].Value<string>("modelScopeKey"));
                        else if (entry.Envelope.Value<string>("kind") == "operation")
                        {
                            var op = entry.Envelope["payload"].ToObject<FailureOperation>();
                            // Recovery reports uncertainty. It never replays an operation or retroactively accepts warnings.
                            op.Complete = false; op.RequiresReview = true; op.Execution = "interrupted_unknown"; op.Outcome = "unknown"; op.MutationApplied = null;
                            WriteOperation(c, tx, op);
                            foreach (var scope in op.ModelScopeKeys)
                                WriteIncident(c, tx, new FailureIncident { IncidentId = FailureCapture.Fingerprint("recovered:" + id + ":" + scope),
                                    OperationId = op.OperationId, ModelScopeKey = scope, Source = "storage_recovery", Action = "storage_uncertainty", Complete = false, State = "open" });
                        }
                        else if (entry.Envelope.Value<string>("kind") == "incident")
                        {
                            var incident = entry.Envelope["payload"].ToObject<FailureIncident>();
                            incident.State = "open"; incident.Complete = false; incident.Persistence = "recorded";
                            incident.Action = "storage_uncertainty"; WriteIncident(c, tx, incident);
                        }
                        else throw new IOException("Unknown pending capture kind.");
                        Execute(c, tx, "INSERT INTO recovered(id) VALUES($i);", "$i", id);
                    }
                    tx.Commit();
                }
                File.Delete(entry.File); count++;
            }
            lock (_sync) _uncertainScopes.Clear();
            return count;
        }
        private static FailurePersistenceResult Recorded() { return new FailurePersistenceResult { Persistence = "recorded", Durable = true }; }
        private static string Stamp(DateTimeOffset value) { return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture); }
        private static T ReadPayload<T>(SqliteConnection c, SqliteTransaction tx, string table, string id)
        { var value = Scalar(c, tx, "SELECT payload FROM " + table + " WHERE id=$i;", "$i", id); return value == null ? default(T) : FailureJson.Deserialize<T>((string)value); }
        private static SqliteCommand Command(SqliteConnection c, SqliteTransaction tx, string sql, params object[] args)
        {
            var command = c.CreateCommand(); command.CommandText = sql; command.Transaction = tx; command.CommandTimeout = 1;
            for (var i = 0; i < args.Length; i += 2) command.Parameters.AddWithValue((string)args[i], args[i + 1] ?? DBNull.Value);
            return command;
        }
        private static void Execute(SqliteConnection c, SqliteTransaction tx, string sql, params object[] args)
        { using (var command = Command(c, tx, sql, args)) command.ExecuteNonQuery(); }
        private static object Scalar(SqliteConnection c, SqliteTransaction tx, string sql, params object[] args)
        { using (var command = Command(c, tx, sql, args)) { var value = command.ExecuteScalar(); return value == DBNull.Value ? null : value; } }
    }
}
