using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Scripting;

namespace RvtMcp.Server.Bake
{
    internal sealed class CodeModuleStore
    {
        private readonly BakeDb _db;
        internal Action<string> WriteCheckpointForTests { get; set; }
        internal CodeModuleStore(BakeDb db) { _db = db ?? throw new ArgumentNullException(nameof(db)); }

        internal async Task<CodeModuleSaveResult> SaveAsync(string name, string code, string description, IEnumerable<string> requires,
            string expectedHash, Func<CodeModuleSnapshot, CancellationToken, Task<CodeModuleCompilation>> compile,
            CancellationToken cancellation = default)
        {
            var candidate = new ScriptModuleRevision(name, code, requires);
            if (candidate.Requires.Count > ScriptLimits.ModuleCount) throw Error(ScriptErrorCodes.ModuleLimitExceeded, "Too many direct dependencies.");
            CheckHash(expectedHash, optional: true); expectedHash = expectedHash?.ToLowerInvariant();
            description = description ?? "";
            CheckPersistence(candidate.Code); CheckPersistence(description);
            if (compile == null) throw new ArgumentNullException(nameof(compile));
            cancellation.ThrowIfCancellationRequested();
            var snapshot = Read((connection, transaction) => {
                CheckExpected(ReadHead(connection, transaction, name), expectedHash);
                return Resolve(connection, transaction, new[] {name}, candidate);
            });
            // No connection/transaction survives the read callback or spans the native compile wait.
            var validation = await compile(snapshot, cancellation).ConfigureAwait(false);
            if (validation == null) throw Error(ScriptErrorCodes.CompileFailed, "Module compile validation did not pass.");
            cancellation.ThrowIfCancellationRequested();
            return Write((connection, transaction) => {
                var current = ReadHead(connection, transaction, name); CheckExpected(current, expectedHash);
                foreach (var dependency in snapshot.Modules.Where(m => m.Name != name))
                    if (ReadHead(connection, transaction, dependency.Name)?.Revision.Hash != dependency.Hash)
                        throw Error(ScriptErrorCodes.ModuleConflict, "A module dependency changed during compile validation.");
                if (current?.Revision.Hash == candidate.Hash && current.Description == description)
                    return new CodeModuleSaveResult(current, false, validation, snapshot.ManifestHash);
                // Detect a poisoned orphan before INSERT OR IGNORE could hide it.
                var existing = ReadRevision(connection, transaction, candidate.Hash);
                if (existing != null && (existing.Name != candidate.Name || existing.Hash != candidate.Hash)) throw Corrupt();
                var now = DateTimeOffset.UtcNow.ToString("o");
                Execute(connection, transaction, @"INSERT OR IGNORE INTO code_module_revisions
(revision_hash,name,code,code_sha256,requires_json,created_at) VALUES($hash,$name,$code,$codeHash,$requires,$now)",
                    ("$hash", candidate.Hash), ("$name", name), ("$code", candidate.Code), ("$codeHash", candidate.CodeSha256),
                    ("$requires", JsonConvert.SerializeObject(candidate.Requires)), ("$now", now));
                WriteCheckpointForTests?.Invoke("after_revision"); cancellation.ThrowIfCancellationRequested();
                Execute(connection, transaction, @"INSERT INTO code_modules(name,revision_hash,description,signatures_json,compiled_host_json,updated_at)
VALUES($name,$hash,$description,$signatures,$host,$now) ON CONFLICT(name) DO UPDATE SET revision_hash=$hash,
description=$description,signatures_json=$signatures,compiled_host_json=$host,updated_at=$now",
                    ("$name", name), ("$hash", candidate.Hash), ("$description", description),
                    ("$signatures", JsonConvert.SerializeObject(validation.Signatures)), ("$host", validation.Host(snapshot.ManifestHash).ToString(Formatting.None)), ("$now", now));
                WriteCheckpointForTests?.Invoke("after_head"); cancellation.ThrowIfCancellationRequested();
                return new CodeModuleSaveResult(ReadHead(connection, transaction, name), true, validation, snapshot.ManifestHash);
            });
        }

        internal CodeModuleSnapshot Resolve(IEnumerable<string> roots, IEnumerable<ScriptModuleManifestEntry> pinned = null)
        {
            var names = (roots ?? Enumerable.Empty<string>()).ToArray();
            foreach (var name in names) ScriptModuleRevision.ValidateName(name);
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw Error(ScriptErrorCodes.InputInvalid, "Duplicate module roots.");
            if (names.Length > ScriptLimits.ModuleCount) throw Error(ScriptErrorCodes.ModuleLimitExceeded, "Too many module roots.");
            var manifest = pinned?.ToArray();
            var expectedManifest = manifest == null ? null : ScriptIdentity.ManifestHash(manifest);
            var snapshot = Read((connection, transaction) => {
                if (manifest != null)
                    foreach (var entry in manifest)
                        if (ReadHead(connection, transaction, entry.Name)?.Revision.Hash != entry.Hash)
                            throw Error(ScriptErrorCodes.ModuleChanged, "A pinned module head changed or was deleted.");
                return Resolve(connection, transaction, names);
            });
            if (manifest != null && snapshot.ManifestHash != expectedManifest)
                throw Error(ScriptErrorCodes.ModuleChanged, "Resolved modules no longer match the pinned manifest.");
            return snapshot;
        }

        internal IReadOnlyList<CodeModuleRecord> List(string nameFilter = "", int startIndex = 0, int limit = 100)
        {
            if (startIndex < 0 || limit < 1 || limit > 500) throw Error(ScriptErrorCodes.InputInvalid, "Invalid module page.");
            return Read((connection, transaction) => {
                var names = Names(connection, transaction).Where(n => n.IndexOf(nameFilter ?? "", StringComparison.Ordinal) >= 0).Skip(startIndex).Take(limit).ToArray();
                return (IReadOnlyList<CodeModuleRecord>)Array.AsReadOnly(names.Select(n => ReadHead(connection, transaction, n)).ToArray());
            });
        }

        internal void Delete(string name, string expectedHash)
        {
            ScriptModuleRevision.ValidateName(name); CheckHash(expectedHash, optional: false);
            Write((connection, transaction) => {
                CheckExpected(ReadHead(connection, transaction, name), expectedHash.ToLowerInvariant());
                foreach (var other in Names(connection, transaction).Where(n => n != name))
                    if (ReadHead(connection, transaction, other).Revision.Requires.Contains(name, StringComparer.Ordinal))
                        throw Error(ScriptErrorCodes.ModuleInUse, "Another module requires this module.");
                Execute(connection, transaction, "DELETE FROM code_modules WHERE name=$name", ("$name", name));
                WriteCheckpointForTests?.Invoke("after_delete");
                return true;
            });
        }

        internal CodeModulePage ReadPage(string nameFilter, int startIndex, int limit)
        {
            if (startIndex < 0 || limit < 1 || limit > 500) throw Error(ScriptErrorCodes.InputInvalid, "Invalid module page.");
            return Read((connection, transaction) => {
                var names = Names(connection, transaction).Where(n => n.IndexOf(nameFilter ?? "", StringComparison.Ordinal) >= 0).ToArray();
                var items = new List<CodeModuleListItem>();
                foreach (var name in names.Skip(startIndex).Take(limit))
                {
                    var record = ReadHead(connection, transaction, name); var status = "current";
                    try
                    {
                        var snapshot = Resolve(connection, transaction, new[] {name});
                        if (JObject.Parse(record.CompiledHostJson).Value<string>("module_manifest_hash") != snapshot.ManifestHash) status = "changed";
                    }
                    catch (ScriptContractException ex) when (ex.ErrorCode == ScriptErrorCodes.ModuleNotFound || ex.ErrorCode == ScriptErrorCodes.ModuleCycle || ex.ErrorCode == ScriptErrorCodes.ModuleLimitExceeded)
                    { status = ex.ErrorCode == ScriptErrorCodes.ModuleNotFound ? "missing" : ex.ErrorCode == ScriptErrorCodes.ModuleCycle ? "cycle" : "limit_exceeded"; }
                    items.Add(new CodeModuleListItem(record, status));
                }
                return new CodeModulePage(items, names.Length);
            });
        }

        private static CodeModuleSnapshot Resolve(SqliteConnection connection, SqliteTransaction transaction, string[] roots, ScriptModuleRevision candidate = null)
        {
            var loaded = new Dictionary<string, ScriptModuleRevision>(StringComparer.Ordinal);
            var heights = new Dictionary<string, int>(StringComparer.Ordinal);
            var active = new HashSet<string>(StringComparer.Ordinal); var order = new List<ScriptModuleRevision>(); long bytes = 0;
            void Visit(string name, int depth)
            {
                if (depth > ScriptLimits.DependencyDepth) throw Error(ScriptErrorCodes.ModuleLimitExceeded, "Module dependency graph is too deep.");
                if (active.Contains(name)) throw Error(ScriptErrorCodes.ModuleCycle, "Module dependency cycle.");
                if (heights.TryGetValue(name, out var height))
                {
                    if (depth + height - 1 > ScriptLimits.DependencyDepth) throw Error(ScriptErrorCodes.ModuleLimitExceeded, "Module dependency graph is too deep.");
                    return;
                }
                var revision = candidate?.Name == name ? candidate : ReadHead(connection, transaction, name)?.Revision;
                if (revision == null) throw Error(ScriptErrorCodes.ModuleNotFound, "A required module does not exist.");
                loaded.Add(name, revision); bytes += ScriptIdentity.Utf8Bytes(revision.Code);
                if (loaded.Count > ScriptLimits.ModuleCount || bytes > ScriptLimits.ResolvedModuleBytes)
                    throw Error(ScriptErrorCodes.ModuleLimitExceeded, "Resolved modules exceed their budget.");
                active.Add(name); foreach (var dependency in revision.Requires) Visit(dependency, depth + 1); active.Remove(name);
                heights[name] = 1 + revision.Requires.Select(d => heights[d]).DefaultIfEmpty(0).Max(); order.Add(revision);
            }
            foreach (var root in roots) Visit(root, 1);
            return new CodeModuleSnapshot(roots, order);
        }

        private T Read<T>(Func<SqliteConnection, SqliteTransaction, T> action) => Access(readOnly: true, action);
        private T Write<T>(Func<SqliteConnection, SqliteTransaction, T> action) => Access(readOnly: false, action);
        private T Access<T>(bool readOnly, Func<SqliteConnection, SqliteTransaction, T> action)
        {
            try
            {
                using var connection = _db.OpenModuleConnection(readOnly);
                using var transaction = connection.BeginTransaction(deferred: readOnly);
                BakeDb.ValidateModuleSchema(connection, transaction);
                var result = action(connection, transaction); transaction.Commit(); return result;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1 || ex.SqliteErrorCode == 11 || ex.SqliteErrorCode == 14 || ex.SqliteErrorCode == 26)
            { throw Corrupt(); }
        }

        private static List<string> Names(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT name FROM code_modules ORDER BY name COLLATE BINARY";
            using var reader = command.ExecuteReader(); var names = new List<string>();
            while (reader.Read()) { if (reader.IsDBNull(0)) throw Corrupt(); names.Add(reader.GetString(0)); } return names;
        }

        private static CodeModuleRecord ReadHead(SqliteConnection connection, SqliteTransaction transaction, string name)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT revision_hash,description,signatures_json,compiled_host_json,updated_at FROM code_modules WHERE name=$name";
            command.Parameters.AddWithValue("$name", name);
            string hash, description, signatures, host, updated;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) return null;
                if (Enumerable.Range(0, reader.FieldCount).Any(reader.IsDBNull)) throw Corrupt();
                hash = reader.GetString(0); description = reader.GetString(1); signatures = reader.GetString(2); host = reader.GetString(3); updated = reader.GetString(4);
            }
            var revision = ReadRevision(connection, transaction, hash); if (revision == null || revision.Name != name) throw Corrupt();
            try
            {
                if (JArray.Parse(signatures).Any(t => t.Type != JTokenType.String) || !(JObject.Parse(host)["compile_verified"]?.Value<bool>() == true)) throw Corrupt();
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidCastException || ex is ArgumentException) { throw Corrupt(); }
            return new CodeModuleRecord(revision, description, signatures, host, updated);
        }

        private static ScriptModuleRevision ReadRevision(SqliteConnection connection, SqliteTransaction transaction, string hash)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT name,code,code_sha256,requires_json FROM code_module_revisions WHERE revision_hash=$hash";
            command.Parameters.AddWithValue("$hash", hash); using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
            if (Enumerable.Range(0, reader.FieldCount).Any(reader.IsDBNull)) throw Corrupt();
            try
            {
                var requires = JArray.Parse(reader.GetString(3)); if (requires.Any(t => t.Type != JTokenType.String)) throw Corrupt();
                var revision = new ScriptModuleRevision(reader.GetString(0), reader.GetString(1), requires.Values<string>());
                if (revision.Hash != hash || revision.CodeSha256 != reader.GetString(2) || revision.Code != reader.GetString(1) || revision.Requires.Count > ScriptLimits.ModuleCount) throw Corrupt();
                return revision;
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is InvalidCastException) { throw Corrupt(); }
        }

        private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value); command.ExecuteNonQuery();
        }
        private static void CheckExpected(CodeModuleRecord current, string expected)
        {
            if (current?.Revision.Hash != expected) throw Error(ScriptErrorCodes.ModuleConflict, "Module head does not match expected_hash.");
        }
        private static void CheckHash(string hash, bool optional)
        {
            if (hash == null && optional) return;
            if (hash == null || !Regex.IsMatch(hash, "\\A[a-fA-F0-9]{64}\\z")) throw Error(ScriptErrorCodes.InputInvalid, "A valid expected_hash is required.");
        }
        private static readonly Regex CredentialAssignment = new Regex(@"(?i)\b(?:password|api_?key|access_?token|client_?secret|authorization)\b\s*=\s*@?""[^""]+""", RegexOptions.CultureInvariant);
        private static void CheckPersistence(string source)
        {
            var policyText = source.Replace("\\\"", "\"").Replace("\"\"", "\"");
            if (SecretMasker.Mask(policyText) != policyText || CredentialAssignment.IsMatch(source))
                throw Error(ScriptErrorCodes.SourceNotStorable, "Persisted source contains credential-like values; supply them at runtime instead.");
        }
        private static ScriptContractException Error(string code, string message) => new ScriptContractException(code, message);
        private static ScriptContractException Corrupt() => Error(ScriptErrorCodes.ModuleStoreCorrupt, "Module storage failed integrity validation; it was not reset.");
    }
}
