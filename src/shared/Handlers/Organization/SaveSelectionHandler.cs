using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class SaveSelectionHandler : IRevitCommand
    {
        public string Name => "save_selection";
        public string Description => "Save named selection filter in the active document.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""name""],
  ""properties"": {
    ""name"": { ""type"": ""string"" },
    ""elementIds"": {
      ""type"": ""array"",
      ""items"": { ""type"": ""integer"" },
      ""description"": ""Optional element IDs. If omitted, the current UI selection is used.""
    },
    ""replaceExisting"": { ""type"": ""boolean"", ""default"": false },
    ""useActiveSelectionIfIdsOmitted"": { ""type"": ""boolean"", ""default"": true },
    ""include_element_ids"": { ""type"": ""boolean"", ""default"": false },
    ""max_element_id_results"": { ""type"": ""integer"", ""default"": 100, ""minimum"": 1, ""maximum"": 1000 }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            var uidoc = app.ActiveUIDocument;
            if (doc == null || uidoc == null)
                return CommandResult.Fail("No active document or UI document is available.");

            var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            var name = request.Value<string>("name");
            var elementIdsInput = request["elementIds"]?.ToObject<long[]>();
            var replaceExisting = request.Value<bool?>("replaceExisting") ?? false;
            var useActiveSelectionIfIdsOmitted = request.Value<bool?>("useActiveSelectionIfIdsOmitted") ?? true;
            var includeElementIds = request.Value<bool?>("include_element_ids") ?? false;
            var maxElementIdResults = request.Value<int?>("max_element_id_results") ?? 100;
            if (maxElementIdResults < 1 || maxElementIdResults > 1000)
                return CommandResult.Fail("max_element_id_results must be between 1 and the hard maximum of 1000.");

            if (string.IsNullOrWhiteSpace(name))
                return CommandResult.Fail("name is required and cannot be empty.");

            var finalIds = new List<long>();
            string source = "explicitIds";

            if (elementIdsInput == null || elementIdsInput.Length == 0)
            {
                if (!useActiveSelectionIfIdsOmitted)
                {
                    return CommandResult.Fail("elementIds is empty and useActiveSelectionIfIdsOmitted is false.");
                }

                var activeSelection = uidoc.Selection.GetElementIds();
                if (activeSelection == null || activeSelection.Count == 0)
                {
                    return CommandResult.Fail("The active selection is empty and no elementIds were supplied.");
                }

                finalIds.AddRange(activeSelection.Select(id => RevitCompat.GetId(id)));
                source = "activeSelection";
            }
            else
            {
                foreach (var idVal in elementIdsInput)
                {
                    if (!RevitCompat.CanRepresentElementId(idVal))
                        return CommandResult.Fail(RevitCompat.ElementIdRangeError(idVal));

                    finalIds.Add(idVal);
                }
            }

            // Validate all element IDs actually exist in doc
            var missingIds = new List<long>();
            var validIds = new List<ElementId>();
            foreach (var idVal in finalIds)
            {
                var elId = RevitCompat.ToElementId(idVal);
                var el = doc.GetElement(elId);
                if (el == null)
                {
                    missingIds.Add(idVal);
                }
                else
                {
                    validIds.Add(elId);
                }
            }

            if (missingIds.Count > 0)
            {
                return CommandResult.Fail($"Some element IDs do not exist in the document: {string.Join(", ", missingIds)}");
            }

            if (validIds.Count == 0)
            {
                return CommandResult.Fail("No valid elements resolved to be saved.");
            }

            SelectionFilterElement existingFilter = new FilteredElementCollector(doc)
                .OfClass(typeof(SelectionFilterElement))
                .Cast<SelectionFilterElement>()
                .FirstOrDefault(f => string.Equals(f.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

            bool created = false;
            bool replaced = false;

            if (existingFilter != null)
            {
                if (!replaceExisting)
                {
                    return CommandResult.Fail($"A saved selection with the name '{name}' already exists. Set replaceExisting to true to overwrite.");
                }
                replaced = true;
            }
            else
            {
                created = true;
            }

            SelectionFilterElement filterElement = null;

            using (var tx = new Transaction(doc, "RvtMcp: save selection"))
            {
                tx.Start();
                try
                {
                    if (existingFilter != null)
                    {
                        filterElement = existingFilter;
                    }
                    else
                    {
                        filterElement = SelectionFilterElement.Create(doc, name.Trim());
                    }

                    filterElement.SetElementIds(validIds);

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                    {
                        return CommandResult.Fail($"Transaction commit failed: {status}");
                    }
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to save selection: {ex.Message}");
                }
            }

            return CommandResult.Ok(new
            {
                saved = true,
                created,
                replaced,
                selectionId = RevitCompat.GetId(filterElement.Id),
                name = filterElement.Name,
                source,
                count = validIds.Count,
                element_ids_included = includeElementIds,
                element_ids_truncated = includeElementIds && validIds.Count > maxElementIdResults,
                elementIds = includeElementIds
                    ? validIds.Select(id => RevitCompat.GetId(id)).Take(maxElementIdResults).ToArray()
                    : null,
                missing_count = missingIds.Count
            });
        }
    }
}
