using System;
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

        public static string Validate(string path)
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

                return $"output_path must be inside %TEMP% ({temp}) or %LOCALAPPDATA%\\Bimwright\\rvt-mcp\\captures\\ ({captures}).";
            }
            catch (Exception ex)
            {
                return $"Invalid output_path: {ex.Message}";
            }
        }
    }
}
