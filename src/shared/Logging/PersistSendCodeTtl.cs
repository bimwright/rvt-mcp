using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RvtMcp.Plugin
{
    public static class PersistSendCodeTtl
    {
        public static readonly TimeSpan Min = TimeSpan.FromHours(1);
        public static readonly TimeSpan Default = TimeSpan.FromHours(4);
        public static readonly TimeSpan Max = TimeSpan.FromDays(14);

        private static readonly Regex Pattern = new Regex(
            @"^\s*(\d+)\s*([hmd])\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool TryParse(string input, out TimeSpan value)
        {
            value = default;
            if (string.IsNullOrWhiteSpace(input)) return false;
            var m = Pattern.Match(input);
            if (!m.Success) return false;
            if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0)
                return false;
            switch (char.ToLowerInvariant(m.Groups[2].Value[0]))
            {
                case 'h': value = TimeSpan.FromHours(n); break;
                case 'd': value = TimeSpan.FromDays(n); break;
                case 'm': value = TimeSpan.FromMinutes(n); break;
                default: return false;
            }
            return true;
        }

        public static TimeSpan Clamp(TimeSpan value)
        {
            if (value < Min) return Min;
            if (value > Max) return Max;
            return value;
        }

        public static string FormatIsoUntil(DateTimeOffset utc) =>
            utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }
}
