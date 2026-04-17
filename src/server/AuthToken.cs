using System;
using System.Diagnostics;
using System.IO;

namespace RevitMcp.Server
{
    internal static class AuthToken
    {
        /// <summary>Target filter set by --target CLI arg. null = auto-detect.</summary>
        public static string Target { get; set; }

        private static readonly string[] TcpVersions = { "R24", "R23", "R22" };
        private static readonly string[] PipeVersions = { "R27", "R26", "R25" };

        /// <summary>All valid --target values.</summary>
        public static readonly string[] AllVersions = { "R22", "R23", "R24", "R25", "R26", "R27" };

        public static bool TryReadTcp(out int port, out string token, out string version)
        {
            port = 0;
            token = null;
            version = null;

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevitMcp");

            // If target specified, only try that version
            var versions = Target != null ? new[] { Target } : TcpVersions;

            foreach (var ver in versions)
            {
                var path = Path.Combine(dir, $"port{ver}.txt");
                if (!File.Exists(path)) continue;
                var lines = File.ReadAllLines(path);
                if (lines.Length < 2) continue;
                if (!int.TryParse(lines[0].Trim(), out port)) continue;
                token = lines[1].Trim();
                if (string.IsNullOrEmpty(token)) continue;

                if (!IsOwnerAlive(lines, path)) continue;

                version = ver;
                return true;
            }
            return false;
        }

        public static bool TryReadPipe(out string pipeName, out string token, out string version)
        {
            pipeName = null;
            token = null;
            version = null;

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevitMcp");

            // If target specified, only try that version
            var versions = Target != null ? new[] { Target } : PipeVersions;

            foreach (var ver in versions)
            {
                var path = Path.Combine(dir, $"pipe{ver}.txt");
                if (!File.Exists(path)) continue;
                var lines = File.ReadAllLines(path);
                if (lines.Length < 2) continue;
                pipeName = lines[0].Trim();
                token = lines[1].Trim();
                if (string.IsNullOrEmpty(pipeName) || string.IsNullOrEmpty(token)) continue;

                if (!IsOwnerAlive(lines, path)) continue;

                version = ver;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Discovery files (v2) include the owning Revit PID on line 3. If that PID is dead,
        /// the file is orphaned (Revit crashed without OnShutdown firing) and we delete it so
        /// we don't waste a 5-second pipe/TCP connect waiting on a ghost.
        /// Files missing line 3 are treated as legacy (v1) and skipped — we let the connect
        /// attempt succeed or fail naturally.
        /// </summary>
        private static bool IsOwnerAlive(string[] lines, string discoveryFilePath)
        {
            if (lines.Length < 3) return true; // legacy file, no PID to check
            if (!int.TryParse(lines[2].Trim(), out var pid) || pid <= 0) return true;

            bool alive;
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    alive = !p.HasExited;
                }
            }
            catch (ArgumentException)
            {
                alive = false; // PID not found → process is gone
            }
            catch
            {
                return true; // access denied etc — don't delete a file we can't verify
            }

            if (!alive)
            {
                try { File.Delete(discoveryFilePath); } catch { }
            }
            return alive;
        }
    }
}
