using System;
using System.IO;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// Directory-boundary checks for capture/toast paths. Uses a trailing separator
    /// so sibling folders (e.g. capturesEvil) cannot pass a StartsWith(root) check.
    /// </summary>
    public static class PathAllowlist
    {
        public static string CapturesDirectory =>
            Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Bimwright", "rvt-mcp",
                "captures"));

        public static string TempDirectory => Path.GetFullPath(Path.GetTempPath());

        /// <summary>
        /// The user's own temp folder. Revit points its process TEMP at a per-session subfolder of it, so a
        /// path a client builds from the user's %TEMP% lies outside <see cref="TempDirectory"/>.
        /// </summary>
        public static string UserTempDirectory
        {
            get
            {
                string user = null;
                try { user = Environment.GetEnvironmentVariable("TEMP", EnvironmentVariableTarget.User); } catch { }
                return UserTemp(TempDirectory, user);
            }
        }

        internal static string UserTemp(string processTemp, string userTemp)
        {
            try
            {
                var text = Environment.ExpandEnvironmentVariables((userTemp ?? "").Trim());
                if (text.Length >= 3 && text[1] == ':' && (text[2] == '\\' || text[2] == '/') && !text.Contains("%"))
                    return Path.GetFullPath(text);
            }
            catch { }
            return processTemp;
        }

        public static bool IsUnderRoot(string fullPath, string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(rootDirectory))
                return false;

            try
            {
                var full = Path.GetFullPath(fullPath);
                var root = Path.GetFullPath(rootDirectory);
                if (!root.EndsWith(Path.DirectorySeparatorChar.ToString())
                    && !root.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
                {
                    root += Path.DirectorySeparatorChar;
                }

                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static bool IsUnderTempOrCaptures(string fullPath)
        {
            return IsUnderTempOrCaptures(fullPath, TempDirectory, CapturesDirectory) || IsUnderRoot(fullPath, UserTempDirectory);
        }
        internal static bool IsUnderTempOrCaptures(string fullPath, string tempDirectory, string capturesDirectory)
            => IsUnderRoot(fullPath, tempDirectory) || IsUnderRoot(fullPath, capturesDirectory);
    }
}
