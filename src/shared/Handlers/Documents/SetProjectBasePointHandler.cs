using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class SetProjectBasePointHandler : IRevitCommand
    {
        private const double FeetToMm = 304.8;

        public string Name => "set_project_base_point";

        public string Description => "Set project base point or survey point numeric parameters with dry run support.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""east_west"", ""north_south""],
  ""properties"": {
    ""east_west"": { ""type"": ""number"" },
    ""north_south"": { ""type"": ""number"" },
    ""elevation"": { ""type"": ""number"" },
    ""angle_to_true_north"": { ""type"": ""number"" },
    ""point_kind"": { ""type"": ""string"", ""enum"": [""project_base_point"", ""survey_point""] },
    ""dry_run"": { ""type"": ""boolean"" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            double eastWest = 0;
            double northSouth = 0;
            double elevation = 0;
            double angleToTrueNorth = 0;
            string pointKind = "project_base_point";
            bool dryRun = false;

            try
            {
                var request = JObject.Parse(paramsJson);
                if (request["east_west"] == null || request["north_south"] == null)
                    return CommandResult.Fail("east_west and north_south are required parameters.");

                eastWest = request.Value<double>("east_west");
                northSouth = request.Value<double>("north_south");

                if (request["elevation"] != null)
                    elevation = request.Value<double>("elevation");
                if (request["angle_to_true_north"] != null)
                    angleToTrueNorth = request.Value<double>("angle_to_true_north");
                if (request["point_kind"] != null)
                    pointKind = request.Value<string>("point_kind") ?? "project_base_point";
                if (request["dry_run"] != null)
                    dryRun = request.Value<bool>("dry_run");
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            // Input Validation
            if (double.IsNaN(eastWest) || double.IsInfinity(eastWest) ||
                double.IsNaN(northSouth) || double.IsInfinity(northSouth) ||
                double.IsNaN(elevation) || double.IsInfinity(elevation) ||
                double.IsNaN(angleToTrueNorth) || double.IsInfinity(angleToTrueNorth))
            {
                return CommandResult.Fail("All coordinates and angle values must be finite numbers.");
            }

            BasePoint point = null;
            bool isSurvey = pointKind.Equals("survey_point", StringComparison.OrdinalIgnoreCase);

            try
            {
                if (isSurvey)
                {
                    point = BasePoint.GetSurveyPoint(doc);
                }
                else if (pointKind.Equals("project_base_point", StringComparison.OrdinalIgnoreCase))
                {
                    point = BasePoint.GetProjectBasePoint(doc);
                }
                else
                {
                    return CommandResult.Fail($"Invalid point_kind '{pointKind}'. Supported: project_base_point, survey_point");
                }
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Failed to retrieve '{pointKind}': {ex.Message}");
            }

            if (point == null)
                return CommandResult.Fail($"Retrieved base point of kind '{pointKind}' is null.");

            var ewParam = point.get_Parameter(BuiltInParameter.BASEPOINT_EASTWEST_PARAM);
            var nsParam = point.get_Parameter(BuiltInParameter.BASEPOINT_NORTHSOUTH_PARAM);
            var elevParam = point.get_Parameter(BuiltInParameter.BASEPOINT_ELEVATION_PARAM);
            var angleParam = point.get_Parameter(BuiltInParameter.BASEPOINT_ANGLETON_PARAM);

            if (ewParam == null || nsParam == null || elevParam == null)
                return CommandResult.Fail("Core project coordinates parameters are not available on the selected point.");

            var warnings = new List<string>();

            // Capture before state
            double beforeEw = ewParam.AsDouble();
            double beforeNs = nsParam.AsDouble();
            double beforeElev = elevParam.AsDouble();
            double beforeAngle = angleParam?.AsDouble() ?? 0.0;

            var beforeDto = new
            {
                east_west_mm = Math.Round(beforeEw * FeetToMm, 3),
                north_south_mm = Math.Round(beforeNs * FeetToMm, 3),
                elevation_mm = Math.Round(beforeElev * FeetToMm, 3),
                angle_to_true_north_deg = Math.Round(beforeAngle * (180.0 / Math.PI), 4)
            };

            // Compute proposed after state
            double proposedEw = eastWest / FeetToMm;
            double proposedNs = northSouth / FeetToMm;
            double proposedElev = elevation / FeetToMm;
            double proposedAngle = angleToTrueNorth * (Math.PI / 180.0);

            var afterDto = new
            {
                east_west_mm = Math.Round(eastWest, 3),
                north_south_mm = Math.Round(northSouth, 3),
                elevation_mm = Math.Round(elevation, 3),
                angle_to_true_north_deg = isSurvey ? 0.0 : Math.Round(angleToTrueNorth, 4)
            };

            if (isSurvey && Math.Abs(angleToTrueNorth) > 1e-6)
            {
                warnings.Add("Survey point does not support angle parameter modification; angle change will be ignored.");
            }

            if (dryRun)
            {
                return CommandResult.Ok(new
                {
                    dry_run = true,
                    updated = false,
                    point_kind = pointKind,
                    element_id = RevitCompat.GetId(point.Id),
                    before = beforeDto,
                    after = afterDto,
                    warnings = warnings
                });
            }

            // Perform mutation inside transaction
            using (var tx = new Transaction(doc, "RvtMcp: Set Project Base Point"))
            {
                tx.Start();
                try
                {
                    if (ewParam.IsReadOnly || nsParam.IsReadOnly || elevParam.IsReadOnly || (angleParam != null && angleParam.IsReadOnly && !isSurvey))
                    {
                        tx.RollBack();
                        return CommandResult.Fail("One or more coordinates parameters are read-only in the current state.");
                    }

                    ewParam.Set(proposedEw);
                    nsParam.Set(proposedNs);
                    elevParam.Set(proposedElev);

                    if (!isSurvey && angleParam != null)
                    {
                        angleParam.Set(proposedAngle);
                    }

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail("Set project base point transaction did not commit. Status: " + status);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to update base point parameters: {ex.Message}");
                }
            }

            return CommandResult.Ok(new
            {
                dry_run = false,
                updated = true,
                point_kind = pointKind,
                element_id = RevitCompat.GetId(point.Id),
                before = beforeDto,
                after = afterDto,
                warnings = warnings
            });
        }
    }
}
