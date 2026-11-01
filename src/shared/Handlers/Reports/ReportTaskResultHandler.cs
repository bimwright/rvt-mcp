using System;
using System.IO;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Reports.Binding;
using RvtMcp.Plugin.Reports.Changes;
using RvtMcp.Plugin.Reports.Model;
using RvtMcp.Plugin.Reports.Revit;

namespace RvtMcp.Plugin.Handlers
{
    public sealed class ReportTaskResultHandler : IRevitCommand
    {
        public string Name => "report_task_result";
        public string Description => "Save an agent task report without changing the document.";
        public string ParametersSchema => @"{""type"":""object"",""required"" : [""task_id"",""outcome"",""summary""],""properties"":{""task_id"":{""type"":""string""},""outcome"":{""type"":""string"",""enum"":[""completed"",""failed"",""cancelled""]},""summary"":{""type"":""string""},""body"":{""type"":""string""},""call_ids"":{""type"":""string""}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            string taskId;
            string outcome;
            string summary;
            string body;
            System.Collections.Generic.IReadOnlyList<string> callIds;
            try
            {
                var request = JObject.Parse(paramsJson ?? "{}");
                taskId = request.Value<string>("task_id");
                outcome = request.Value<string>("outcome");
                summary = request.Value<string>("summary");
                body = request.Value<string>("body");
                callIds = ReportCallContext.ParseCallIds(request.Value<string>("call_ids"));
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Invalid JSON parameters: " + ex.Message);
            }

            var error = ReportLimits.Validate(taskId, outcome, summary, body);
            if (error != null) return CommandResult.Fail(error);

            var target = DocumentTargetGuard.Capture(app);
            var source = new ReportSource
            {
                ClientSession = ReportExecutionContext.Current?.Session,
                TargetId = target.Value<string>("target_id"),
                Pid = target.Value<int>("pid"),
                HostYear = target.Value<int>("host_year"),
                ProcessStartUtc = target.Value<string>("process_start_utc")
            };
            ReportBuildResult result;
            try
            {
                result = ReportBuilder.BuildAndSave(taskId, outcome, summary, body, new RevitReportResolver(app),
                    source, ReportChangeBuffer.Shared, ReportServices.Store,
                    new ReportSubmissionScope { Session = source.ClientSession, CallIds = callIds });
            }
            catch (ReportCaptureException ex) { return CommandResult.Fail(ex.Message); }
            catch (IOException ex) { return CommandResult.Fail("REPORT_SAVE_FAILED: " + ex.Message + "; resend with the same task_id."); }
            catch (UnauthorizedAccessException ex) { return CommandResult.Fail("REPORT_SAVE_FAILED: " + ex.Message + "; resend with the same task_id."); }

            return CommandResult.Ok(ReportBuilder.CreateResponse(result, result.Saved, RvtMcpConfig.LoadReadOnly().EnableToastOrDefault));
        }
    }
}
