using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class AnalyzeStructuralConnectionsHandler : IRevitCommand
    {
        public string Name => "analyze_structural_connections";
        public string Description => "Audit structural joins between columns/beams. Reports per-element: joined neighbor count + neighbor ids. Optional element_ids filter; default = all structural framing + columns in model.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},""limit"":{""type"":""integer"",""default"":500,""minimum"":1,""maximum"":500}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var limit = req.Value<int?>("limit") ?? 500;
            if (limit < 1 || limit > 500)
                return CommandResult.Fail("limit must be between 1 and the hard maximum of 500.");
            var idsToken = req["element_ids"] as JArray;

            IEnumerable<Element> elements;
            if (idsToken != null && idsToken.Any())
            {
                elements = idsToken
                    .Select(t => doc.GetElement(RevitCompat.ToElementId(t.Value<long>())))
                    .Where(e => e != null);
            }
            else
            {
                var framing = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_StructuralFraming)
                    .WhereElementIsNotElementType();
                var columns = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_StructuralColumns)
                    .WhereElementIsNotElementType();
                elements = framing.Cast<Element>().Concat(columns.Cast<Element>());
            }

            var items = new List<object>();
            foreach (var element in elements.Take(limit))
            {
                var joinedWith = new List<long>();
                try
                {
                    joinedWith = JoinGeometryUtils.GetJoinedElements(doc, element)
                        .Select(RevitCompat.GetId)
                        .ToList();
                }
                catch
                {
                    // Some elements/categories cannot participate in geometric joins.
                }

                items.Add(new
                {
                    id = RevitCompat.GetId(element.Id),
                    category = element.Category?.Name,
                    name = element.Name,
                    joined_count = joinedWith.Count,
                    joined_with = joinedWith
                });
            }

            return CommandResult.Ok(new
            {
                count = items.Count,
                items
            });
        }
    }
}
