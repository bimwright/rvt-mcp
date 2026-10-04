using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateConduitHandler : IRevitCommand
    {
        private const double MmToFeet = 1.0 / 304.8;
        private const double FeetToMm = 304.8;

        public string Name => "create_conduit";

        public string Description =>
            "Create an electrical conduit between two points. Coordinates in mm. " +
            "Optionally specify conduit_type_id, level_id and diameter (mm). " +
            "If conduit_type_id is omitted, the first available ConduitType is used. " +
            "If level_id is omitted, the level nearest to start_z is used.";

        public string ParametersSchema => @"{""type"":""object"",""required"":[""start_x"",""start_y"",""start_z"",""end_x"",""end_y"",""end_z""],""properties"":{""start_x"":{""type"":""number""},""start_y"":{""type"":""number""},""start_z"":{""type"":""number""},""end_x"":{""type"":""number""},""end_y"":{""type"":""number""},""end_z"":{""type"":""number""},""conduit_type_id"":{""type"":""integer"",""description"":""ConduitType ElementId. If omitted, first available.""},""level_id"":{""type"":""integer"",""description"":""Level ElementId. If omitted, nearest to start_z.""},""diameter"":{""type"":""number"",""description"":""Conduit diameter in mm.""}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            JObject request;
            try
            {
                request = JObject.Parse(paramsJson);
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail("Invalid JSON parameters: " + ex.Message);
            }

            var startX = request.Value<double?>("start_x");
            var startY = request.Value<double?>("start_y");
            var startZ = request.Value<double?>("start_z");
            var endX = request.Value<double?>("end_x");
            var endY = request.Value<double?>("end_y");
            var endZ = request.Value<double?>("end_z");

            if (startX == null || startY == null || startZ == null ||
                endX == null || endY == null || endZ == null)
                return CommandResult.Fail("start_x, start_y, start_z, end_x, end_y, end_z are all required.");

            var conduitTypeId = request.Value<long?>("conduit_type_id");
            var levelId = request.Value<long?>("level_id");
            var diameter = request.Value<double?>("diameter");

            // Convert mm to feet.
            var startPt = new XYZ(startX.Value * MmToFeet, startY.Value * MmToFeet, startZ.Value * MmToFeet);
            var endPt = new XYZ(endX.Value * MmToFeet, endY.Value * MmToFeet, endZ.Value * MmToFeet);

            if (startPt.DistanceTo(endPt) < 1e-6)
                return CommandResult.Fail("Start and end points are coincident; conduit must have non-zero length.");

            // Resolve conduit type.
            ConduitType conduitType = null;
            if (conduitTypeId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(conduitTypeId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(conduitTypeId.Value));

                conduitType = doc.GetElement(RevitCompat.ToElementId(conduitTypeId.Value)) as ConduitType;
                if (conduitType == null)
                    return CommandResult.Fail($"Conduit type with ID {conduitTypeId.Value} not found.");
            }
            if (conduitType == null)
            {
                conduitType = new FilteredElementCollector(doc)
                    .OfClass(typeof(ConduitType))
                    .Cast<ConduitType>()
                    .FirstOrDefault();
            }
            if (conduitType == null)
                return CommandResult.Fail("No ConduitType found in the project. Load a conduit family first.");

            // Resolve level.
            Level level = null;
            if (levelId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(levelId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(levelId.Value));

                level = doc.GetElement(RevitCompat.ToElementId(levelId.Value)) as Level;
                if (level == null)
                    return CommandResult.Fail($"Level with ID {levelId.Value} not found.");
            }
            if (level == null)
            {
                // Nearest level to start_z (start elevation in feet).
                double targetElevation = startZ.Value * MmToFeet;
                level = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(lv => Math.Abs(lv.Elevation - targetElevation))
                    .FirstOrDefault();
            }
            if (level == null)
                return CommandResult.Fail("No level found in the project.");

            using (var tx = new Transaction(doc, "RvtMcp: create conduit"))
            {
                tx.Start();
                try
                {
                    var conduit = Conduit.Create(doc, conduitType.Id, startPt, endPt, level.Id);
                    if (conduit == null)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        return CommandResult.Fail("Conduit creation returned null.");
                    }

                    // Apply diameter if supplied (mm -> feet).
                    if (diameter.HasValue)
                    {
                        var diamParam = conduit.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
                        if (diamParam != null && !diamParam.IsReadOnly)
                            diamParam.Set(diameter.Value * MmToFeet);
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");

                    // Read back resulting length and diameter.
                    double lengthMm = 0;
                    var lengthParam = conduit.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
                    if (lengthParam != null)
                        lengthMm = Math.Round(lengthParam.AsDouble() * FeetToMm, 1);
                    else
                        lengthMm = Math.Round(startPt.DistanceTo(endPt) * FeetToMm, 1);

                    double? diameterMm = null;
                    var resultDiamParam = conduit.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
                    if (resultDiamParam != null)
                        diameterMm = Math.Round(resultDiamParam.AsDouble() * FeetToMm, 1);

                    return CommandResult.Ok(new
                    {
                        created = true,
                        conduit_id = RevitCompat.GetId(conduit.Id),
                        type = conduitType.Name,
                        level = level.Name,
                        length_mm = lengthMm,
                        diameter_mm = diameterMm,
                        error = (string)null
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Failed to create conduit: " + ex.Message);
                }
            }
        }
    }
}
