using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateStructuralWallHandler : IRevitCommand
    {
        public string Name => "create_structural_wall";
        public string Description => "Create a structural wall (isStructural=true) between two points on a level. Uses Wall.Create with structural flag.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""wall_type_id"":{""type"":""integer""},""wall_type_name"":{""type"":""string""},""start_x_mm"":{""type"":""number""},""start_y_mm"":{""type"":""number""},""end_x_mm"":{""type"":""number""},""end_y_mm"":{""type"":""number""},""level_id"":{""type"":""integer""},""level_name"":{""type"":""string""},""height_mm"":{""type"":""number""}},""required"":[""start_x_mm"",""start_y_mm"",""end_x_mm"",""end_y_mm""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var typeId = req.Value<long?>("wall_type_id");
            var typeName = req.Value<string>("wall_type_name");
            var sx = req.Value<double?>("start_x_mm") ?? 0;
            var sy = req.Value<double?>("start_y_mm") ?? 0;
            var ex = req.Value<double?>("end_x_mm") ?? 0;
            var ey = req.Value<double?>("end_y_mm") ?? 0;
            var levelId = req.Value<long?>("level_id");
            var levelName = req.Value<string>("level_name");
            var heightMm = req.Value<double?>("height_mm") ?? 3000;

            var level = ResolveLevel(doc, levelId, levelName);
            if (level == null) return CommandResult.Fail("Could not resolve Level.");

            var start = new XYZ(sx / 304.8, sy / 304.8, 0);
            var end = new XYZ(ex / 304.8, ey / 304.8, 0);
            if (start.DistanceTo(end) < 1e-6)
                return CommandResult.Fail("start and end points are identical.");
            var line = Line.CreateBound(start, end);

            using (var tx = new Transaction(doc, "RvtMcp: Create structural wall"))
            {
                tx.Start();
                try
                {
                    var wall = Wall.Create(doc, line, level.Id, true); // isStructural=true
                    wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.Set(heightMm / 304.8);

                    if (typeId.HasValue || !string.IsNullOrWhiteSpace(typeName))
                    {
                        var wallType = ResolveWallType(doc, typeId, typeName);
                        if (wallType != null) wall.WallType = wallType;
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
                    return CommandResult.Ok(new
                    {
                        created_id = RevitCompat.GetId(wall.Id),
                        wall_type_id = RevitCompat.GetId(wall.WallType.Id),
                        level_id = RevitCompat.GetId(level.Id),
                        height_mm = heightMm,
                        is_structural = true
                    });
                }
                catch (Exception ex_)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to create structural wall: {ex_.Message}");
                }
            }
        }

        private static WallType ResolveWallType(Document doc, long? typeId, string typeName)
        {
            if (typeId.HasValue)
                return doc.GetElement(RevitCompat.ToElementId(typeId.Value)) as WallType;
            var types = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>();
            if (!string.IsNullOrWhiteSpace(typeName))
                return types.FirstOrDefault(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
            return null;
        }

        private static Level ResolveLevel(Document doc, long? levelId, string levelName)
        {
            if (levelId.HasValue)
                return doc.GetElement(RevitCompat.ToElementId(levelId.Value)) as Level;
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>();
            if (!string.IsNullOrWhiteSpace(levelName))
                return levels.FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
            return levels.OrderBy(l => l.Elevation).FirstOrDefault();
        }
    }
}
