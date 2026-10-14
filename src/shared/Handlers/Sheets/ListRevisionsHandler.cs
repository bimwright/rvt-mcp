using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ListRevisionsHandler : IRevitCommand
    {
        public string Name => "list_revisions";
        public string Description => "List project revisions and optionally their assigned sheets";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""include_sheets"": { ""type"": ""boolean"", ""default"": true }
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

            var includeSheets = request.Value<bool?>("include_sheets") ?? request.Value<bool?>("includeSheets") ?? true;

            var revisions = new FilteredElementCollector(doc)
                .OfClass(typeof(Revision))
                .Cast<Revision>()
                .ToList();

            var sheetMap = new Dictionary<long, List<ViewSheet>>();
            if (includeSheets)
            {
                var sheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .ToList();

                foreach (var sheet in sheets)
                {
                    if (sheet.IsPlaceholder) continue;

                    ICollection<ElementId> revIdsOnSheet = null;
                    try
                    {
                        revIdsOnSheet = sheet.GetAllRevisionIds();
                    }
                    catch
                    {
                        try
                        {
                            revIdsOnSheet = sheet.GetAdditionalRevisionIds();
                        }
                        catch
                        {
                            continue;
                        }
                    }

                    if (revIdsOnSheet != null)
                    {
                        foreach (var rId in revIdsOnSheet)
                        {
                            long idValue = RevitCompat.GetId(rId);
                            if (!sheetMap.ContainsKey(idValue))
                            {
                                sheetMap[idValue] = new List<ViewSheet>();
                            }
                            sheetMap[idValue].Add(sheet);
                        }
                    }
                }
            }

            var revisionDtos = new List<object>();
            int skipped = 0;

            foreach (var rev in revisions)
            {
                try
                {
                    long revId = RevitCompat.GetId(rev.Id);
                    string revNum = "";
                    try
                    {
                        revNum = rev.RevisionNumber;
                    }
                    catch { }

                    var sheetIdsList = new List<long>();
                    var sheetsList = new List<object>();

                    if (includeSheets && sheetMap.TryGetValue(revId, out var matchedSheets))
                    {
                        foreach (var s in matchedSheets)
                        {
                            sheetIdsList.Add(RevitCompat.GetId(s.Id));
                            sheetsList.Add(new
                            {
                                id = RevitCompat.GetId(s.Id),
                                sheet_number = s.SheetNumber,
                                sheet_name = s.Name
                            });
                        }
                    }

                    revisionDtos.Add(new
                    {
                        id = revId,
                        sequence_number = rev.SequenceNumber,
                        revision_number = revNum,
                        date = rev.RevisionDate ?? "",
                        description = rev.Description ?? "",
                        issued = rev.Issued,
                        issued_to = rev.IssuedTo ?? "",
                        issued_by = rev.IssuedBy ?? "",
                        sheet_ids = sheetIdsList,
                        sheets = sheetsList
                    });
                }
                catch
                {
                    skipped++;
                }
            }

            return CommandResult.Ok(new
            {
                total = revisionDtos.Count,
                skipped = skipped,
                revisions = revisionDtos
            });
        }
    }
}
