using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class DeleteSavedSelectionHandler : IRevitCommand
    {
        public string Name => "delete_saved_selection";
        public string Description => "Delete a saved named selection filter in the active document.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""name"": { ""type"": ""string"" },
    ""selectionId"": { ""type"": ""integer"" },
    ""dryRun"": { ""type"": ""boolean"", ""default"": true }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            var name = request.Value<string>("name");
            var selectionIdInput = request.Value<long?>("selectionId");
            var dryRun = request.Value<bool?>("dryRun") ?? true;

            if (string.IsNullOrEmpty(name) && !selectionIdInput.HasValue)
                return CommandResult.Fail("Either name or selectionId is required.");

            if (!string.IsNullOrEmpty(name) && selectionIdInput.HasValue)
                return CommandResult.Fail("Only one of name or selectionId can be specified, not both.");

            SelectionFilterElement targetFilter = null;

            if (selectionIdInput.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(selectionIdInput.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(selectionIdInput.Value));

                var elementId = RevitCompat.ToElementId(selectionIdInput.Value);
                targetFilter = doc.GetElement(elementId) as SelectionFilterElement;
                if (targetFilter == null)
                    return CommandResult.Fail($"SelectionFilterElement with ID {selectionIdInput.Value} not found.");
            }
            else
            {
                var matches = new FilteredElementCollector(doc)
                    .OfClass(typeof(SelectionFilterElement))
                    .Cast<SelectionFilterElement>()
                    .Where(f => string.Equals(f.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matches.Count == 0)
                    return CommandResult.Fail($"Selection filter named '{name}' not found.");
                if (matches.Count > 1)
                    return CommandResult.Fail($"Multiple selection filters found with the name '{name}'. Use selectionId instead.");

                targetFilter = matches[0];
            }

            var selectionId = RevitCompat.GetId(targetFilter.Id);
            var filterName = targetFilter.Name;
            var elementIds = targetFilter.GetElementIds();
            var deletedElementIds = new List<long>();

            if (!dryRun)
            {
                using (var tx = new Transaction(doc, "RvtMcp: delete saved selection"))
                {
                    tx.Start();
                    try
                    {
                        var deletedIds = doc.Delete(targetFilter.Id);
                        if (deletedIds != null)
                        {
                            foreach (var id in deletedIds)
                            {
                                deletedElementIds.Add(RevitCompat.GetId(id));
                            }
                        }

                        var status = tx.Commit();
                        if (status != TransactionStatus.Committed)
                        {
                            return CommandResult.Fail($"Transaction commit failed: {status}");
                        }
                    }
                    catch (Exception ex)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        return CommandResult.Fail($"Failed to delete saved selection: {ex.Message}");
                    }
                }
            }

            return CommandResult.Ok(new
            {
                wouldDelete = true,
                deleted = !dryRun,
                dryRun = dryRun,
                selectionId = selectionId,
                name = filterName,
                count = elementIds.Count,
                deletedElementIds = deletedElementIds.ToArray()
            });
        }
    }
}
