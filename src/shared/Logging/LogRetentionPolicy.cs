using System;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// <c>BIMWRIGHT_KEEP_ALL_LOGS</c>: when on, automatic deletion of logs, call records, journals,
    /// usage/audit logs and agent reports is skipped. Rotation still archives; nothing is overwritten.
    /// It never turns a recorder on or changes what is recorded. Each process reads its own environment.
    /// </summary>
    public static class LogRetentionPolicy
    {
        public const string EnvKeepAllLogs = "BIMWRIGHT_KEEP_ALL_LOGS";

        /// <summary>Test seam: a fixed value instead of the process environment.</summary>
        internal static bool? KeepAllOverride { get; set; }

        public static bool KeepAll => KeepAllOverride ?? IsTruthy(Environment.GetEnvironmentVariable(EnvKeepAllLogs));

        /// <summary><c>1</c>, <c>true</c>, <c>yes</c> or <c>on</c> (trimmed, any case); anything else is off.</summary>
        public static bool IsTruthy(string value)
        {
            var text = value?.Trim();
            return string.Equals(text, "1", StringComparison.Ordinal)
                || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "on", StringComparison.OrdinalIgnoreCase);
        }
    }
}
