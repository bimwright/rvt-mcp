using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Attaches walls' top constraint to a level ("Up to level") with an optional offset, because
    /// create_line_based_element only sets an unconnected height.
    /// </summary>
    public class SetWallTopHandler : IRevitCommand
    {
        private const double MmPerFoot = 304.8;

        public string Name => "set_wall_top";

        public string Description => "Set walls' top constraint to a level (Up to level) with an optional top offset in mm";

        public string ParametersSchema => @"{""type"":""object"",""properties"":{""wall_ids"":{""type"":""array"",""items"":{""type"":""integer""},""description"":""Wall element ids""},""top_level"":{""type"":""string"",""description"":""Level name, e.g. Level 2""},""top_offset"":{""type"":""number"",""description"":""Top offset in mm (default 0)""}},""required"":[""wall_ids"",""top_level""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(string.IsNullOrWhiteSpace(paramsJson) ? "{}" : paramsJson);
            var ids = request["wall_ids"]?.Values<long>().ToList() ?? new List<long>();
            var levelName = request.Value<string>("top_level")?.Trim();
            var offsetMm = request.Value<double?>("top_offset") ?? 0;
            if (ids.Count == 0)
                return CommandResult.Fail("wall_ids must list at least one wall id.");

            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            var level = levels.FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.Ordinal))
                ?? levels.FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase));
            if (level == null)
                return CommandResult.Fail("No level named '" + levelName + "'. Levels: " + string.Join(", ", levels.Select(l => l.Name)));

            // Check every wall before changing any, so a bad id or a top below a base changes nothing.
            var walls = new List<Wall>();
            var topFt = level.Elevation + offsetMm / MmPerFoot;
            for (var i = 0; i < ids.Count; i++)
            {
                var id = ids[i];
                if (!RevitCompat.CanRepresentElementId(id))
                    return CommandResult.Fail("wall_ids[" + i.ToString(CultureInfo.InvariantCulture) + "] " + RevitCompat.ElementIdRangeError(id) + " Nothing was changed.");
                if (!(doc.GetElement(RevitCompat.ToElementId(id)) is Wall wall))
                    return CommandResult.Fail("Element " + id + " is not a wall. Nothing was changed.");
                var baseLevel = doc.GetElement(wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId() ?? ElementId.InvalidElementId) as Level;
                var baseOffset = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0;
                if (baseLevel != null && topFt <= baseLevel.Elevation + baseOffset + 1e-6)
                    return CommandResult.Fail("Wall " + id + " starts at " + Mm(baseLevel.Elevation + baseOffset) + " mm ('" + baseLevel.Name + "'); a top at "
                        + Mm(topFt) + " mm would be at or below its base. Nothing was changed.");
                walls.Add(wall);
            }

            using (var tx = new Transaction(doc, "MCP: Set wall top"))
            {
                tx.Start();
                try
                {
                    foreach (var wall in walls)
                    {
                        var id = RevitCompat.GetId(wall.Id);
                        var top = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);
                        if (top == null || top.IsReadOnly)
                        {
                            tx.RollBack();
                            return CommandResult.Fail("Wall " + id + " does not allow changing its top constraint. Nothing was changed.");
                        }
                        top.Set(level.Id);
                        // Top Offset is read-only while the wall is Unconnected, so look it up only after attaching.
                        var offset = wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET);
                        if (offset == null || offset.IsReadOnly)
                        {
                            tx.RollBack();
                            return CommandResult.Fail("Wall " + id + " does not allow a top offset. Nothing was changed.");
                        }
                        offset.Set(offsetMm / MmPerFoot);
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Failed to set wall top: " + ex.Message + " Nothing was changed.");
                }
            }

            // Report what Revit stored, since joins and attachments can change the resulting height.
            var result = walls.Select(wall =>
            {
                var topId = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId();
                var offset = wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET);
                var height = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                return new
                {
                    wall_id = RevitCompat.GetId(wall.Id),
                    top_constraint = topId == null ? null : doc.GetElement(topId)?.Name,
                    top_offset_mm = offset == null ? (double?)null : Mm(offset.AsDouble()),
                    height_mm = height == null ? (double?)null : Mm(height.AsDouble())
                };
            }).ToList();
            return CommandResult.Ok(new { updated = result.Count, walls = result });
        }

        private static double Mm(double feet) => Math.Round(feet * MmPerFoot, 1);
    }
}
