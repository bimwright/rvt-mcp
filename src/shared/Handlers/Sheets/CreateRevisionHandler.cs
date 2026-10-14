using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateRevisionHandler : IRevitCommand
    {
        public string Name => "create_revision";
        public string Description => "Create a new document revision";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""description""],
  ""properties"": {
    ""description"": { ""type"": ""string"" },
    ""date"": { ""type"": ""string"" },
    ""issued_to"": { ""type"": ""string"" },
    ""issued_by"": { ""type"": ""string"" },
    ""issued"": { ""type"": ""boolean"", ""default"": false }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            JObject request;
            try
            {
                request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Parameters must be a JSON object: {ex.Message}");
            }

            var description = request.Value<string>("description");
            var date = request.Value<string>("date") ?? "";
            var issuedTo = request.Value<string>("issued_to") ?? request.Value<string>("issuedTo") ?? "";
            var issuedBy = request.Value<string>("issued_by") ?? request.Value<string>("issuedBy") ?? "";
            var issued = request.Value<bool?>("issued") ?? false;

            if (string.IsNullOrWhiteSpace(description))
                return CommandResult.Fail("description is required and cannot be empty.");

            using (var tx = new Transaction(doc, "RvtMcp: create revision"))
            {
                tx.Start();
                try
                {
                    var rev = Revision.Create(doc);
                    rev.Description = description;

                    if (!string.IsNullOrEmpty(date))
                        rev.RevisionDate = date;

                    if (!string.IsNullOrEmpty(issuedTo))
                        rev.IssuedTo = issuedTo;

                    if (!string.IsNullOrEmpty(issuedBy))
                        rev.IssuedBy = issuedBy;

                    // Lock revision by setting Issued=true last if requested
                    if (issued)
                    {
                        rev.Issued = true;
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");

                    string revNumber = "";
                    try
                    {
                        revNumber = rev.RevisionNumber;
                    }
                    catch { }

                    return CommandResult.Ok(new
                    {
                        created = true,
                        revision_id = RevitCompat.GetId(rev.Id),
                        sequence_number = rev.SequenceNumber,
                        revision_number = revNumber,
                        date = rev.RevisionDate,
                        description = rev.Description,
                        issued = rev.Issued,
                        error = (string)null
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Ok(new
                    {
                        created = false,
                        error = ex.Message
                    });
                }
            }
        }
    }
}
