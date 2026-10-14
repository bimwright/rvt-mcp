using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateRoomSeparatorHandler : IRevitCommand
    {
        public string Name => "create_room_separator";
        public string Description => "Create room separation lines from supplied model-space points";
        public string ParametersSchema => @"{
            ""type"": ""object"",
            ""required"": [""points""],
            ""properties"": {
                ""points"": {
                    ""type"": ""array"",
                    ""minItems"": 2,
                    ""items"": {
                        ""type"": ""object"",
                        ""required"": [""x"", ""y""],
                        ""properties"": {
                            ""x"": { ""type"": ""number"" },
                            ""y"": { ""type"": ""number"" },
                            ""z"": { ""type"": ""number"" }
                        }
                    }
                },
                ""view_id"": { ""type"": ""integer"" },
                ""level_name"": { ""type"": ""string"" },
                ""close_loop"": { ""type"": ""boolean"" }
            }
        }";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            JArray pointsArray;
            long? viewId = null;
            string levelName = "";
            bool closeLoop = false;

            try
            {
                var request = JObject.Parse(paramsJson);
                if (!request.TryGetValue("points", out var ptsVal) || !(ptsVal is JArray))
                    return CommandResult.Fail("points is a required array.");

                pointsArray = (JArray)ptsVal;
                if (request.TryGetValue("view_id", out var viewIdVal))
                    viewId = viewIdVal.Value<long?>();
                if (request.TryGetValue("level_name", out var lvVal))
                    levelName = lvVal.Value<string>() ?? "";
                if (request.TryGetValue("close_loop", out var closeVal))
                    closeLoop = closeVal.Value<bool>();
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail($"Invalid JSON parameters: {ex.Message}");
            }

            if (pointsArray.Count < 2)
                return CommandResult.Fail("At least 2 points are required.");

            // Resolve level
            Level level = null;
            if (!string.IsNullOrEmpty(levelName))
            {
                level = new FilteredElementCollector(doc).OfClass(typeof(Level))
                    .Cast<Level>()
                    .FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
                if (level == null)
                    return CommandResult.Fail($"Level '{levelName}' was not found.");
            }

            // Resolve ViewPlan
            ViewPlan planView = null;
            if (viewId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(viewId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(viewId.Value));

                var vId = RevitCompat.ToElementId(viewId.Value);
                planView = doc.GetElement(vId) as ViewPlan;
                if (planView == null)
                    return CommandResult.Fail($"Specified view ID {viewId.Value} is not a valid Plan View.");
            }
            else
            {
                planView = app.ActiveUIDocument?.ActiveView as ViewPlan;
                if (planView == null && level != null)
                {
                    planView = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan))
                        .Cast<ViewPlan>()
                        .FirstOrDefault(v => v.GenLevel?.Id == level.Id && !v.IsTemplate);
                }
            }

            if (planView == null)
                return CommandResult.Fail("A valid Floor Plan or Area Plan view could not be determined. Please specify a valid view_id or level_name.");

            if (level == null)
            {
                level = planView.GenLevel;
            }

            if (level == null)
                return CommandResult.Fail("A valid Level could not be determined for the target plan view.");

            // Parse points
            var rawPts = new List<XYZ>();
            foreach (var ptToken in pointsArray)
            {
                double x = ptToken.Value<double>("x");
                double y = ptToken.Value<double>("y");
                double z = ptToken.Value<double>("z"); // will return 0 if missing

                // If Z is not explicitly provided, use the level's elevation
                if (ptToken["z"] == null)
                {
                    z = level.Elevation * 304.8;
                }

                if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y))
                {
                    return CommandResult.Fail("Points contain invalid non-finite coordinates.");
                }

                rawPts.Add(new XYZ(x / 304.8, y / 304.8, z / 304.8));
            }

            // Filter adjacent duplicates
            var uniquePts = new List<XYZ>();
            foreach (var p in rawPts)
            {
                if (uniquePts.Count == 0 || uniquePts.Last().DistanceTo(p) > 0.0001)
                {
                    uniquePts.Add(p);
                }
            }

            if (uniquePts.Count < 2)
                return CommandResult.Fail("Fewer than two unique points remain after removing adjacent duplicate points.");

            var curveArray = new CurveArray();
            double totalLengthM = 0;
            for (int i = 0; i < uniquePts.Count - 1; i++)
            {
                var p1 = uniquePts[i];
                var p2 = uniquePts[i + 1];
                var line = Line.CreateBound(p1, p2);
                curveArray.Append(line);
                totalLengthM += line.Length * 0.3048;
            }

            bool actuallyClosed = false;
            if (closeLoop && uniquePts.Count > 2)
            {
                var p1 = uniquePts.Last();
                var p2 = uniquePts.First();
                if (p1.DistanceTo(p2) > 0.0001)
                {
                    var line = Line.CreateBound(p1, p2);
                    curveArray.Append(line);
                    totalLengthM += line.Length * 0.3048;
                    actuallyClosed = true;
                }
                else
                {
                    actuallyClosed = true;
                }
            }

            if (curveArray.Size == 0)
                return CommandResult.Fail("Fewer than one valid segment was produced.");

            using (var tx = new Transaction(doc, "RvtMcp: Create Room Separator"))
            {
                tx.Start();
                try
                {
                    var planePoint = new XYZ(0, 0, level.Elevation);
                    var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, planePoint);
                    var sketchPlane = SketchPlane.Create(doc, plane);

                    var modelCurves = doc.Create.NewRoomBoundaryLines(sketchPlane, curveArray, planView);

                    var separatorIds = new List<long>();
                    foreach (ModelCurve mc in modelCurves)
                    {
                        separatorIds.Add(RevitCompat.GetId(mc.Id));
                    }

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail("Create room separator transaction did not commit. Status: " + status);

                    return CommandResult.Ok(new
                    {
                        created = true,
                        view = new { element_id = RevitCompat.GetId(planView.Id), name = planView.Name, view_type = planView.ViewType.ToString() },
                        level = new { element_id = RevitCompat.GetId(level.Id), name = level.Name },
                        curve_count = separatorIds.Count,
                        separator_ids = separatorIds,
                        length_m = Math.Round(totalLengthM, 4),
                        closed_loop = actuallyClosed
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to create room separation lines: {ex.Message}");
                }
            }
        }
    }
}
