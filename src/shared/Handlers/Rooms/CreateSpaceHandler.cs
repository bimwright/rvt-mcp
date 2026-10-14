using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateSpaceHandler : IRevitCommand
    {
        public string Name => "create_space";
        public string Description => "Create an MEP space at a point";
        public string ParametersSchema => @"{
            ""type"": ""object"",
            ""required"": [""x"", ""y""],
            ""properties"": {
                ""x"": { ""type"": ""number"" },
                ""y"": { ""type"": ""number"" },
                ""level_name"": { ""type"": ""string"" },
                ""phase_name"": { ""type"": ""string"" },
                ""name"": { ""type"": ""string"" },
                ""number"": { ""type"": ""string"" }
            }
        }";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            double x, y;
            string levelName = "";
            string phaseName = "";
            string spaceName = "";
            string spaceNumber = "";

            try
            {
                var request = JObject.Parse(paramsJson);
                if (!request.TryGetValue("x", out var xVal) || !request.TryGetValue("y", out var yVal))
                    return CommandResult.Fail("x and y are required.");

                x = xVal.Value<double>();
                y = yVal.Value<double>();

                if (request.TryGetValue("level_name", out var lvVal))
                    levelName = lvVal.Value<string>() ?? "";
                if (request.TryGetValue("phase_name", out var phVal))
                    phaseName = phVal.Value<string>() ?? "";
                if (request.TryGetValue("name", out var nVal))
                    spaceName = nVal.Value<string>() ?? "";
                if (request.TryGetValue("number", out var numVal))
                    spaceNumber = numVal.Value<string>() ?? "";
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail($"Invalid JSON parameters: {ex.Message}");
            }

            // Resolve Level
            Level level = null;
            if (!string.IsNullOrEmpty(levelName))
            {
                level = new FilteredElementCollector(doc).OfClass(typeof(Level))
                    .Cast<Level>()
                    .FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
                if (level == null)
                    return CommandResult.Fail($"Level '{levelName}' was not found.");
            }
            else
            {
                level = (app.ActiveUIDocument?.ActiveView as ViewPlan)?.GenLevel ??
                        new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault();
            }

            if (level == null)
                return CommandResult.Fail("A valid Level could not be determined.");

            // Resolve Phase
            Phase phase = null;
            if (!string.IsNullOrEmpty(phaseName))
            {
                phase = new FilteredElementCollector(doc).OfClass(typeof(Phase))
                    .Cast<Phase>()
                    .FirstOrDefault(p => p.Name.Equals(phaseName, StringComparison.OrdinalIgnoreCase));
                if (phase == null)
                    return CommandResult.Fail($"Phase '{phaseName}' was not found.");
            }
            else
            {
                var activeView = app.ActiveUIDocument?.ActiveView;
                if (activeView != null)
                {
                    var phaseParam = activeView.get_Parameter(BuiltInParameter.VIEW_PHASE);
                    if (phaseParam != null && phaseParam.HasValue)
                    {
                        phase = doc.GetElement(phaseParam.AsElementId()) as Phase;
                    }
                }
                if (phase == null)
                {
                    phase = new FilteredElementCollector(doc).OfClass(typeof(Phase))
                        .Cast<Phase>()
                        .LastOrDefault();
                }
            }

            using (var tx = new Transaction(doc, "RvtMcp: Create Space"))
            {
                tx.Start();
                try
                {
                    var point = new UV(x / 304.8, y / 304.8);
                    Space space = null;

                    if (phase != null)
                    {
                        space = doc.Create.NewSpace(level, phase, point);
                    }
                    else
                    {
                        space = doc.Create.NewSpace(level, point);
                    }

                    if (space == null)
                    {
                        tx.RollBack();
                        return CommandResult.Fail("Revit API doc.Create.NewSpace returned null. Ensure the project is an MEP-enabled project.");
                    }

                    if (!string.IsNullOrEmpty(spaceName))
                    {
                        var param = space.get_Parameter(BuiltInParameter.ROOM_NAME) ?? space.LookupParameter("Name");
                        if (param != null && !param.IsReadOnly) param.Set(spaceName);
                        else space.Name = spaceName;
                    }

                    if (!string.IsNullOrEmpty(spaceNumber))
                    {
                        var param = space.get_Parameter(BuiltInParameter.ROOM_NUMBER) ?? space.LookupParameter("Number");
                        if (param != null && !param.IsReadOnly) param.Set(spaceNumber);
                        else space.Number = spaceNumber;
                    }

                    var commitStatus = tx.Commit();
                    if (commitStatus != TransactionStatus.Committed)
                        return CommandResult.Fail("Create space transaction did not commit. Status: " + commitStatus);

                    string status = "unplaced";
                    if (space.Location != null)
                    {
                        var options = new SpatialElementBoundaryOptions();
                        var boundary = space.GetBoundarySegments(options);
                        if (space.Area > 0.0001 && boundary != null && boundary.Count > 0)
                        {
                            status = "placed";
                        }
                        else
                        {
                            status = "not_enclosed";
                        }
                    }

                    var locPoint = space.Location as LocationPoint;
                    object locData = null;
                    if (locPoint != null)
                    {
                        var pt = locPoint.Point;
                        locData = new { x_mm = Math.Round(pt.X * 304.8, 2), y_mm = Math.Round(pt.Y * 304.8, 2), z_mm = Math.Round(pt.Z * 304.8, 2) };
                    }

                    return CommandResult.Ok(new
                    {
                        space = new
                        {
                            element_id = RevitCompat.GetId(space.Id),
                            name = space.Name,
                            number = space.Number,
                            status = status,
                            area_m2 = Math.Round(space.Area * 0.09290304, 4),
                            volume_m3 = Math.Round(space.Volume * 0.0283168, 4)
                        },
                        level = new { element_id = RevitCompat.GetId(level.Id), name = level.Name },
                        phase = phase != null ? new { element_id = RevitCompat.GetId(phase.Id), name = phase.Name } : null,
                        location = locData
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to create space: {ex.Message}");
                }
            }
        }
    }
}
