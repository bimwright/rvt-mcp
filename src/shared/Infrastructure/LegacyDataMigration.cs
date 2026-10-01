using System;
using System.IO;

namespace RvtMcp.Plugin
{
    internal static class LegacyDataMigration
    {
        public static void MigrateOnce(string localAppDataPath = null)
        {
            var local = string.IsNullOrEmpty(localAppDataPath)
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : localAppDataPath;
            var legacy = Path.Combine(local, "Bimwright");
            var current = Path.Combine(local, "Bimwright", "rvt-mcp");
            var marker = Path.Combine(current, ".migrated-from-bimwright");

            if (!Directory.Exists(legacy)) return;
            if (File.Exists(marker)) return;

            // Bimwright is now shared by the family; its existence alone is not
            // evidence of a pre-rename Revit installation.
            var folders = new[] { "baked", "journal", "firm-profiles" };
            var logs = Directory.GetFiles(legacy, "*.log");
            if (!Array.Exists(folders, sub => Directory.Exists(Path.Combine(legacy, sub)))
                && logs.Length == 0) return;

            Directory.CreateDirectory(current);

            foreach (var sub in folders)
            {
                var src = Path.Combine(legacy, sub);
                var dst = Path.Combine(current, sub);
                if (Directory.Exists(src) && !Directory.Exists(dst))
                {
                    CopyDirectory(src, dst);
                }
            }

            foreach (var log in logs)
            {
                var dst = Path.Combine(current, Path.GetFileName(log));
                if (!File.Exists(dst)) File.Copy(log, dst);
            }

            File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
        }

        private static void CopyDirectory(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var file in Directory.GetFiles(src))
                File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: false);
            foreach (var dir in Directory.GetDirectories(src))
                CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
        }
    }
}
