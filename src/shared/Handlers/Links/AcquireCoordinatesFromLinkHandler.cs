using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class AcquireCoordinatesFromLinkHandler : IRevitCommand
    {
        private const double FeetToMm = 304.8;

        public string Name => "acquire_coordinates_from_link";

        public string Description => "Acquire shared coordinates from a Revit link instance or linked CAD instance.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""link_instance_id""],
  ""properties"": {
    ""link_instance_id"": { ""type"": ""integer"" },
    ""confirm"": { ""type"": ""boolean"" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            long linkInstanceId = 0;
            bool confirm = false;

            try
            {
                var request = JObject.Parse(paramsJson);
                linkInstanceId = request.Value<long>("link_instance_id");
                if (request["confirm"] != null)
                    confirm = request.Value<bool>("confirm");
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            // STRICT GATE: must fail immediately if confirm = false before starting any transaction
            if (!confirm)
            {
                return CommandResult.Fail("Coordinate acquisition requires confirm = true. Warning: this operation will modify the shared coordinate system of the host project.");
            }

            if (!RevitCompat.CanRepresentElementId(linkInstanceId))
                return CommandResult.Fail("link_instance_id " + RevitCompat.ElementIdRangeError(linkInstanceId));

            var elementId = RevitCompat.ToElementId(linkInstanceId);
            var element = doc.GetElement(elementId);
            if (element == null)
                return CommandResult.Fail($"Element with ID {linkInstanceId} not found.");

            string kind = "";
            string name = element.Name;

            var rvtLink = element as RevitLinkInstance;
            var cadImport = element as ImportInstance;

            if (rvtLink != null)
            {
                kind = "revit_link";
            }
            else if (cadImport != null)
            {
                if (!cadImport.IsLinked)
                {
                    return CommandResult.Fail($"ImportInstance with ID {linkInstanceId} is a CAD import, not a CAD link. Shared coordinates can only be acquired from CAD links.");
                }
                kind = "cad_link";
            }
            else
            {
                return CommandResult.Fail($"Element with ID {linkInstanceId} is not a RevitLinkInstance or linked ImportInstance.");
            }

            // Capture state before mutation
            object beforeLocation = null;
            object beforeBp = null;
            try
            {
                var activeLocation = doc.ActiveProjectLocation;
                if (activeLocation != null)
                {
                    beforeLocation = new
                    {
                        name = activeLocation.Name,
                        id = RevitCompat.GetId(activeLocation.Id)
                    };
                }

                var bp = BasePoint.GetProjectBasePoint(doc);
                if (bp != null)
                {
                    beforeBp = ReadBasePointParams(bp);
                }
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Failed to capture coordinate system state before mutation: {ex.Message}");
            }

            var warnings = new List<string>();

            using (var tx = new Transaction(doc, "RvtMcp: Acquire Coordinates From Link"))
            {
                tx.Start();
                try
                {
                    doc.AcquireCoordinates(element.Id);
                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail("Acquire coordinates transaction did not commit. Status: " + status);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Revit API failed to acquire coordinates: {ex.Message}");
                }
            }

            // Capture state after mutation
            object afterLocation = null;
            object afterBp = null;
            try
            {
                var activeLocation = doc.ActiveProjectLocation;
                if (activeLocation != null)
                {
                    afterLocation = new
                    {
                        name = activeLocation.Name,
                        id = RevitCompat.GetId(activeLocation.Id)
                    };
                }

                var bp = BasePoint.GetProjectBasePoint(doc);
                if (bp != null)
                {
                    afterBp = ReadBasePointParams(bp);
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Coordinates were acquired but failed to capture final state: {ex.Message}");
            }

            return CommandResult.Ok(new
            {
                acquired = true,
                target = new
                {
                    element_id = linkInstanceId,
                    kind = kind,
                    name = name
                },
                project_location_before = beforeLocation,
                project_location_after = afterLocation,
                project_base_point_before = beforeBp,
                project_base_point_after = afterBp,
                warnings = warnings
            });
        }

        private static object ReadBasePointParams(BasePoint bp)
        {
            if (bp == null)
                return null;

            double ew = 0;
            double ns = 0;
            double elev = 0;
            double angle = 0;

            try
            {
                var pEw = bp.get_Parameter(BuiltInParameter.BASEPOINT_EASTWEST_PARAM);
                if (pEw != null) ew = pEw.AsDouble();

                var pNs = bp.get_Parameter(BuiltInParameter.BASEPOINT_NORTHSOUTH_PARAM);
                if (pNs != null) ns = pNs.AsDouble();

                var pElev = bp.get_Parameter(BuiltInParameter.BASEPOINT_ELEVATION_PARAM);
                if (pElev != null) elev = pElev.AsDouble();

                var pAngle = bp.get_Parameter(BuiltInParameter.BASEPOINT_ANGLETON_PARAM);
                if (pAngle != null) angle = pAngle.AsDouble();
            }
            catch { }

            return new
            {
                east_west_mm = Math.Round(ew * FeetToMm, 3),
                north_south_mm = Math.Round(ns * FeetToMm, 3),
                elevation_mm = Math.Round(elev * FeetToMm, 3),
                angle_to_true_north_deg = Math.Round(angle * (180.0 / Math.PI), 4)
            };
        }
    }
}
