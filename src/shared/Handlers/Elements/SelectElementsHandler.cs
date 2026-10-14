using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class SelectElementsHandler : IRevitCommand
    {
        public string Name => "select_elements";
        public string Description => "Select elements in the Revit user interface by ID or saved selection.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""elementIds"": {
      ""type"": ""array"",
      ""items"": { ""type"": ""integer"" },
      ""description"": ""Explicit element IDs to select.""
    },
    ""savedSelectionId"": {
      ""type"": ""integer"",
      ""description"": ""ID of a saved named selection filter to select.""
    },
    ""savedSelectionName"": {
      ""type"": ""string"",
      ""description"": ""Name of a saved named selection filter to select.""
    },
    ""zoomToSelection"": {
      ""type"": ""boolean"",
      ""default"": false,
      ""description"": ""If true, zooms the active view to show the selected elements.""
    }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (uidoc == null || doc == null)
                return CommandResult.Fail("No active document or UI document is available.");

            var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            var elementIdsInput = request["elementIds"]?.ToObject<long[]>();
            var savedSelectionId = request.Value<long?>("savedSelectionId");
            var savedSelectionName = request.Value<string>("savedSelectionName");
            var zoomToSelection = request.Value<bool?>("zoomToSelection") ?? false;

            // Validate that exactly one of the selection sources is provided
            int providedSources = 0;
            if (elementIdsInput != null) providedSources++;
            if (savedSelectionId.HasValue) providedSources++;
            if (!string.IsNullOrEmpty(savedSelectionName)) providedSources++;

            if (providedSources == 0)
            {
                return CommandResult.Fail("At least one of elementIds, savedSelectionId, or savedSelectionName must be provided.");
            }
            if (providedSources > 1)
            {
                return CommandResult.Fail("Only one of elementIds, savedSelectionId, or savedSelectionName can be specified, not multiple.");
            }

            var validIds = new List<ElementId>();
            var missingIds = new List<long>();
            string source = "";

            if (elementIdsInput != null)
            {
                source = "explicitIds";
                foreach (var idVal in elementIdsInput)
                {
                    if (!RevitCompat.CanRepresentElementId(idVal))
                        return CommandResult.Fail(RevitCompat.ElementIdRangeError(idVal));

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
            }
            else if (savedSelectionId.HasValue)
            {
                source = "savedSelectionId";
                if (!RevitCompat.CanRepresentElementId(savedSelectionId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(savedSelectionId.Value));

                var filterId = RevitCompat.ToElementId(savedSelectionId.Value);
                var filter = doc.GetElement(filterId) as SelectionFilterElement;
                if (filter == null)
                    return CommandResult.Fail($"SelectionFilterElement with ID {savedSelectionId.Value} not found.");

                var elementIds = filter.GetElementIds();
                foreach (var id in elementIds)
                {
                    var el = doc.GetElement(id);
                    if (el == null)
                    {
                        missingIds.Add(RevitCompat.GetId(id));
                    }
                    else
                    {
                        validIds.Add(id);
                    }
                }
            }
            else // savedSelectionName is not empty
            {
                source = "savedSelectionName";
                var matches = new FilteredElementCollector(doc)
                    .OfClass(typeof(SelectionFilterElement))
                    .Cast<SelectionFilterElement>()
                    .Where(f => string.Equals(f.Name, savedSelectionName.Trim(), StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matches.Count == 0)
                    return CommandResult.Fail($"Selection filter named '{savedSelectionName}' not found.");
                if (matches.Count > 1)
                    return CommandResult.Fail($"Multiple selection filters found with the name '{savedSelectionName}'. Use savedSelectionId instead.");

                var filter = matches[0];
                var elementIds = filter.GetElementIds();
                foreach (var id in elementIds)
                {
                    var el = doc.GetElement(id);
                    if (el == null)
                    {
                        missingIds.Add(RevitCompat.GetId(id));
                    }
                    else
                    {
                        validIds.Add(id);
                    }
                }
            }

            // Perform UI Selection (DO NOT open a document transaction)
            uidoc.Selection.SetElementIds(validIds);

            bool zoomed = false;
            if (zoomToSelection && validIds.Count > 0)
            {
                try
                {
                    uidoc.ShowElements(validIds);
                    zoomed = true;
                }
                catch (Exception)
                {
                    // Swallowing exception for show elements if view does not support zooming/showing.
                }
            }

            return CommandResult.Ok(new
            {
                selected = true,
                count = validIds.Count,
                source = source,
                elementIds = validIds.Select(id => RevitCompat.GetId(id)).ToArray(),
                missingIds = missingIds.ToArray(),
                zoomed = zoomed
            });
        }
    }
}
