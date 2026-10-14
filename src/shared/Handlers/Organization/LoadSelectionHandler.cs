using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class LoadSelectionHandler : IRevitCommand
    {
        public string Name => "load_selection";
        public string Description => "Load a saved selection filter and return its elements.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""name"": { ""type"": ""string"" },
    ""selectionId"": { ""type"": ""integer"" },
    ""includeElementSummary"": { ""type"": ""boolean"", ""default"": false },
    ""start_index"": { ""type"": ""integer"", ""default"": 0, ""minimum"": 0 },
    ""max_results"": { ""type"": ""integer"", ""default"": 200, ""minimum"": 1, ""maximum"": 1000 }
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
            var includeElementSummary = request.Value<bool?>("includeElementSummary") ?? false;
            if (!ResponsePaging.TryParse(request, "start_index", "max_results", 200, 1000, out var paging, out var pagingError))
                return CommandResult.Fail(pagingError);

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

            var elementIds = targetFilter.GetElementIds()
                .OrderBy(RevitCompat.GetId)
                .ToArray();
            var page = ResponsePaging.Slice(elementIds, paging.StartIndex, paging.MaxResults);
            var staleIds = new List<long>();
            var elements = new List<object>();

            foreach (var id in page.Items)
            {
                var el = doc.GetElement(id);
                if (el == null)
                {
                    staleIds.Add(RevitCompat.GetId(id));
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

            return CommandResult.Ok(new
            {
                selectionId = RevitCompat.GetId(targetFilter.Id),
                name = targetFilter.Name,
                count = page.TotalCount,
                start_index = page.StartIndex,
                returned_count = page.ReturnedCount,
                truncated = page.Truncated,
                next_index = page.NextIndex,
                elementIds = page.Items.Select(id => RevitCompat.GetId(id)).ToArray(),
                staleIds = staleIds.ToArray(),
                elements = includeElementSummary ? elements.ToArray() : null
            });
        }
    }
}
