using System;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RvtMcp.Plugin;
using RvtMcp.Plugin.Reports.Model;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("meta")]
    public class ReportTools
    {
        [McpServerTool(Name = "revit_get_report_history", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Read exact SQLite History for a saved Agent Report: capture call IDs, record IDs, reasons and before/after. Disabled, pending, missing and legacy evidence are explicit. Read-only local data; no Revit connection required. Paginate calls and elements separately; element_offset applies to each call on the page. report_id is a 32-character hexadecimal report ID; offsets must be nonnegative, call_limit is 1-10 (default 5), element_limit is 1-25 (default 10). Example: report_id=\"0123456789abcdef0123456789abcdef\", call_offset=0, element_offset=0, call_limit=5, element_limit=10.")]
        public static string GetReportHistory(string report_id, int call_offset = 0, int element_offset = 0, int call_limit = 5, int element_limit = 10)
        {
            try
            {
                return new RvtMcp.Plugin.Reports.History.ReportHistoryLookup().Read(report_id, call_offset, element_offset, call_limit, element_limit).ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex) { return $"Error: {ErrorSanitizer.Sanitize(ex.Message)}"; }
        }

        [McpServerTool(Name = "revit_report_task_result", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description(
            "Report the finished task to the Revit user in the Reports window. Call it once when a job that worked in Revit is verified, has failed or was cancelled: " +
            "it changed elements, or it found elements the user should check. Not for a quick answer in chat or a job that stopped before changing anything; never after each tool. " +
            "task_id is unique per job (1-80; resending in the same client session and Revit process replaces that report); outcome completed|failed|cancelled; summary one line (1-120); " +
            "body optional Markdown subset (headings, lists, **bold**, *italic*, `code`, fenced code blocks, pipe tables, --- rules; underscores are literal), max 32 KiB UTF-8. " +
            "Link elements as [label](revit:<id>[,<id>...][?view=<viewId>][&doc=<document_id>]); use ?doc= when there is no view parameter. " +
            "Without doc a link targets the active document; use doc=<_target.document_id> for another open document. " +
            "The response lists unresolved links: fix them and resend. Optional call_ids is a JSON array of _report_capture.call_id values for this task (max 256); use it on the first report to isolate interleaved jobs. " +
            "Omit it for a client interval, not task attribution. Resends keep their original scope. Agent-reported; recorded changes do not verify completion. Does not change the model.")]
        public static async Task<string> ReportTaskResult(string task_id, string outcome, string summary, string body = null, string call_ids = null)
            => await ToolGateway.CallWithParamsAsync("report_task_result", () =>
            {
                var error = ReportLimits.Validate(task_id, outcome, summary, body);
                if (error != null) throw new ArgumentException(error);
                ReportCallContext.ParseCallIds(call_ids);
                return new { task_id, outcome, summary, body, call_ids };
            });
    }
}
