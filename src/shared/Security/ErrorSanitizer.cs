using System.Text.RegularExpressions;

namespace Bimwright.Rvt.Plugin
{
    /// <summary>
    /// S5 path-leak mask (aspect #5 §S5): strip absolute paths from error strings
    /// before they reach the MCP response, JSONL log, or session-log UI.
    /// L2 mask = keep filename + line number for debuggability; drop workspace / user-home / UNC prefixes.
    /// </summary>
    public static class ErrorSanitizer
    {
        // 1. Windows absolute paths: D:\..., C:\Users\... → keep last filename only
        private static readonly Regex WindowsAbsolute = new Regex(
            @"[A-Za-z]:\\(?:[^\\""'\s]+\\)*([^\\""'\s]+)",
            RegexOptions.Compiled);

        // 2. UNC paths: \\server\share\... → keep last filename only
        private static readonly Regex Unc = new Regex(
            @"\\\\[^\\""'\s]+\\(?:[^\\""'\s]+\\)*([^\\""'\s]+)",
            RegexOptions.Compiled);

        // 3. Unix paths (safety): /home/..., /Users/... → keep last filename only
        private static readonly Regex UnixHome = new Regex(
            @"/(?:home|Users)/[^/\s""']+/(?:[^/\s""']+/)*([^/\s""']+)",
            RegexOptions.Compiled);

        public static string Sanitize(string error)
        {
            if (string.IsNullOrEmpty(error)) return error;

            // UNC must run before WindowsAbsolute so the leading `\\server\` isn't mis-matched.
            error = Unc.Replace(error, "$1");
            error = WindowsAbsolute.Replace(error, "$1");
            error = UnixHome.Replace(error, "$1");
            return error;
        }
    }
}
