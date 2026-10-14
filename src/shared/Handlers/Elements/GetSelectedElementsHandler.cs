using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class GetSelectedElementsHandler : IRevitCommand
    {
        public string Name => "get_selected_elements";
        public string Description => "Get a bounded page of currently selected elements. Elements deleted between selection and retrieval are reported in staleIds.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""start_index"":{""type"":""integer"",""default"":0,""minimum"":0},""max_results"":{""type"":""integer"",""default"":200,""minimum"":1,""maximum"":1000}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null)
                return CommandResult.Fail("No document is open.");

            var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            if (!ResponsePaging.TryParse(request, "start_index", "max_results", 200, 1000, out var options, out var pagingError))
                return CommandResult.Fail(pagingError);

            var selectedIds = uidoc.Selection.GetElementIds()
                .OrderBy(RevitCompat.GetId)
                .ToArray();
            var page = ResponsePaging.Slice(selectedIds, options.StartIndex, options.MaxResults);
            var doc = uidoc.Document;
            var elements = new List<object>();
            var staleIds = new List<long>();

            foreach (var id in page.Items)
            {
                var el = doc.GetElement(id);
                if (el == null)
                {
                    staleIds.Add(RevitCompat.GetId(id));
                    continue;
                }
                elements.Add(new
                {
                    elementId = RevitCompat.GetId(id),
                    name = el.Name,
                    category = el.Category?.Name,
                    typeName = doc.GetElement(el.GetTypeId())?.Name
                });
            }

            return CommandResult.Ok(new
            {
                total_count = page.TotalCount,
                start_index = page.StartIndex,
                returned_count = page.ReturnedCount,
                truncated = page.Truncated,
                next_index = page.NextIndex,
                elements,
                staleIds
            });
        }
    }
}
