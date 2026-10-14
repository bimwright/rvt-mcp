#nullable enable
using System.Text;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// Size policy for MCP wire responses. Warn from 64 KiB, warn strongly above
    /// 256 KiB, and reject once the compact payload passes the enforcement budget.
    /// The budget sits below the 1 MiB delivered ceiling because the server
    /// pretty-prints (Formatting.Indented) before the payload reaches the agent,
    /// inflating it beyond the compact bytes measured here; the ~700 KiB headroom
    /// keeps the delivered response under MaxResponseBytes.
    /// </summary>
    public static class ResponseSizeGuard
    {
        public const int DefaultThresholdBytes = 64 * 1024;
        public const int StrongWarningThresholdBytes = 256 * 1024;

        /// <summary>Delivered-size ceiling the agent's context must stay under.</summary>
        public const int MaxResponseBytes = 1024 * 1024;

        /// <summary>
        /// Plugin-side reject threshold on the compact payload. Reduced below
        /// MaxResponseBytes to absorb server-side pretty-print expansion.
        /// </summary>
        public const int EnforcementBudgetBytes = 700 * 1024;

        public sealed class Decision
        {
            public int ByteCount { get; set; }
            public string? Warning { get; set; }
            public string? WarningLevel { get; set; }
            public string? AgentWarning { get; set; }
            public bool Reject { get; set; }
            public string? RejectError { get; set; }
        }

        public static string? CheckResponse(
            string commandName,
            string serializedPayload,
            int topLevelKeyCount,
            int thresholdBytes = DefaultThresholdBytes)
        {
            return Evaluate(commandName, serializedPayload, topLevelKeyCount, thresholdBytes, MaxResponseBytes).Warning;
        }

        public static Decision Evaluate(
            string commandName,
            string serializedPayload,
            int topLevelKeyCount,
            int warnBytes = DefaultThresholdBytes,
            int maxBytes = EnforcementBudgetBytes,
            int strongWarnBytes = StrongWarningThresholdBytes,
            string? narrowingHint = null)
        {
            var byteCount = Encoding.UTF8.GetByteCount(serializedPayload ?? string.Empty);
            var decision = new Decision { ByteCount = byteCount };
            var hint = string.IsNullOrWhiteSpace(narrowingHint)
                ? ResponseSizePolicyCatalog.GetNarrowingHint(commandName)
                : narrowingHint;

            if (byteCount > maxBytes)
            {
                decision.Reject = true;
                decision.RejectError =
                    $"RESPONSE_TOO_LARGE: Response exceeded the {maxBytes}-byte response budget ({byteCount} bytes) for command={commandName} " +
                    $"top_level_keys={topLevelKeyCount}. {hint} " +
                    "Do not retry the same unscoped request.";
                return decision;
            }

            if (byteCount >= warnBytes)
            {
                var strong = byteCount > strongWarnBytes;
                decision.WarningLevel = strong ? "strong_warning" : "warning";
                decision.Warning =
                    $"[S4] Oversized response: command={commandName} bytes={byteCount} " +
                    $"top_level_keys={topLevelKeyCount} threshold={warnBytes} level={decision.WarningLevel}";
                decision.AgentWarning = strong
                    ? $"Oversized response strong warning: {byteCount} bytes. {hint}"
                    : $"Oversized response warning: {byteCount} bytes. Consider narrowing the next request. {hint}";
            }

            return decision;
        }
    }
}
