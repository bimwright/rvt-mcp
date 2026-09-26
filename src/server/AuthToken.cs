using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    /// <summary>
    /// Snapshot of a Revit plugin advertising itself via a discovery file in
    /// %LOCALAPPDATA%\RvtMcp\revit-YYYY.json.
    /// </summary>
    internal sealed class DiscoveredRevit
    {
        public string Year { get; set; }              // "2022".."2027"
        public string Transport { get; set; }         // "tcp" | "pipe"
        public int Port { get; set; }                  // populated when Transport == "tcp"
        public string PipeName { get; set; }           // populated when Transport == "pipe"
        public string AuthToken { get; set; }
        public int Pid { get; set; }
        public List<string> Capabilities { get; set; }
        public string DiscoveryFilePath { get; set; }
    }

    internal static class AuthToken
    {
        /// <summary>
        /// Pinned Revit version from --target CLI / config. null = auto-detect.
        /// Stored as a 4-digit year string ("2024"), NOT an R-code.
        /// </summary>
        public static string Target { get; set; }

        /// <summary>All valid --target values (4-digit calendar years).</summary>
        public static readonly string[] AllVersions = { "2022", "2023", "2024", "2025", "2026", "2027" };

        // Auto-detect preference: Named Pipe (R25+) first because it's faster on modern
        // Windows than loopback TCP. Within each family, newest year first.
        private static readonly string[] PipePriority = { "2027", "2026", "2025" };
        private static readonly string[] TcpPriority  = { "2024", "2023", "2022" };

        public static string DiscoveryDir()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RvtMcp");
        }

        public static string DiscoveryFileName(string year)
        {
            return "revit-" + year + ".json";
        }

        /// <summary>
        /// Returns every alive Revit plugin advertising itself on this machine.
        /// Used by the revit_list_available_targets MCP tool.
        /// </summary>
        public static IReadOnlyList<DiscoveredRevit> ListAvailable()
        {
            var results = new List<DiscoveredRevit>();
            var dir = DiscoveryDir();
            if (!Directory.Exists(dir)) return results;

            foreach (var year in AllVersions)
            {
                var path = Path.Combine(dir, DiscoveryFileName(year));
                if (!File.Exists(path)) continue;
                if (TryParseDiscovery(path, out var d)) results.Add(d);
            }
            return results;
        }

        public static bool TryReadTcp(out int port, out string token, out string version)
        {
            return TryReadTcp(out port, out token, out version, out _);
        }

        public static bool TryReadTcp(out int port, out string token, out string version, out IReadOnlyList<string> capabilities)
        {
            port = 0;
            token = null;
            version = null;
            capabilities = Array.Empty<string>();

            var preferred = Target != null ? new[] { Target } : TcpPriority;
            foreach (var year in preferred)
            {
                var d = TryReadOne(year);
                if (d == null) continue;
                if (!string.Equals(d.Transport, "tcp", StringComparison.OrdinalIgnoreCase)) continue;
                port = d.Port;
                token = d.AuthToken;
                version = d.Year;
                capabilities = d.Capabilities ?? new List<string>();
                return true;
            }
            return false;
        }

        public static bool TryReadPipe(out string pipeName, out string token, out string version)
        {
            return TryReadPipe(out pipeName, out token, out version, out _);
        }

        public static bool TryReadPipe(out string pipeName, out string token, out string version, out IReadOnlyList<string> capabilities)
        {
            pipeName = null;
            token = null;
            version = null;
            capabilities = Array.Empty<string>();

            var preferred = Target != null ? new[] { Target } : PipePriority;
            foreach (var year in preferred)
            {
                var d = TryReadOne(year);
                if (d == null) continue;
                if (!string.Equals(d.Transport, "pipe", StringComparison.OrdinalIgnoreCase)) continue;
                pipeName = d.PipeName;
                token = d.AuthToken;
                version = d.Year;
                capabilities = d.Capabilities ?? new List<string>();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Delete leftover v0.4-and-earlier discovery files (portR22.txt, pipeR25.txt, …)
        /// from the discovery dir so they don't confuse troubleshooting after upgrade.
        /// Called once at server startup.
        /// </summary>
        public static void CleanupLegacyDiscoveryFiles()
        {
            var dir = DiscoveryDir();
            if (!Directory.Exists(dir)) return;
            try
            {
                foreach (var pattern in new[] { "portR*.txt", "pipeR*.txt" })
                {
                    foreach (var file in Directory.GetFiles(dir, pattern))
                    {
                        try { File.Delete(file); }
                        catch { /* best-effort */ }
                    }
                }
            }
            catch { /* best-effort */ }
        }

        private static DiscoveredRevit TryReadOne(string year)
        {
            var path = Path.Combine(DiscoveryDir(), DiscoveryFileName(year));
            if (!File.Exists(path)) return null;
            return TryParseDiscovery(path, out var d) ? d : null;
        }

        private static bool TryParseDiscovery(string path, out DiscoveredRevit result)
        {
            result = null;
            try
            {
                var raw = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(raw)) return false;
                var obj = JObject.Parse(raw);

                var d = new DiscoveredRevit
                {
                    Year      = obj.Value<string>("revit_year"),
                    Transport = (obj.Value<string>("transport") ?? string.Empty).ToLowerInvariant(),
                    Port      = obj.Value<int?>("port") ?? 0,
                    PipeName  = obj.Value<string>("pipe_name"),
                    AuthToken = obj.Value<string>("auth_token"),
                    Pid       = obj.Value<int?>("pid") ?? 0,
                    Capabilities = obj["capabilities"] is JArray capabilityArray
                        ? capabilityArray.Values<string>()
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList()
                        : new List<string>(),
                    DiscoveryFilePath = path
                };

                if (string.IsNullOrEmpty(d.Year) || string.IsNullOrEmpty(d.AuthToken))
                    return false;
                if (d.Transport != "tcp" && d.Transport != "pipe")
                    return false;
                if (d.Transport == "tcp" && d.Port <= 0)
                    return false;
                if (d.Transport == "pipe" && string.IsNullOrEmpty(d.PipeName))
                    return false;

                if (!IsOwnerAlive(d.Pid, path)) return false;

                result = d;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// PID liveness check: if the Revit process that wrote this discovery file is gone,
        /// the file is orphaned (Revit crashed without OnShutdown firing) — delete it so we
        /// don't burn a 5-second connect waiting on a ghost. PID 0 means legacy file with no
        /// PID, which we accept.
        /// </summary>
        private static bool IsOwnerAlive(int pid, string discoveryFilePath)
        {
            if (pid <= 0) return true;

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
                alive = false;
            }
            catch
            {
                return true; // access denied — don't delete a file we can't verify
            }

            if (!alive)
            {
                try { File.Delete(discoveryFilePath); } catch { }
            }
            return alive;
        }
    }
}
