using System;
using RvtMcp.Plugin.Update;

namespace RvtMcp.Server
{
    /// <summary>
    /// MCP-side view of the add-in's cached update check
    /// (%LOCALAPPDATA%\RvtMcp\update-check.json). Never touches the network and
    /// stays silent when the user skipped that version in Revit.
    /// </summary>
    internal static class UpdateStatus
    {
        private static readonly object Gate = new object();
        private static DateTime _readAtUtc;
        private static UpdateInfo _cached;

        internal static string StatePath { get; set; }

        public static UpdateInfo Current()
        {
            lock (Gate)
            {
                var now = DateTime.UtcNow;
                if (now - _readAtUtc > TimeSpan.FromMinutes(1))
                {
                    try
                    {
                        var info = UpdateChecker.IsDisabledByEnvironment()
                            ? null
                            : UpdateChecker.FromCache(Program.ServerVersion, StatePath);
                        _cached = info != null && !info.IsSkipped ? info : null;
                    }
                    catch { _cached = null; }
                    _readAtUtc = now;
                }
                return _cached;
            }
        }

        /// <summary>Value for the <c>update_available</c> field on status-style results; null when up to date.</summary>
        public static object ForStatusResult()
        {
            var info = Current();
            if (info == null) return null;
            return new
            {
                current_version = info.CurrentVersion,
                latest_version = info.LatestVersion,
                release_url = info.ReleaseUrl,
                message = UpdateNoticeText.ForAgent(info)
            };
        }

        internal static void ResetForTests()
        {
            lock (Gate) { _readAtUtc = DateTime.MinValue; _cached = null; }
        }
    }
}
