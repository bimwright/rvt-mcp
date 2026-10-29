using System;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RvtMcp.Plugin.Reports.Model;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("meta")]
    public class ReportTools
    {
        [McpServerTool(Name = "revit_report_task_result", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description(
            "Report the finished task to the Revit user in the Reports window. Call it once when a job that worked in Revit is verified, has failed or was cancelled: " +
            "it changed elements, or it found elements the user should check. Not for a quick answer in chat or a job that stopped before changing anything; never after each tool. " +
            "task_id is unique per job (1-80; resending in the same Revit process replaces that report); outcome completed|failed|cancelled; summary one line (1-120); " +
            "body optional Markdown subset (headings, lists, **bold**, *italic*, `code`, fenced code blocks, pipe tables, --- rules; underscores are literal), max 32 KiB UTF-8. " +
            "Link elements as [label](revit:<id>[,<id>...][?view=<viewId>][&doc=<document_id>]); use ?doc= when there is no view parameter. " +
            "Without doc a link targets the active document; use doc=<_target.document_id> for another open document. " +
            "The response lists unresolved links: fix them and resend. Agent-reported; the window also shows the changes rvt-mcp recorded. Does not change the model.")]
        public static async Task<string> ReportTaskResult(string task_id, string outcome, string summary, string body = null)
            => await ToolGateway.CallWithParamsAsync("report_task_result", () =>
            {
                var error = ReportLimits.Validate(task_id, outcome, summary, body);
                if (error != null) throw new ArgumentException(error);
                return new { task_id, outcome, summary, body };
            });
    }
}
