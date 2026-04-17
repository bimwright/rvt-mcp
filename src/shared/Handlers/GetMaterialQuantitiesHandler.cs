using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace Bimwright.Rvt.Plugin.Handlers
{
    public class GetMaterialQuantitiesHandler : IRevitCommand
    {
        public string Name => "get_material_quantities";
        public string Description => "Calculate material quantities from elements by category";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""category"":{""type"":""string"",""description"":""Built-in category name""}},""required"":[""category""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var categoryName = request.Value<string>("category");

            if (string.IsNullOrEmpty(categoryName))
                return CommandResult.Fail("category is required (e.g. 'Walls', 'Floors', 'Roofs').");

            // Find BuiltInCategory
            BuiltInCategory? bic = null;
            foreach (BuiltInCategory cat in Enum.GetValues(typeof(BuiltInCategory)))
            {
                try
                {
                    var c = Category.GetCategory(doc, cat);
                    if (c != null && c.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase))
                    {
                        bic = cat;
                        break;
                    }
                }
                catch { }
            }

            if (bic == null)
                return CommandResult.Fail($"Category '{categoryName}' not found.");

            var elements = new FilteredElementCollector(doc)
                .OfCategory(bic.Value)
                .WhereElementIsNotElementType()
                .ToList();

            var materialData = elements
                .SelectMany(el =>
                {
                    var matIds = el.GetMaterialIds(false);
                    return matIds.Select(matId =>
                    {
                        var mat = doc.GetElement(matId) as Material;
                        var area = el.GetMaterialArea(matId, false);
                        var volume = el.GetMaterialVolume(matId);
                        return new
                        {
                            materialName = mat?.Name ?? "Unknown",
                            area = Math.Round(area * 0.09290304, 4),     // sqft → m²
                            volume = Math.Round(volume * 0.0283168, 6)   // cuft → m³
                        };
                    });
                })
                .GroupBy(m => m.materialName)
                .Select(g => new
                {
                    material = g.Key,
                    totalAreaM2 = Math.Round(g.Sum(x => x.area), 2),
                    totalVolumeM3 = Math.Round(g.Sum(x => x.volume), 4)
                })
                .OrderByDescending(m => m.totalVolumeM3)
                .ToArray();

            return CommandResult.Ok(new
            {
                category = categoryName,
                elementCount = elements.Count,
                materialCount = materialData.Length,
                materials = materialData
            });
        }
    }
}
