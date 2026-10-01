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

            Directory.CreateDirectory(current);

            foreach (var sub in new[] { "baked", "journal", "firm-profiles" })
            {
                var src = Path.Combine(legacy, sub);
                var dst = Path.Combine(current, sub);
                if (Directory.Exists(src) && !Directory.Exists(dst))
                {
                    CopyDirectory(src, dst);
                }
            }

            foreach (var log in Directory.Exists(legacy)
                ? Directory.GetFiles(legacy, "*.log")
                : Array.Empty<string>())
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
