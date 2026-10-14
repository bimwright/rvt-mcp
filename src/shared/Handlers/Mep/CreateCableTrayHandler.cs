using System;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateCableTrayHandler : IRevitCommand
    {
        private const double MmToFeet = 1.0 / 304.8;
        private const double FeetToMm = 304.8;

        public string Name => "create_cable_tray";

        public string Description =>
            "Create an electrical cable tray between two points. Coordinates are in mm. " +
            "If cable_tray_type_id is omitted, the first available CableTrayType is used. " +
            "If level_id is omitted, the Level nearest to start_z is used. " +
            "Optional width/height (mm) override the tray cross-section.";

        public string ParametersSchema => @"{""type"":""object"",""required"":[""start_x"",""start_y"",""start_z"",""end_x"",""end_y"",""end_z""],""properties"":{""start_x"":{""type"":""number""},""start_y"":{""type"":""number""},""start_z"":{""type"":""number""},""end_x"":{""type"":""number""},""end_y"":{""type"":""number""},""end_z"":{""type"":""number""},""cable_tray_type_id"":{""type"":""integer"",""description"":""CableTrayType ElementId. If omitted, first available.""},""level_id"":{""type"":""integer"",""description"":""Level ElementId. If omitted, nearest to start_z.""},""width"":{""type"":""number"",""description"":""Tray width in mm.""},""height"":{""type"":""number"",""description"":""Tray height in mm.""}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            JObject request;
            try
            {
                request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            if (!TryReadRequiredNumber(request, "start_x", out var startX, out var err)) return CommandResult.Fail(err);
            if (!TryReadRequiredNumber(request, "start_y", out var startY, out err)) return CommandResult.Fail(err);
            if (!TryReadRequiredNumber(request, "start_z", out var startZ, out err)) return CommandResult.Fail(err);
            if (!TryReadRequiredNumber(request, "end_x", out var endX, out err)) return CommandResult.Fail(err);
            if (!TryReadRequiredNumber(request, "end_y", out var endY, out err)) return CommandResult.Fail(err);
            if (!TryReadRequiredNumber(request, "end_z", out var endZ, out err)) return CommandResult.Fail(err);

            var startPoint = new XYZ(startX * MmToFeet, startY * MmToFeet, startZ * MmToFeet);
            var endPoint = new XYZ(endX * MmToFeet, endY * MmToFeet, endZ * MmToFeet);

            if (startPoint.DistanceTo(endPoint) < 1e-6)
                return CommandResult.Fail("Start and end points are identical; cannot create a cable tray of zero length.");

            // Resolve cable tray type.
            var cableTrayTypeIdToken = request["cable_tray_type_id"];
            CableTrayType cableTrayType;
            if (cableTrayTypeIdToken != null && cableTrayTypeIdToken.Type != JTokenType.Null)
            {
                if (cableTrayTypeIdToken.Type != JTokenType.Integer)
                    return CommandResult.Fail("cable_tray_type_id must be an integer.");

                var ctTypeIdValue = cableTrayTypeIdToken.Value<long>();
                if (!RevitCompat.CanRepresentElementId(ctTypeIdValue))
                    return CommandResult.Fail("cable_tray_type_id " + RevitCompat.ElementIdRangeError(ctTypeIdValue));

                cableTrayType = doc.GetElement(RevitCompat.ToElementId(ctTypeIdValue)) as CableTrayType;
                if (cableTrayType == null)
                    return CommandResult.Fail("CableTrayType with ID " + ctTypeIdValue.ToString(CultureInfo.InvariantCulture) + " not found.");
            }
            else
            {
                cableTrayType = new FilteredElementCollector(doc)
                    .OfClass(typeof(CableTrayType))
                    .Cast<CableTrayType>()
                    .FirstOrDefault();
                if (cableTrayType == null)
                    return CommandResult.Fail("No CableTrayType found in the project. Load a cable tray family first.");
            }

            // Resolve level.
            var levelIdToken = request["level_id"];
            Level level;
            if (levelIdToken != null && levelIdToken.Type != JTokenType.Null)
            {
                if (levelIdToken.Type != JTokenType.Integer)
                    return CommandResult.Fail("level_id must be an integer.");

                var levelIdValue = levelIdToken.Value<long>();
                if (!RevitCompat.CanRepresentElementId(levelIdValue))
                    return CommandResult.Fail("level_id " + RevitCompat.ElementIdRangeError(levelIdValue));

                level = doc.GetElement(RevitCompat.ToElementId(levelIdValue)) as Level;
                if (level == null)
                    return CommandResult.Fail("Level with ID " + levelIdValue.ToString(CultureInfo.InvariantCulture) + " not found.");
            }
            else
            {
                var startZFeet = startZ * MmToFeet;
                level = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(lv => Math.Abs(lv.Elevation - startZFeet))
                    .FirstOrDefault();
                if (level == null)
                    return CommandResult.Fail("No level found in the project.");
            }

            // Optional cross-section overrides.
            double? widthMm = null;
            var widthToken = request["width"];
            if (widthToken != null && widthToken.Type != JTokenType.Null)
            {
                if (widthToken.Type != JTokenType.Integer && widthToken.Type != JTokenType.Float)
                    return CommandResult.Fail("width must be a number.");
                widthMm = widthToken.Value<double>();
                if (widthMm.Value <= 0)
                    return CommandResult.Fail("width must be greater than zero.");
            }

            double? heightMm = null;
            var heightToken = request["height"];
            if (heightToken != null && heightToken.Type != JTokenType.Null)
            {
                if (heightToken.Type != JTokenType.Integer && heightToken.Type != JTokenType.Float)
                    return CommandResult.Fail("height must be a number.");
                heightMm = heightToken.Value<double>();
                if (heightMm.Value <= 0)
                    return CommandResult.Fail("height must be greater than zero.");
            }

            using (var tx = new Transaction(doc, "RvtMcp: create cable tray"))
            {
                tx.Start();
                try
                {
                    var cableTray = CableTray.Create(doc, cableTrayType.Id, startPoint, endPoint, level.Id);
                    if (cableTray == null)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        return CommandResult.Fail("Revit returned no cable tray from CableTray.Create.");
                    }

                    if (widthMm.HasValue)
                    {
                        var widthParam = cableTray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
                        if (widthParam != null && !widthParam.IsReadOnly)
                            widthParam.Set(widthMm.Value * MmToFeet);
                    }

                    if (heightMm.HasValue)
                    {
                        var heightParam = cableTray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
                        if (heightParam != null && !heightParam.IsReadOnly)
                            heightParam.Set(heightMm.Value * MmToFeet);
                    }

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the cable tray. Transaction status: " + status);

                    return CommandResult.Ok(new
                    {
                        created = true,
                        cable_tray_id = RevitCompat.GetId(cableTray.Id),
                        type = SafeName(cableTrayType),
                        level = SafeName(level),
                        length_mm = Math.Round(GetLengthMm(cableTray, startPoint, endPoint), 1),
                        width_mm = ReadDimensionMm(cableTray, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM, widthMm),
                        height_mm = ReadDimensionMm(cableTray, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM, heightMm),
                        error = (string)null
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Failed to create cable tray: " + ex.Message);
                }
            }
        }

        private static bool TryReadRequiredNumber(JObject request, string propertyName, out double value, out string error)
        {
            value = 0;
            error = null;

            var token = request[propertyName];
            if (token == null || token.Type == JTokenType.Null)
            {
                error = propertyName + " is required.";
                return false;
            }

            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
            {
                error = propertyName + " must be a number.";
                return false;
            }

            value = token.Value<double>();
            return true;
        }

        private static double GetLengthMm(CableTray cableTray, XYZ startPoint, XYZ endPoint)
        {
            try
            {
                var lengthParam = cableTray.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
                if (lengthParam != null && lengthParam.StorageType == StorageType.Double)
                    return lengthParam.AsDouble() * FeetToMm;
            }
            catch { }

            try
            {
                if (cableTray.Location is LocationCurve locationCurve && locationCurve.Curve != null)
                    return locationCurve.Curve.Length * FeetToMm;
            }
            catch { }

            return startPoint.DistanceTo(endPoint) * FeetToMm;
        }

        private static double? ReadDimensionMm(CableTray cableTray, BuiltInParameter parameter, double? requestedMm)
        {
            try
            {
                var param = cableTray.get_Parameter(parameter);
                if (param != null && param.StorageType == StorageType.Double)
                    return Math.Round(param.AsDouble() * FeetToMm, 1);
            }
            catch { }

            return requestedMm.HasValue ? Math.Round(requestedMm.Value, 1) : (double?)null;
        }

        private static string SafeName(Element element)
        {
            if (element == null)
                return null;

            try
            {
                return element.Name;
            }
            catch
            {
                return null;
            }
        }
    }
}
