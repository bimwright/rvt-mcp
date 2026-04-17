using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitMcp.Plugin.Handlers
{
    public class CreatePointBasedElementHandler : IRevitCommand
    {
        public string Name => "create_point_based_element";
        public string Description => "Create point-based elements (door, window, furniture) at a location";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""typeId"":{""type"":""integer""},""x"":{""type"":""number""},""y"":{""type"":""number""},""z"":{""type"":""number""},""level"":{""type"":""string""}},""required"":[""typeId"",""x"",""y""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var typeId = request.Value<long>("typeId");
            var x = request.Value<double>("x");
            var y = request.Value<double>("y");
            var z = request.Value<double?>("z") ?? 0;
            var levelName = request.Value<string>("level");

            var point = new XYZ(x / 304.8, y / 304.8, z / 304.8);

            var familySymbol = doc.GetElement(RevitCompat.ToElementId(typeId)) as FamilySymbol;
            if (familySymbol == null)
                return CommandResult.Fail($"Family type with ID {typeId} not found. Use get_available_family_types to find valid IDs.");

            // Find level
            Level level = null;
            if (!string.IsNullOrEmpty(levelName))
            {
                level = new FilteredElementCollector(doc).OfClass(typeof(Level))
                    .Cast<Level>()
                    .FirstOrDefault(lv => lv.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
            }
            if (level == null)
            {
                level = new FilteredElementCollector(doc).OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(lv => lv.Elevation)
                    .FirstOrDefault();
            }
            if (level == null)
                return CommandResult.Fail("No level found in the project.");

            using (var tx = new Transaction(doc, "MCP: Create family instance"))
            {
                tx.Start();
                try
                {
                    if (!familySymbol.IsActive)
                        familySymbol.Activate();

                    var instance = doc.Create.NewFamilyInstance(point, familySymbol, level, StructuralType.NonStructural);
                    tx.Commit();

                    return CommandResult.Ok(new
                    {
                        elementId = RevitCompat.GetId(instance.Id),
                        familyName = familySymbol.FamilyName,
                        typeName = familySymbol.Name,
                        category = instance.Category?.Name
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to create instance: {ex.Message}");
                }
            }
        }
    }
}
