using System;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Reports.Revit;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Opens the Reports window on one saved agent report, the same way a click on its toast does.
    /// It reads nothing from the model and changes nothing; the user reads the report and follows its links.
    /// </summary>
    public sealed class ShowReportHandler : IRevitCommand
    {
        public string Name => "show_report";
        public string Description => "Open the Reports window on one saved agent report; changes nothing.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""report_id"":{""type"":""string""}},""required"":[""report_id""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            string reportId;
            try { reportId = JObject.Parse(string.IsNullOrWhiteSpace(paramsJson) ? "{}" : paramsJson).Value<string>("report_id"); }
            catch (Exception ex) { return CommandResult.Fail("Invalid JSON parameters: " + ex.Message); }
            if (reportId?.Length != 32 || !Guid.TryParseExact(reportId, "N", out _))
                return CommandResult.Fail("INVALID_ARGUMENT: report_id must be the 32-character hexadecimal report_id returned by revit_report_task_result.");
            reportId = reportId.ToLowerInvariant(); // saved IDs are lowercase and the window matches them exactly

            bool exists;
            try { exists = ReportServices.Store.Exists(reportId); }
            catch (Exception ex) { return CommandResult.Fail("REPORT_STORAGE_FAILED: the local reports folder could not be read: " + ex.Message); }
            if (!exists) return CommandResult.Fail("REPORT_NOT_FOUND: no saved report has this report_id. It may have been deleted by the user or by retention.");
            if (!ReportsModule.ShowReport(reportId)) return CommandResult.Fail("UI_UNAVAILABLE: the Reports window is not available in this Revit session.");

            return CommandResult.Ok(new
            {
                shown = true,
                report_id = reportId,
                note = "The Reports window is open on this report. Nothing was changed."
            });
        }
    }
}
