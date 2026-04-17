using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace Bimwright.Plugin.Handlers
{
    public class ColorElementsHandler : IRevitCommand
    {
        public string Name => "color_elements";
        public string Description => "Color elements based on a parameter value";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""category"":{""type"":""string""},""parameterName"":{""type"":""string""}},""required"":[""category"",""parameterName""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var category = request.Value<string>("category");
            var paramName = request.Value<string>("parameterName");

            if (string.IsNullOrEmpty(category) || string.IsNullOrEmpty(paramName))
                return CommandResult.Fail("category and parameterName are required.");

            // Find BuiltInCategory
            BuiltInCategory? bic = null;
            foreach (BuiltInCategory cat in Enum.GetValues(typeof(BuiltInCategory)))
            {
                try
                {
                    var c = Category.GetCategory(doc, cat);
                    if (c != null && c.Name.Equals(category, StringComparison.OrdinalIgnoreCase))
                    { bic = cat; break; }
                }
                catch { }
            }
            if (bic == null)
                return CommandResult.Fail($"Category '{category}' not found.");

            var elements = new FilteredElementCollector(doc)
                .OfCategory(bic.Value)
                .WhereElementIsNotElementType()
                .ToList();

            // Group by parameter value
            var groups = elements
                .GroupBy(el =>
                {
                    var p = el.LookupParameter(paramName);
                    if (p == null) return "(no parameter)";
                    switch (p.StorageType)
                    {
                        case StorageType.String: return p.AsString() ?? "(empty)";
                        case StorageType.Integer: return p.AsInteger().ToString();
                        case StorageType.Double: return p.AsDouble().ToString("F2");
                        case StorageType.ElementId:
                            var refEl = doc.GetElement(p.AsElementId());
                            return refEl?.Name ?? RevitCompat.GetId(p.AsElementId()).ToString();
                        default: return "(unknown)";
                    }
                })
                .ToList();

            // Assign colors
            var colors = new[]
            {
                new Color(255, 0, 0), new Color(0, 128, 255), new Color(0, 200, 0),
                new Color(255, 165, 0), new Color(128, 0, 255), new Color(255, 255, 0),
                new Color(0, 200, 200), new Color(200, 0, 100), new Color(100, 200, 0),
                new Color(200, 100, 0)
            };

            var view = doc.ActiveView;
            int coloredCount = 0;

            using (var tx = new Transaction(doc, "MCP: Color elements by parameter"))
            {
                tx.Start();
                for (int i = 0; i < groups.Count; i++)
                {
                    var color = colors[i % colors.Length];
                    var ogs = new OverrideGraphicSettings();
                    ogs.SetProjectionLineColor(color);
                    ogs.SetSurfaceForegroundPatternColor(color);

                    foreach (var el in groups[i])
                    {
                        view.SetElementOverrides(el.Id, ogs);
                        coloredCount++;
                    }
                }
                tx.Commit();
            }

            var summary = groups.Select((g, i) => new
            {
                value = g.Key,
                count = g.Count(),
                color = $"RGB({colors[i % colors.Length].Red},{colors[i % colors.Length].Green},{colors[i % colors.Length].Blue})"
            }).ToArray();

            return CommandResult.Ok(new { coloredCount, groups = summary });
        }
    }
}
