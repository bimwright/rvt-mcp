using System.Threading;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    /// <summary>
    /// Legacy 0.6.x line only: tells the connected AI agent that this build is
    /// outdated and points the user at the latest GitHub release. Deliberately
    /// does not name a newer version number so the text never goes stale.
    /// </summary>
    internal static class LegacyUpdateNotice
    {
        public const string ReleasesLatestUrl = "https://github.com/bimwright/rvt-mcp/releases/latest";

        // Prepended to the MCP initialize instructions; keep it short (2 KB cap overall).
        public const string InstructionsPrefix =
            "NOTICE: this is a legacy rvt-mcp build and a newer version exists. Tell the user to check and install the latest from " +
            ReleasesLatestUrl + "\n\n";

        public const string Text =
            "Legacy rvt-mcp build; a newer version exists. Ask the user to install the latest from " + ReleasesLatestUrl;

        public const string FieldName = "update_notice";

        private static int _attached;

        /// <summary>
        /// Adds <c>update_notice</c> to the first successful Revit response of this
        /// server process (one MCP session). Never overwrites an existing field.
        /// </summary>
        public static void AttachOnce(JObject data)
        {
            if (data == null || data[FieldName] != null) return;
            if (Interlocked.Exchange(ref _attached, 1) != 0) return;
            data[FieldName] = Text;
        }

        internal static void ResetForTests() => Interlocked.Exchange(ref _attached, 0);
    }
}
