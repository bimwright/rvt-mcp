using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using RvtMcp.Scripting;

namespace RvtMcp.Server.Bake
{
    public sealed partial class BakeDb
    {
        internal const int ModuleSchemaVersion = 2;
        internal string MigrationBackupPath { get; private set; }
        internal Action MigrationBeforeCommitForTests { get; set; }

        internal SqliteConnection OpenModuleConnection(bool readOnly, bool create = false)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = _dbPath, Mode = readOnly ? SqliteOpenMode.ReadOnly : create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 5, ForeignKeys = true }.ToString());
            try { connection.Open(); return connection; }
            catch { connection.Dispose(); throw; }
        }

        private static long Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }

        private static int ReadSchemaVersion(SqliteConnection connection, SqliteTransaction transaction)
        {
            if (Scalar(connection, transaction, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_version'") == 0) return 0;
            if (Scalar(connection, transaction, "SELECT COUNT(*) FROM schema_version") != 1)
                throw new ScriptContractException(ScriptErrorCodes.ModuleStoreCorrupt, "Invalid bake database schema version record.");
            var version = Scalar(connection, transaction, "SELECT version FROM schema_version");
            if (version < 1 || version > int.MaxValue)
                throw new ScriptContractException(ScriptErrorCodes.ModuleStoreCorrupt, "Invalid bake database schema version.");
            return (int)version;
        }

        private static bool HasModuleTables(SqliteConnection connection, SqliteTransaction transaction)
            => Scalar(connection, transaction, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('code_modules','code_module_revisions')") != 0;

        private void BackupBeforeModuleMigration(SqliteConnection connection, SqliteTransaction transaction)
        {
            if (Scalar(connection, transaction, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'") == 0) return;
            var path = _dbPath + ".pre-v2-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".bak";
            // The immediate transaction excludes writers. A separate private-cache reader backs up committed WAL pages.
            using var source = OpenModuleConnection(readOnly: true);
            using var backup = new SqliteConnection(new SqliteConnectionStringBuilder {DataSource = path, Pooling = false}.ToString());
            backup.Open(); source.BackupDatabase(backup);
            MigrationBackupPath = path;
        }

        private void CreateModuleSchema(SqliteConnection connection, SqliteTransaction transaction)
        {
            Execute(connection, transaction, @"
CREATE TABLE code_module_revisions(
 revision_hash TEXT PRIMARY KEY, name TEXT NOT NULL, code TEXT NOT NULL, code_sha256 TEXT NOT NULL,
 requires_json TEXT NOT NULL, created_at TEXT NOT NULL
);
CREATE TABLE code_modules(
 name TEXT PRIMARY KEY, revision_hash TEXT NOT NULL REFERENCES code_module_revisions(revision_hash),
 description TEXT NOT NULL, signatures_json TEXT NOT NULL, compiled_host_json TEXT NOT NULL, updated_at TEXT NOT NULL
);
CREATE TRIGGER code_module_revisions_no_update BEFORE UPDATE ON code_module_revisions
 BEGIN SELECT RAISE(ABORT,'Module revisions are immutable'); END;
CREATE TRIGGER code_module_revisions_no_delete BEFORE DELETE ON code_module_revisions
 BEGIN SELECT RAISE(ABORT,'Module revisions are immutable'); END;");
            MigrationBeforeCommitForTests?.Invoke();
        }

        internal static void ValidateModuleSchema(SqliteConnection connection, SqliteTransaction transaction)
        {
            var version = ReadSchemaVersion(connection, transaction);
            if (version > ModuleSchemaVersion)
                throw new ScriptContractException(ScriptErrorCodes.ModuleStoreCorrupt, "Module store uses a newer schema than this build supports; no downgrade was applied.");
            if (version != ModuleSchemaVersion)
                throw new ScriptContractException(ScriptErrorCodes.ModuleStoreCorrupt, "Module schema is not available; migration is required.");
            foreach (var table in new[] {"code_module_revisions", "code_modules"})
            {
                var expected = table == "code_modules" ? new[] {"name", "revision_hash", "description", "signatures_json", "compiled_host_json", "updated_at"}
                    : new[] {"revision_hash", "name", "code", "code_sha256", "requires_json", "created_at"};
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT name FROM pragma_table_info($table)"; command.Parameters.AddWithValue("$table", table);
                using var reader = command.ExecuteReader(); var columns = new System.Collections.Generic.List<string>();
                while (reader.Read()) columns.Add(reader.GetString(0));
                if (!columns.OrderBy(c => c, StringComparer.Ordinal).SequenceEqual(expected.OrderBy(c => c, StringComparer.Ordinal)))
                    throw new ScriptContractException(ScriptErrorCodes.ModuleStoreCorrupt, "Module table schema is invalid.");
            }
            if (Scalar(connection, transaction, "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name IN ('code_module_revisions_no_update','code_module_revisions_no_delete')") != 2)
                throw new ScriptContractException(ScriptErrorCodes.ModuleStoreCorrupt, "Module revision immutability guards are missing.");
            if (Scalar(connection, transaction, "SELECT COUNT(*) FROM pragma_table_info('code_modules') WHERE name='name' AND pk=1 AND type='TEXT'") != 1
                || Scalar(connection, transaction, "SELECT COUNT(*) FROM pragma_table_info('code_module_revisions') WHERE name='revision_hash' AND pk=1 AND type='TEXT'") != 1
                || Scalar(connection, transaction, "SELECT COUNT(*) FROM pragma_foreign_key_list('code_modules') WHERE \"table\"='code_module_revisions' AND \"from\"='revision_hash' AND \"to\"='revision_hash'") != 1)
                throw new ScriptContractException(ScriptErrorCodes.ModuleStoreCorrupt, "Module key constraints are invalid.");
        }
    }
}
