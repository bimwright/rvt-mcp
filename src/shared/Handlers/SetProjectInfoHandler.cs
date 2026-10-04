using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class SetProjectInfoHandler : IRevitCommand
    {
        public string Name => "set_project_info";
        public string Description => "Set typed fields on doc.ProjectInformation: name, number, client_name, address, status, issue_date. Skips read-only/missing parameters with structured warnings. At least one field required.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""name"":{""type"":""string""},""number"":{""type"":""string""},""client_name"":{""type"":""string""},""address"":{""type"":""string""},""status"":{""type"":""string""},""issue_date"":{""type"":""string""}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var fields = new Dictionary<string, FieldValue>
            {
                ["name"] = new FieldValue(BuiltInParameter.PROJECT_NAME, req.Value<string>("name")),
                ["number"] = new FieldValue(BuiltInParameter.PROJECT_NUMBER, req.Value<string>("number")),
                ["client_name"] = new FieldValue(BuiltInParameter.CLIENT_NAME, req.Value<string>("client_name")),
                ["address"] = new FieldValue(BuiltInParameter.PROJECT_ADDRESS, req.Value<string>("address")),
                ["status"] = new FieldValue(BuiltInParameter.PROJECT_STATUS, req.Value<string>("status")),
                ["issue_date"] = new FieldValue(BuiltInParameter.PROJECT_ISSUE_DATE, req.Value<string>("issue_date"))
            };

            var supplied = 0;
            foreach (var field in fields.Values)
            {
                if (field.Value != null) supplied++;
            }

            if (supplied == 0)
                return CommandResult.Fail("At least one field must be supplied.");

            var projectInfo = doc.ProjectInformation;
            if (projectInfo == null) return CommandResult.Fail("doc.ProjectInformation is null.");

            var changed = new List<string>();
            var skipped = new List<object>();

            using (var tx = new Transaction(doc, "RvtMcp: Set project info"))
            {
                tx.Start();
                try
                {
                    foreach (var entry in fields)
                    {
                        var key = entry.Key;
                        var field = entry.Value;
                        if (field.Value == null) continue;

                        var parameter = projectInfo.get_Parameter(field.BuiltInParameter);
                        if (parameter == null)
                        {
                            skipped.Add(new { field = key, reason = "parameter_not_found" });
                            continue;
                        }
                        if (parameter.IsReadOnly)
                        {
                            skipped.Add(new { field = key, reason = "read_only" });
                            continue;
                        }

                        try
                        {
                            parameter.Set(field.Value);
                            changed.Add(key);
                        }
                        catch (Exception ex)
                        {
                            skipped.Add(new { field = key, reason = "set_failed", detail = ex.Message });
                        }
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Transaction failed: {ex.Message}");
                }
            }

            return CommandResult.Ok(new
            {
                project_info_id = RevitCompat.GetId(projectInfo.Id),
                supplied,
                changed_fields = changed,
                skipped
            });
        }

        private sealed class FieldValue
        {
            public FieldValue(BuiltInParameter builtInParameter, string value)
            {
                BuiltInParameter = builtInParameter;
                Value = value;
            }

            public BuiltInParameter BuiltInParameter { get; }
            public string Value { get; }
        }
    }
}
