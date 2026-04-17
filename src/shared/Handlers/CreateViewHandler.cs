using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace Bimwright.Rvt.Plugin.Handlers
{
    public class CreateViewHandler : IRevitCommand
    {
        public string Name => "create_view";
        public string Description => "Create a floor plan, section, or 3D view";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""viewType"":{""type"":""string"",""enum"":[""FloorPlan"",""CeilingPlan"",""Section"",""Elevation"",""3D"",""Sheet""]},""level"":{""type"":""string""},""name"":{""type"":""string""}},""required"":[""viewType""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var viewType = request.Value<string>("viewType")?.ToLower() ?? "";
            var levelName = request.Value<string>("level");
            var name = request.Value<string>("name");

            using (var tx = new Transaction(doc, "MCP: Create View"))
            {
                tx.Start();
                try
                {
                    View created = null;

                    switch (viewType)
                    {
                        case "floorplan":
                        case "floor plan":
                            var level = FindLevel(doc, levelName);
                            if (level == null)
                            {
                                tx.RollBack();
                                return CommandResult.Fail("Level not found.");
                            }
                            var floorPlanType = new FilteredElementCollector(doc)
                                .OfClass(typeof(ViewFamilyType))
                                .Cast<ViewFamilyType>()
                                .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.FloorPlan);
                            if (floorPlanType == null)
                            {
                                tx.RollBack();
                                return CommandResult.Fail("No floor plan view family type found.");
                            }
                            created = ViewPlan.Create(doc, floorPlanType.Id, level.Id);
                            break;

                        case "3d":
                        case "3dview":
                            var threeDType = new FilteredElementCollector(doc)
                                .OfClass(typeof(ViewFamilyType))
                                .Cast<ViewFamilyType>()
                                .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.ThreeDimensional);
                            if (threeDType == null)
                            {
                                tx.RollBack();
                                return CommandResult.Fail("No 3D view family type found.");
                            }
                            created = View3D.CreateIsometric(doc, threeDType.Id);
                            break;

                        default:
                            tx.RollBack();
                            return CommandResult.Fail($"View type '{viewType}' not supported. Supported: floorplan, 3d");
                    }

                    if (!string.IsNullOrEmpty(name))
                        created.Name = name;

                    tx.Commit();
                    return CommandResult.Ok(new
                    {
                        elementId = RevitCompat.GetId(created.Id),
                        viewName = created.Name,
                        viewType = created.ViewType.ToString()
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to create view: {ex.Message}");
                }
            }
        }

        private Level FindLevel(Document doc, string levelName)
        {
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>();
            if (!string.IsNullOrEmpty(levelName))
            {
                var match = levels.FirstOrDefault(lv => lv.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
            return levels.OrderBy(lv => lv.Elevation).FirstOrDefault();
        }
    }
}
