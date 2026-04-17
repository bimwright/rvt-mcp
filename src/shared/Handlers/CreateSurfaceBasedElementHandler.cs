using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitMcp.Plugin.Handlers
{
    public class CreateSurfaceBasedElementHandler : IRevitCommand
    {
        public string Name => "create_surface_based_element";
        public string Description => "Create surface-based elements (floor, ceiling) from corner points";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""elementType"":{""type"":""string"",""description"":""floor, ceiling""},""points"":{""type"":""array"",""items"":{""type"":""object"",""properties"":{""x"":{""type"":""number""},""y"":{""type"":""number""}}}},""level"":{""type"":""string""},""typeId"":{""type"":""integer""}},""required"":[""elementType"",""points""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var elementType = request.Value<string>("elementType")?.ToLower() ?? "";
            var levelName = request.Value<string>("level");
            var typeId = request.Value<long?>("typeId");
            var points = request["points"] as JArray;

            if (points == null || points.Count < 3)
                return CommandResult.Fail("At least 3 corner points are required. Each point: {x, y} in mm.");

            // Find level
            var level = FindLevel(doc, levelName);
            if (level == null)
                return CommandResult.Fail("No level found.");

            // Build curve loop from points (mm → feet)
            var curveLoop = new CurveLoop();
            for (int i = 0; i < points.Count; i++)
            {
                var pt1 = points[i];
                var pt2 = points[(i + 1) % points.Count];
                var p1 = new XYZ(pt1.Value<double>("x") / 304.8, pt1.Value<double>("y") / 304.8, 0);
                var p2 = new XYZ(pt2.Value<double>("x") / 304.8, pt2.Value<double>("y") / 304.8, 0);
                curveLoop.Append(Line.CreateBound(p1, p2));
            }

            using (var tx = new Transaction(doc, "MCP: Create " + elementType))
            {
                tx.Start();
                try
                {
                    Element created = null;
                    switch (elementType)
                    {
                        case "floor":
                            var floorTypeEl = typeId.HasValue ? doc.GetElement(RevitCompat.ToElementId(typeId.Value)) as FloorType : null;
                            if (floorTypeEl == null)
                            {
                                floorTypeEl = new FilteredElementCollector(doc)
                                    .OfClass(typeof(FloorType))
                                    .FirstElement() as FloorType;
                            }
                            // Floor.Create works on both Revit 2022 and 2024 (replaces deprecated NewFloor).
                            created = Floor.Create(doc, new List<CurveLoop> { curveLoop }, floorTypeEl.Id, level.Id);
                            break;

                        case "ceiling":
                            var ceilingTypeEl = typeId.HasValue ? doc.GetElement(RevitCompat.ToElementId(typeId.Value)) as CeilingType : null;
                            if (ceilingTypeEl == null)
                            {
                                ceilingTypeEl = new FilteredElementCollector(doc)
                                    .OfClass(typeof(CeilingType))
                                    .FirstElement() as CeilingType;
                            }
                            created = Ceiling.Create(doc, new List<CurveLoop> { curveLoop }, ceilingTypeEl.Id, level.Id);
                            break;

                        default:
                            tx.RollBack();
                            return CommandResult.Fail($"Element type '{elementType}' not supported. Supported: floor, ceiling");
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

        private Level FindLevel(Document doc, string levelName)
        {
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level));
            foreach (Level lv in levels)
            {
                if (!string.IsNullOrEmpty(levelName) && lv.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase))
                    return lv;
            }
            foreach (Level lv in levels) return lv;
            return null;
        }
    }
}
