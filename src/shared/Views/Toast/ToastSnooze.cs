using System;
using System.Globalization;

namespace RvtMcp.Plugin.Views.Toast
{
    /// <summary>
    /// "Pause for today": activity toasts stay silent in every Revit on this machine until the next local midnight.
    /// The pause is the local date saved in rvtmcp.config.json (toastSnoozedDate), read on each check, so a pause
    /// set in one Revit applies to the others without a timer.
    /// </summary>
    internal static class ToastSnooze
    {
        private const string DateFormat = "yyyy-MM-dd";

        /// <summary>Raised after this process paused or resumed toasts (Settings refreshes its switch).</summary>
        public static event Action Changed;

        /// <summary>Test hook for the local clock.</summary>
        internal static Func<DateTime> Now { get; set; } = () => DateTime.Now;

        public static bool IsActive() => IsActive(RvtMcpConfig.LoadReadOnly());

        public static bool IsActive(RvtMcpConfig config) => IsActive(config?.ToastSnoozedDate, Now());

        public static bool IsActive(string snoozedDate, DateTime now) =>
            DateTime.TryParseExact(snoozedDate, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            && date.Date == now.Date;

        /// <summary>Pauses toasts for the rest of today. Returns whether the choice was saved.</summary>
        public static bool PauseForToday(out string error) =>
            Save(Now().ToString(DateFormat, CultureInfo.InvariantCulture), out error);

        /// <summary>Ends a pause early. Returns whether the choice was saved.</summary>
        public static bool Resume(out string error) => Save(null, out error);

        /// <summary>
        /// Turning toasts on means showing them now, so an active pause ends too. Returns false only when a pause
        /// was active and its removal could not be saved.
        /// </summary>
        public static bool ResumeIfPaused(out string error)
        {
            error = null;
            return !IsActive() || Resume(out error);
        }

        private static bool Save(string date, out string error)
        {
            var saved = RvtMcpConfig.TrySaveToastSnoozedDate(date, out error);
            try { Changed?.Invoke(); } catch { }
            return saved;
        }
    }
}
