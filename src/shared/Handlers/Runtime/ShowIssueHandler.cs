using System;
using System.Linq;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RvtMcp.Failures;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Opens History → Issues on one recorded incident and marks the suggested action, so the user does not have to search for it.
    /// It never records a decision: the user still reviews the evidence, ticks the review box and clicks the action.
    /// </summary>
    public class ShowIssueHandler : IRevitCommand
    {
        internal static readonly string[] Actions = { "rework", "abandon", "accept_once", "create_rule", "native_grant" };

        public string Name => "show_issue";
        public string Description => "Open History > Issues on one incident for the user to decide; records nothing.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""incident_id"":{""type"":""string""},""action"":{""type"":""string"",""enum"":[""rework"",""abandon"",""accept_once"",""create_rule"",""native_grant""]}},""required"":[""incident_id""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            string incidentId, action;
            try
            {
                var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
                incidentId = request.Value<string>("incident_id");
                action = request.Value<string>("action");
            }
            catch (Exception ex) { return CommandResult.Fail("Invalid JSON parameters: " + ex.Message); }
            if (!FailureCapture.IsKey(incidentId)) return CommandResult.Fail("INVALID_ARGUMENT: incident_id must be an issued incident ID.");
            if (action != null && !Actions.Contains(action))
                return CommandResult.Fail("INVALID_ARGUMENT: action must be one of " + string.Join(", ", Actions) + ".");

            FailureIncident incident;
            try { incident = new FailureLedgerStore(readOnly: true).Query(new FailureRecordQuery { IncidentId = incidentId, Limit = 1 }).Records.FirstOrDefault(); }
            catch (Exception ex) { return CommandResult.Fail("FAILURE_STORAGE_FAILED: the local failure ledger could not be read: " + ex.Message); }
            if (incident == null) return CommandResult.Fail("INCIDENT_NOT_FOUND: no recorded incident has this ID. Read it again with revit_get_failure_records.");
            if (App.Instance == null) return CommandResult.Fail("UI_UNAVAILABLE: the add-in window host is not running.");

            App.Instance.FocusIssue(incident.IncidentId, action);
            return CommandResult.Ok(new
            {
                shown = true,
                incidentId = incident.IncidentId,
                state = incident.State,
                modelTitle = incident.ModelTitle,
                modelScopeKey = incident.ModelScopeKey,
                revitYear = incident.RevitYear,
                suggestedAction = action,
                decided = false,
                note = "History > Issues is open on this incident with all states shown" + (action == null ? "." : " and the suggested action highlighted.")
                    + " Nothing was recorded. Ask the user to review it, tick the review box and click the action themselves; then read the outcome with revit_get_failure_records."
            });
        }
    }
}
