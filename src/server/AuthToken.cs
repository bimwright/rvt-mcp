using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Bimwright.Targeting;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    /// <summary>
    /// Snapshot of a Revit plugin advertising itself via a discovery file in
    /// %LOCALAPPDATA%\Bimwright\rvt-mcp\revit-YYYY.json.
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

    /// <summary>
    /// Discovery-file scanner. Reads every revit-*.json descriptor (per-instance
    /// schema v3 and legacy schema 2), normalizes through the shared core and
    /// evaluates liveness. Only descriptors whose owner pid is proven dead are
    /// deleted; malformed files are skipped, never deleted (spec §5.3, §5.6).
    /// </summary>
    internal static class AuthToken
    {
        /// <summary>All valid Revit calendar years.</summary>
        public static readonly string[] AllVersions = { "2022", "2023", "2024", "2025", "2026", "2027" };

        /// <summary>Tests redirect discovery reads to a temp dir via this override.</summary>
        internal static string DiscoveryDirOverride;

        public static string DiscoveryDir()
        {
            return DiscoveryDirOverride ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Bimwright", "rvt-mcp");
        }

        public static string DiscoveryFileName(string year)
        {
            return "revit-" + year + ".json";
        }

        /// <summary>
        /// Scan the discovery directory (default: production dir; pass <paramref name="dir"/>
        /// from tests). Live candidates come back in §6.3 order. Files whose owner pid is
        /// proven dead are deleted here — scanning is the single place that garbage-collects.
        /// </summary>
        public static ScanResult Scan(IProcessProbe probe, string dir = null)
        {
            dir ??= DiscoveryDir();
            var descriptors = new List<TargetDescriptor>();
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir, "revit-*.json"))
                {
                    JObject json;
                    try
                    {
                        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                                   FileShare.ReadWrite | FileShare.Delete))
                        using (var sr = new StreamReader(fs))
                            json = JObject.Parse(sr.ReadToEnd());
                    }
                    catch { continue; } // unreadable/malformed — skip, never delete

                    if (DescriptorNormalizer.TryNormalize(HostProduct.Revit,
                            Path.GetFileName(file), json, out var d, out _))
                        descriptors.Add(d);
                }
            }

            var result = TargetScan.Evaluate(descriptors, probe);
            foreach (var dead in result.ProvenDead)
            {
                try { File.Delete(Path.Combine(dir, dead.FileName)); } catch { /* best-effort */ }
            }
            return result;
        }

        /// <summary>
        /// Descriptor for one specific bound instance: its per-instance file
        /// (revit-&lt;year&gt;-&lt;pid&gt;.json), else a legacy revit-&lt;year&gt;.json with
        /// the same pid (spec §6.4 step 2). Returns null when absent.
        /// </summary>
        internal static TargetDescriptor ReadDescriptorFor(int pid, int hostYear, string dir = null)
        {
            dir ??= DiscoveryDir();
            if (!Directory.Exists(dir)) return null;
            var perInstance = Path.Combine(dir, $"revit-{hostYear}-{pid}.json");
            if (TryReadDescriptorFile(perInstance, out var d) && d.Pid == pid) return d;
            var legacy = Path.Combine(dir, $"revit-{hostYear}.json");
            if (TryReadDescriptorFile(legacy, out d) && d.Pid == pid) return d;
            return null;
        }

        private static bool TryReadDescriptorFile(string path, out TargetDescriptor d)
        {
            d = null;
            try
            {
                JObject json;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs))
                    json = JObject.Parse(sr.ReadToEnd());
                return DescriptorNormalizer.TryNormalize(HostProduct.Revit,
                    Path.GetFileName(path), json, out d, out _);
            }
            catch { return false; }
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

        /// <summary>
        /// Legacy single-file parser kept for tests. Liveness check retained: a file whose
        /// owner pid is proven dead is deleted (same rule as <see cref="Scan"/>).
        /// </summary>
        internal static bool TryParseDiscovery(string path, out DiscoveredRevit result)
        {
            result = null;
            try
            {
                var raw = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(raw)) return false;
                var obj = JObject.Parse(raw);

                var d = new DiscoveredRevit
                {
                    Year      = obj.Value<string>("revit_year") ?? obj.Value<string>("host_year"),
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
