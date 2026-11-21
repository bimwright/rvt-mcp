using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RvtMcp.Plugin
{
    public static class CaptureOutputPath
    {
        public static string NormalizeOrDefault(string outputPath, string imageFormat)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                var format = (imageFormat ?? "png").ToLowerInvariant();
                var ext = format == "jpeg" ? "jpg" : format;
                var guid = Guid.NewGuid().ToString("N").Substring(0, 8);
                var fileName = $"capture-{DateTime.Now:yyyyMMdd-HHmmss}-{guid}.{ext}";
                return Path.Combine(PathAllowlist.CapturesDirectory, fileName);
            }
            return outputPath;
        }

        /// <summary>Folders the owner added with captureRoots / BIMWRIGHT_CAPTURE_ROOTS; empty when unset or unreadable.</summary>
        public static IReadOnlyList<string> ConfiguredRoots()
        {
            try { return RvtMcpConfig.LoadReadOnly().CaptureRootsOrEmpty; }
            catch { return new string[0]; }
        }

        public const int RetentionDays = 30;
        public const long RetentionBytes = 200L * 1024 * 1024;

        // The name NormalizeOrDefault generates, plus the view suffix Revit appends to it.
        private static readonly Regex Generated = new Regex(@"^capture-\d{8}-\d{6}-[0-9a-f]{8}.*\.(png|jpg)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Bounds the captures folder the way log groups are bounded: generated captures older than
        /// <paramref name="days"/> go, then the oldest until the rest fits <paramref name="budget"/>. Only files
        /// with a generated name directly in <paramref name="directory"/> are touched; a file the caller named
        /// is never deleted. Never throws: a capture must not fail because an old one is locked.
        /// </summary>
        public static int PruneGenerated(string directory, string keep, DateTime utcNow, int days = RetentionDays, long budget = RetentionBytes)
        {
            var deleted = 0;
            try
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return 0;
                var files = new DirectoryInfo(directory).GetFiles("capture-*")
                    .Where(f => Generated.IsMatch(f.Name) && (f.Attributes & FileAttributes.ReparsePoint) == 0)
                    .OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();
                var total = files.Sum(f => f.Length);
                var cutoff = utcNow.AddDays(-days);
                foreach (var file in files)
                {
                    if (string.Equals(file.FullName, keep, StringComparison.OrdinalIgnoreCase)) continue;
                    if (file.LastWriteTimeUtc >= cutoff && total <= budget) break;
                    try { var length = file.Length; file.Delete(); total -= length; deleted++; }
                    catch { }
                }
            }
            catch { }
            return deleted;
        }

        /// <summary>Every folder a capture may be written into, as full paths.</summary>
        public static string[] AllowedRoots()
        {
            var roots = new List<string> { PathAllowlist.TempDirectory, PathAllowlist.UserTempDirectory, PathAllowlist.CapturesDirectory };
            roots.AddRange(ConfiguredRoots());
            var distinct = new List<string>();
            foreach (var root in roots)
                if (!distinct.Exists(r => string.Equals(r.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                    distinct.Add(root);
            return distinct.ToArray();
        }

        public static string Validate(string path) => Validate(path, ConfiguredRoots());

        internal static string Validate(string path, IReadOnlyList<string> extraRoots)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "output_path is required after normalization.";

            if (path.IndexOf("%TEMP%", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var temp = PathAllowlist.TempDirectory;
                var captures = PathAllowlist.CapturesDirectory;
                return $"output_path contains unexpanded environment variables like %TEMP% or %LOCALAPPDATA%. " +
                       $"You must expand these to their absolute folder paths before calling this tool. " +
                       $"Resolved allowlist roots: %TEMP% is '{temp}', and %LOCALAPPDATA%\\Bimwright\\rvt-mcp\\captures\\ is '{captures}'.";
            }

            if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return "UNC paths are not allowed.";
            if (path.Contains("..")) return "output_path cannot contain '..'.";

            try
            {
                var full = Path.GetFullPath(path);
                if (!string.Equals(full, path, StringComparison.OrdinalIgnoreCase))
                    return $"output_path must be canonical. Did you mean: {full}";

                var temp = PathAllowlist.TempDirectory;
                var captures = PathAllowlist.CapturesDirectory;

                if (PathAllowlist.IsUnderTempOrCaptures(full)) return null;
                foreach (var root in extraRoots)
                    if (PathAllowlist.IsUnderRoot(full, root)) return null;

                var allowed = $"output_path must be inside %TEMP% ({temp}) or %LOCALAPPDATA%\\Bimwright\\rvt-mcp\\captures\\ ({captures})";
                foreach (var root in extraRoots) allowed += $" or {root}";
                // Paths in an error text are masked on the way out, so the response also carries them as allowed_roots.
                // What the agent can do itself comes first; the setting is the owner's to change.
                return allowed + "; allowed_roots lists the full paths. Omit output_path (the response returns saved_path) or use one of these folders, " +
                       "then copy the file where it is needed. To allow another folder, the user adds it to captureRoots in " +
                       $"rvtmcp.config.json or to {RvtMcpConfig.EnvCaptureRoots} (';' separated) in the Revit process environment.";
            }
            catch (Exception ex)
            {
                return $"Invalid output_path: {ex.Message}";
            }
        }
    }
}
