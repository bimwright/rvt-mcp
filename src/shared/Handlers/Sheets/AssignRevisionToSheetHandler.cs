using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class AssignRevisionToSheetHandler : IRevitCommand
    {
        public string Name => "assign_revision_to_sheet";
        public string Description => "Assign or remove a revision on sheets";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""revision_id""],
  ""properties"": {
    ""revision_id"": { ""type"": ""integer"" },
    ""sheet_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } },
    ""sheet_numbers"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""mode"": { ""type"": ""string"", ""enum"": [""append"", ""replace"", ""remove""], ""default"": ""append"" }
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

            var revisionId = request.Value<long?>("revision_id") ?? request.Value<long?>("revisionId");
            var sheetIdsToken = request["sheet_ids"] ?? request["sheetIds"];
            var sheetNumbersToken = request["sheet_numbers"] ?? request["sheetNumbers"];
            var mode = request.Value<string>("mode") ?? "append";

            if (!revisionId.HasValue)
                return CommandResult.Fail("revision_id is required.");
            if (!RevitCompat.CanRepresentElementId(revisionId.Value))
                return CommandResult.Fail(RevitCompat.ElementIdRangeError(revisionId.Value));

            if (!mode.Equals("append", StringComparison.OrdinalIgnoreCase) &&
                !mode.Equals("replace", StringComparison.OrdinalIgnoreCase) &&
                !mode.Equals("remove", StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.Fail("mode must be one of: append, replace, remove.");
            }

            // Resolve revision
            var revision = doc.GetElement(RevitCompat.ToElementId(revisionId.Value)) as Revision;
            if (revision == null)
                return CommandResult.Fail($"Revision with ID {revisionId.Value} not found.");

            // Resolve sheets
            var sheetElements = new Dictionary<long, ViewSheet>();

            if (sheetIdsToken != null && sheetIdsToken.Type == JTokenType.Array)
            {
                var sheetIds = sheetIdsToken.ToObject<long[]>();
                foreach (var id in sheetIds)
                {
                    if (!RevitCompat.CanRepresentElementId(id))
                        return CommandResult.Fail(RevitCompat.ElementIdRangeError(id));

                    var sheet = doc.GetElement(RevitCompat.ToElementId(id)) as ViewSheet;
                    if (sheet == null)
                        return CommandResult.Fail($"Sheet with ID {id} not found.");
                    sheetElements[RevitCompat.GetId(sheet.Id)] = sheet;
                }
            }

            if (sheetNumbersToken != null && sheetNumbersToken.Type == JTokenType.Array)
            {
                var sheetNumbers = sheetNumbersToken.ToObject<string[]>();
                var allSheetsInDoc = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .ToList();

                foreach (var num in sheetNumbers)
                {
                    if (string.IsNullOrEmpty(num)) continue;
                    var match = allSheetsInDoc.Where(s => s.SheetNumber.Equals(num, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (match.Count == 0)
                        return CommandResult.Fail($"Sheet with number '{num}' not found.");
                    if (match.Count > 1)
                        return CommandResult.Fail($"Ambiguous sheet number '{num}' matched multiple sheets.");

                    var sheet = match[0];
                    sheetElements[RevitCompat.GetId(sheet.Id)] = sheet;
                }
            }

            if (sheetElements.Count == 0)
                return CommandResult.Fail("No sheets were specified or resolved via sheet_ids or sheet_numbers.");

            var targetRevisionId = revision.Id;
            var sheetResultDtos = new List<object>();

            using (var tx = new Transaction(doc, "RvtMcp: assign revision to sheet"))
            {
                tx.Start();
                try
                {
                    foreach (var kvp in sheetElements)
                    {
                        var sheet = kvp.Value;
                        var currentAdditionalIds = sheet.GetAdditionalRevisionIds();
                        var newAdditionalIds = new HashSet<ElementId>();

                        foreach (var id in currentAdditionalIds)
                        {
                            newAdditionalIds.Add(id);
                        }

                        if (mode.Equals("append", StringComparison.OrdinalIgnoreCase))
                        {
                            newAdditionalIds.Add(targetRevisionId);
                        }
                        else if (mode.Equals("replace", StringComparison.OrdinalIgnoreCase))
                        {
                            newAdditionalIds.Clear();
                            newAdditionalIds.Add(targetRevisionId);
                        }
                        else if (mode.Equals("remove", StringComparison.OrdinalIgnoreCase))
                        {
                            newAdditionalIds.Remove(targetRevisionId);
                        }

                        sheet.SetAdditionalRevisionIds(newAdditionalIds.ToList());

                        // Check if revision still remains (could be cloud-derived)
                        bool remains = false;
                        try
                        {
                            remains = sheet.GetAllRevisionIds().Contains(targetRevisionId);
                        }
                        catch
                        {
                            try
                            {
                                remains = sheet.GetAdditionalRevisionIds().Contains(targetRevisionId);
                            }
                            catch { }
                        }

                        string statusStr = "assigned";
                        if (mode.Equals("remove", StringComparison.OrdinalIgnoreCase))
                        {
                            if (remains)
                            {
                                statusStr = "remains_cloud_derived";
                            }
                            else
                            {
                                statusStr = "removed";
                            }
                        }
                        else if (mode.Equals("replace", StringComparison.OrdinalIgnoreCase))
                        {
                            statusStr = "replaced";
                        }

                        sheetResultDtos.Add(new
                        {
                            sheet_id = RevitCompat.GetId(sheet.Id),
                            sheet_number = sheet.SheetNumber,
                            status = statusStr
                        });
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Ok(new
                    {
                        updated = false,
                        error = ex.Message
                    });
                }
            }

            return CommandResult.Ok(new
            {
                updated = true,
                revision_id = RevitCompat.GetId(targetRevisionId),
                mode = mode.ToLowerInvariant(),
                sheets = sheetResultDtos,
                error = (string)null
            });
        }
    }
}
