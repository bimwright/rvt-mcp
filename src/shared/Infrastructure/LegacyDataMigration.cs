using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace RvtMcp.Plugin
{
    internal static class LegacyDataMigration
    {
        private static readonly string[] UserFolders = { "baked", "journal", "firm-profiles", "captures", "locales", "projects", "history-pending", "history-quarantine" };
        private static readonly string[] FamilyRootFiles = { "rvtmcp.config.json", "bake.db", "mcp-calls.jsonl", "mcp-calls.version", "send-code-journal.jsonl", "send-code-journal.disabled-at", "usage.jsonl", "revit-mcp.log", "debug.log" };

        public static bool TryMigrateOnce(Action<string> log, string localAppDataPath = null)
        {
            try { MigrateOnce(localAppDataPath); return true; }
            catch (Exception ex)
            {
                HistoryDiagnostics.Report("data_migration", ex, log: log);
                log("[RvtMcp] MIGRATION_REQUIRED: legacy data was preserved. Migration failed or current data conflicts with it. Resolve the legacy/current data folders before retrying; the gateway will not start with fallback settings.");
                return false;
            }
        }

        public static void MigrateOnce(string localAppDataPath = null)
        {
            var local = localAppDataPath ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var family = Path.Combine(local, "Bimwright");
            var current = Path.Combine(family, "rvt-mcp");
            var old = Path.Combine(local, "RvtMcp");
            bool oldExists = Directory.Exists(old);
            // A sibling product or a lone shared bake.db does not prove this is legacy RVT data.
            bool familyHasRvt = File.Exists(Path.Combine(family, "rvtmcp.config.json"))
                || File.Exists(Path.Combine(family, "revit-mcp.log"))
                || new[] { "baked", "journal", "firm-profiles" }.Any(d => Directory.Exists(Path.Combine(family, d)));
            if (!oldExists && !familyHasRvt) return;
            string key;
            using (var hash = SHA256.Create())
                key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(current).ToUpperInvariant()))).Replace("-", "");
            using (var gate = new Mutex(false, @"Local\RvtMcp.Migration." + key))
            {
                bool acquired = false;
                try
                {
                    try { acquired = gate.WaitOne(TimeSpan.FromSeconds(5)); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new IOException("Data migration is in use by another process.");
                    if (oldExists) CopyRoot(old, current, ".migrated-from-rvtmcp-v2", false);
                    if (familyHasRvt) CopyRoot(family, current, ".migrated-from-bimwright-v2", true);
                }
                finally { if (acquired) gate.ReleaseMutex(); }
            }
        }

        private static void CopyRoot(string source, string destination, string markerName, bool familyRoot)
        {
            var marker = Path.Combine(destination, markerName);
            if (File.Exists(marker)) return;
            CheckDirectory(source);
            Directory.CreateDirectory(destination); CheckDirectory(destination);
            foreach (var folder in UserFolders)
            {
                var path = Path.Combine(source, folder);
                if (Directory.Exists(path)) CopyDirectory(path, Path.Combine(destination, folder));
            }
            foreach (var file in Directory.GetFiles(source).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(file);
                if (familyRoot && !FamilyRootFiles.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (name.StartsWith(".", StringComparison.Ordinal) || name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("revit-20", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(destination, name);
                // Startup diagnostics may already exist. Keep current logs and the legacy
                // originals; they are not configuration or executable tool state.
                if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) && File.Exists(target)) continue;
                CopyFile(file, target);
            }
            // All copies are complete; sources remain available as recovery data.
            File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
        }

        private static void CheckDirectory(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Migration refuses linked data directories.");
        }
        private static void CopyDirectory(string source, string destination)
        {
            CheckDirectory(source); Directory.CreateDirectory(destination); CheckDirectory(destination);
            foreach (var file in Directory.GetFiles(source).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (file.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) || file.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)) continue;
                CopyFile(file, Path.Combine(destination, Path.GetFileName(file)));
            }
            foreach (var directory in Directory.GetDirectories(source).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
        private static void CopyFile(string source, string destination)
        {
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0
                || File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Migration refuses linked data files.");
            var temporary = destination + ".migration-" + Guid.NewGuid().ToString("N");
            try
            {
                if (source.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                {
                    // Include committed WAL content without copying live database files separately.
                    using (var input = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString()))
                    using (var output = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary, Pooling = false }.ToString()))
                    {
                        input.Open(); output.Open(); input.BackupDatabase(output);
                        using (var check = output.CreateCommand())
                        {
                            check.CommandText = "PRAGMA integrity_check";
                            if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.Ordinal)) throw new IOException("Migrated database failed integrity check.");
                        }
                    }
                }
                else File.Copy(source, temporary, false);
                if (File.Exists(destination))
                {
                    if (!SameBytes(temporary, destination)) throw new IOException("Legacy/current data conflict; neither file was overwritten.");
                    return;
                }
                File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static bool SameBytes(string left, string right)
        {
            if (new FileInfo(left).Length != new FileInfo(right).Length) return false;
            using (var a = File.OpenRead(left))
            using (var b = File.OpenRead(right))
            using (var hash = SHA256.Create())
                return hash.ComputeHash(a).SequenceEqual(hash.ComputeHash(b));
        }
    }
}
