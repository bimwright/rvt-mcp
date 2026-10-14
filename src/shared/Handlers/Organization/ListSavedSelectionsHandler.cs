using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ListSavedSelectionsHandler : IRevitCommand
    {
        public string Name => "list_saved_selections";
        public string Description => "List all saved named selection filters in the current document.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""nameFilter"": { ""type"": ""string"" },
    ""includeElementIds"": { ""type"": ""boolean"", ""default"": false },
    ""includeElementSummary"": { ""type"": ""boolean"", ""default"": false },
    ""limit"": { ""type"": ""integer"", ""default"": 500, ""minimum"": 1, ""maximum"": 2000 }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            var nameFilter = request.Value<string>("nameFilter");
            var includeElementIds = request.Value<bool?>("includeElementIds") ?? false;
            var includeElementSummary = request.Value<bool?>("includeElementSummary") ?? false;
            var limit = request.Value<int?>("limit") ?? 500;

            if (limit < 1 || limit > 2000)
                return CommandResult.Fail("limit must be between 1 and 2000.");

            var selections = new FilteredElementCollector(doc)
                .OfClass(typeof(SelectionFilterElement))
                .Cast<SelectionFilterElement>()
                .ToList();

            var selectionsList = new List<object>();

            foreach (var filter in selections)
            {
                if (filter == null) continue;

                if (!string.IsNullOrEmpty(nameFilter) &&
                    filter.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (selectionsList.Count >= limit) break;

                var elementIds = filter.GetElementIds();
                int staleCount = 0;
                var elements = new List<object>();

                foreach (var id in elementIds)
                {
                    var el = doc.GetElement(id);
                    if (el == null)
                    {
                        staleCount++;
                        continue;
                    }

                    if (includeElementSummary)
                    {
                        elements.Add(new
                        {
                            elementId = RevitCompat.GetId(id),
                            name = el.Name,
                            category = el.Category?.Name,
                            typeName = doc.GetElement(el.GetTypeId())?.Name
                        });
                    }
                }

                selectionsList.Add(new
                {
                    selectionId = RevitCompat.GetId(filter.Id),
                    name = filter.Name,
                    count = elementIds.Count,
                    staleCount = staleCount,
                    elementIds = includeElementIds ? elementIds.Select(id => RevitCompat.GetId(id)).ToArray() : null,
                    elements = includeElementSummary ? elements.ToArray() : null
                });
            }

            return CommandResult.Ok(new
            {
                count = selections.Count,
                returned = selectionsList.Count,
                selections = selectionsList.OrderBy(s => ((dynamic)s).name).ToArray()
            });
        }
    }
}
