using System;
using System.Globalization;
using RvtMcp.Plugin.Localization;

namespace RvtMcp.Plugin.Reports.Presentation
{
    internal static class ReportCalendar
    {
        private static readonly string[] Formats = {
            "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"
        };

        public static DateTimeOffset? Parse(string value) => DateTimeOffset.TryParseExact(value, Formats,
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant)
            ? instant : (DateTimeOffset?)null;

        public static DateTimeOffset? Local(DateTimeOffset? instant, TimeZoneInfo zone) => instant.HasValue
            ? TimeZoneInfo.ConvertTime(instant.Value, zone) : (DateTimeOffset?)null;

        public static string Header(DateTime? date, int count, DateTime now)
        {
            var label = !date.HasValue ? L.T("reports.day.unknown")
                : date.Value == now.Date ? L.T("reports.day.today")
                : date.Value == now.Date.AddDays(-1) ? L.T("reports.day.yesterday")
                : date.Value.ToString("d", Culture);
            return L.T(count == 1 ? "reports.day.one" : "reports.day.many", ("day", label), ("count", count));
        }

        public static string Tooltip(DateTimeOffset? instant, TimeZoneInfo zone)
        {
            var local = Local(instant, zone);
            return !local.HasValue ? L.T("reports.day.unknown")
                : local.Value.ToString("F", Culture) + " UTC" + local.Value.ToString("zzz", CultureInfo.InvariantCulture) + " · " + zone.Id;
        }

        public static CultureInfo Culture
        {
            get
            {
                try { return CultureInfo.GetCultureInfo(L.Locale); }
                catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
            }
        }
    }
}
