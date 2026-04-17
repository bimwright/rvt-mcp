using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcp.Plugin.Handlers
{
    public class GetFamilyTypesHandler : IRevitCommand
    {
        public string Name => "get_available_family_types";
        public string Description => "Get available family types in current project";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""category"":{""type"":""string"",""description"":""Built-in category name (e.g. Walls, Doors, Pipes)""}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = Newtonsoft.Json.Linq.JObject.Parse(paramsJson);
            var categoryFilter = request.Value<string>("category");

            var collector = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .WhereElementIsElementType();

            var familyTypes = collector
                .Cast<FamilySymbol>()
                .Where(fs => string.IsNullOrEmpty(categoryFilter)
                             || (fs.Category != null && fs.Category.Name.ToLower().Contains(categoryFilter.ToLower())))
                .GroupBy(fs => fs.Category?.Name ?? "Uncategorized")
                .Select(g => new
                {
                    category = g.Key,
                    count = g.Count(),
                    types = g.Select(fs => new
                    {
                        typeId = RevitCompat.GetId(fs.Id),
                        familyName = fs.FamilyName,
                        typeName = fs.Name
                    }).ToArray()
                })
                .OrderBy(g => g.category)
                .ToArray();

            return CommandResult.Ok(new
            {
                totalCategories = familyTypes.Length,
                totalTypes = familyTypes.Sum(g => g.count),
                categories = familyTypes
            });
        }
    }
}
