using System;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Views.Toast
{
    /// <summary>
    /// Remembers which activity card currently ends with an agent report, so a click on that card
    /// opens the report. Any later result on the card, or a new card, sends clicks back to History.
    /// </summary>
    public sealed class ToastReportRoute
    {
        public const string ToolName = "report_task_result";

        private readonly object _gate = new object();
        private long _cardId = -1;
        private string _reportId;

        /// <summary>The <c>report_id</c> of a successful report_task_result; null for anything else.</summary>
        public static string ReportIdFrom(string toolName, bool success, string resultJson)
        {
            if (!success || !string.Equals(toolName, ToolName, StringComparison.Ordinal) || string.IsNullOrEmpty(resultJson)) return null;
            try
            {
                var id = (JObject.Parse(resultJson)["report_id"] as JValue)?.Value as string;
                return id != null && Guid.TryParseExact(id, "N", out _) ? id : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Call after each result is recorded on <paramref name="cardId"/>; <paramref name="reportId"/> is null for other results.</summary>
        public void Recorded(long cardId, string reportId)
        {
            lock (_gate) { _cardId = cardId; _reportId = reportId; }
        }

        /// <summary>The report to open for a click on <paramref name="cardId"/>, or null to open History.</summary>
        public string ReportFor(long cardId)
        {
            lock (_gate) return cardId == _cardId ? _reportId : null;
        }
    }
}
