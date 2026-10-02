using System;
using System.Globalization;
using System.Text.RegularExpressions;
using RvtMcp.Plugin;

namespace RvtMcp.Plugin.Views.Toast
{
    /// <summary>
    /// One compact, immutable outcome for the current card. Never retains a request,
    /// source code, response JSON or host object; no disk persistence is involved.
    /// </summary>
    public sealed class ToastActivityEntry
    {
        public const int TitleLimit = 80;
        public const int BodyLimit = 240;
        private static readonly Regex Paths = new Regex(
            @"(?:[A-Za-z]:[\\/]|\\\\)[^\r\n\""<>|]+", RegexOptions.Compiled);
        private static readonly Regex Credentials = new Regex(
            @"\b(?:auth[_-]?token|access[_-]?token|api[_-]?key|password|secret)\b[\""']?\s*[:=]\s*(?:\""[^\""]*\""|'[^']*'|[^\s,;]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);

        public ToastActivityEntry(string title, string body, bool success, long? durationMs = null,
            DateTimeOffset? completedAt = null)
        {
            Title = Compact(title, TitleLimit);
            Body = Compact(body, BodyLimit);
            Success = success;
            DurationMs = durationMs.HasValue ? (long?)Math.Max(0, durationMs.Value) : null;
            CompletedAt = completedAt ?? DateTimeOffset.Now;
        }

        public string Title { get; }
        public string Body { get; }
        public bool Success { get; }
        /// <summary>Null when the completion did not supply a measured duration.</summary>
        public long? DurationMs { get; }
        /// <summary>Captured when this outcome is recorded, never when the user hovers.</summary>
        public DateTimeOffset CompletedAt { get; }
        public string LocalTimeText => CompletedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        internal static string Redact(string value)
        {
            var text = Credentials.Replace(value ?? string.Empty, "<redacted>");
            return Paths.Replace(SecretMasker.Mask(text), "<path>");
        }

        internal static string Compact(string value, int limit)
        {
            // Redact before truncation: cutting a credential first could evade its mask.
            var text = Redact(value);
            text = Whitespace.Replace(text, " ").Trim();
            return text.Length <= limit ? text : text.Substring(0, limit - 1) + "…";
        }
    }
}
