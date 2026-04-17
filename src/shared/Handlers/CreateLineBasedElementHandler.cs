using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace Bimwright.Plugin.Handlers
{
    public class CreateLineBasedElementHandler : IRevitCommand
    {
        public string Name => "create_line_based_element";
        public string Description => "Create line-based elements (wall, beam, pipe) from start/end points";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""elementType"":{""type"":""string"",""description"":""wall, pipe, duct, cabletray""},""startX"":{""type"":""number""},""startY"":{""type"":""number""},""endX"":{""type"":""number""},""endY"":{""type"":""number""},""level"":{""type"":""string""},""typeId"":{""type"":""integer""},""height"":{""type"":""number""}},""required"":[""elementType"",""startX"",""startY"",""endX"",""endY""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var elementType = request.Value<string>("elementType")?.ToLower() ?? "";
            var startX = request.Value<double>("startX");
            var startY = request.Value<double>("startY");
            var endX = request.Value<double>("endX");
            var endY = request.Value<double>("endY");
            var levelName = request.Value<string>("level");
            var typeId = request.Value<long?>("typeId");
            var height = request.Value<double?>("height") ?? 3000; // mm default

            // Convert mm to feet
            var startPt = new XYZ(startX / 304.8, startY / 304.8, 0);
            var endPt = new XYZ(endX / 304.8, endY / 304.8, 0);
            var heightFt = height / 304.8;
            var line = Line.CreateBound(startPt, endPt);

            // Find level
            Level level = null;
            if (!string.IsNullOrEmpty(levelName))
            {
                var collector = new FilteredElementCollector(doc).OfClass(typeof(Level));
                foreach (Level lv in collector)
                {
                    if (lv.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase))
                    {
                        level = lv;
                        break;
                    }
                }
            }
            if (level == null)
            {
                var collector = new FilteredElementCollector(doc).OfClass(typeof(Level));
                foreach (Level lv in collector)
                {
                    level = lv;
                    break;
                }
            }
            if (level == null)
                return CommandResult.Fail("No level found in the project.");

            using (var tx = new Transaction(doc, "MCP: Create " + elementType))
            {
                tx.Start();
                try
                {
                    Element created = null;
                    switch (elementType)
                    {
                        case "wall":
                            var wall = Wall.Create(doc, line, level.Id, false);
                            wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.Set(heightFt);
                            if (typeId.HasValue)
                            {
                                var wallType = doc.GetElement(RevitCompat.ToElementId(typeId.Value)) as WallType;
                                if (wallType != null) wall.WallType = wallType;
                            }
                            created = wall;
                            break;

                        default:
                            tx.RollBack();
                            return CommandResult.Fail($"Element type '{elementType}' not supported. Supported: wall");
                    }

                    tx.Commit();
                    return CommandResult.Ok(new
                    {
                        elementId = RevitCompat.GetId(created.Id),
                        elementType,
                        name = created.Name,
                        category = created.Category?.Name
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to create {elementType}: {ex.Message}");
                }
            }
        }
    }
}
