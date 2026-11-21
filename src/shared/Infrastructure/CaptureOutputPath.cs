using System;
using System.Collections.Generic;
using System.IO;

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
                // What the agent can do itself comes first; the setting is the owner's to change.
                return allowed + ". Omit output_path (the response returns saved_path) or use one of these folders, " +
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
