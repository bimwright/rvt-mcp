using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateLineBasedElementHandler : IRevitCommand
    {
        public string Name => "create_line_based_element";
        public string Description => "Create a wall from start/end points";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""elementType"":{""type"":""string"",""description"":""wall (the only supported value)""},""startX"":{""type"":""number""},""startY"":{""type"":""number""},""endX"":{""type"":""number""},""endY"":{""type"":""number""},""level"":{""type"":""string""},""typeId"":{""type"":""integer""},""height"":{""type"":""number""}},""required"":[""elementType"",""startX"",""startY"",""endX"",""endY""]}";

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

            if (elementType != "wall")
                return CommandResult.Fail($"Element type '{elementType}' not supported. Supported: wall");

            // Check the requested type before creating anything: a non-wall type must fail, not fall back to the default.
            WallType wallType = null;
            if (typeId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(typeId.Value))
                    return CommandResult.Fail("typeId " + RevitCompat.ElementIdRangeError(typeId.Value));
                var typeElement = doc.GetElement(RevitCompat.ToElementId(typeId.Value));
                wallType = typeElement as WallType;
                if (wallType == null)
                    return CommandResult.Fail(typeElement == null
                        ? $"typeId {typeId.Value} does not exist in the document."
                        : $"typeId {typeId.Value} is a {typeElement.GetType().Name} ('{typeElement.Name}'), not a wall type. Pass a WallType id, for example the typeId revit_get_element_details returns for an existing wall.");
            }

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
                            if (wallType != null) wall.WallType = wallType;
                            created = wall;
                            break;

                        default:
                            tx.RollBack();
                            return CommandResult.Fail($"Element type '{elementType}' not supported. Supported: wall");
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
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
