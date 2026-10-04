using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateDuctHandler : IRevitCommand
    {
        public string Name => "create_duct";

        public string Description =>
            "Create an HVAC duct between two points. Coordinates in mm. " +
            "Optionally specify duct_type_id, system_type_id, level_id, and a cross-section " +
            "(diameter for round ducts, OR width + height for rectangular ducts).";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""start_x"",""start_y"",""start_z"",""end_x"",""end_y"",""end_z""],
  ""properties"": {
    ""start_x"": {""type"":""number""}, ""start_y"": {""type"":""number""}, ""start_z"": {""type"":""number""},
    ""end_x"": {""type"":""number""}, ""end_y"": {""type"":""number""}, ""end_z"": {""type"":""number""},
    ""duct_type_id"": {""type"":""integer"",""description"":""DuctType ElementId. If omitted, first available DuctType is used.""},
    ""system_type_id"": {""type"":""integer"",""description"":""MEP system type ElementId. If omitted, first available MechanicalSystemType is used.""},
    ""level_id"": {""type"":""integer"",""description"":""Level ElementId. If omitted, nearest level to start_z.""},
    ""width"": {""type"":""number"",""description"":""Rectangular duct width in mm.""},
    ""height"": {""type"":""number"",""description"":""Rectangular duct height in mm.""},
    ""diameter"": {""type"":""number"",""description"":""Round duct diameter in mm. Use diameter OR width+height.""}
  }
}";

        private const double MmToFeet = 1.0 / 304.8;
        private const double FeetToMm = 304.8;

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
            catch (Newtonsoft.Json.JsonException ex)
            {
                return CommandResult.Fail("Invalid JSON parameters: " + ex.Message);
            }

            // Required coordinates (mm).
            var startX = request.Value<double>("start_x");
            var startY = request.Value<double>("start_y");
            var startZ = request.Value<double>("start_z");
            var endX = request.Value<double>("end_x");
            var endY = request.Value<double>("end_y");
            var endZ = request.Value<double>("end_z");

            var startPt = new XYZ(startX * MmToFeet, startY * MmToFeet, startZ * MmToFeet);
            var endPt = new XYZ(endX * MmToFeet, endY * MmToFeet, endZ * MmToFeet);

            if (startPt.DistanceTo(endPt) < 1e-6)
                return CommandResult.Fail("Start and end points are identical; cannot create a duct.");

            var ductTypeId = request.Value<long?>("duct_type_id");
            var systemTypeId = request.Value<long?>("system_type_id");
            var levelId = request.Value<long?>("level_id");
            var width = request.Value<double?>("width");
            var height = request.Value<double?>("height");
            var diameter = request.Value<double?>("diameter");

            // Resolve DuctType.
            DuctType ductType = null;
            if (ductTypeId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(ductTypeId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(ductTypeId.Value));
                ductType = doc.GetElement(RevitCompat.ToElementId(ductTypeId.Value)) as DuctType;
                if (ductType == null)
                    return CommandResult.Fail($"DuctType with ID {ductTypeId.Value} not found.");
            }
            else
            {
                ductType = new FilteredElementCollector(doc)
                    .OfClass(typeof(DuctType))
                    .Cast<DuctType>()
                    .FirstOrDefault();
                if (ductType == null)
                    return CommandResult.Fail("No DuctType found in the project.");
            }

            // Resolve MechanicalSystemType.
            MechanicalSystemType systemType = null;
            if (systemTypeId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(systemTypeId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(systemTypeId.Value));
                systemType = doc.GetElement(RevitCompat.ToElementId(systemTypeId.Value)) as MechanicalSystemType;
                if (systemType == null)
                    return CommandResult.Fail($"MechanicalSystemType with ID {systemTypeId.Value} not found.");
            }
            else
            {
                systemType = new FilteredElementCollector(doc)
                    .OfClass(typeof(MechanicalSystemType))
                    .Cast<MechanicalSystemType>()
                    .FirstOrDefault();
                if (systemType == null)
                    return CommandResult.Fail("No MechanicalSystemType found in the project.");
            }

            // Resolve Level.
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
                double startZFeet = startZ * MmToFeet;
                level = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(lv => Math.Abs(lv.Elevation - startZFeet))
                    .FirstOrDefault();
            }
            if (level == null)
                return CommandResult.Fail("No level found in the project.");

            using (var tx = new Transaction(doc, "RvtMcp: create duct"))
            {
                tx.Start();
                try
                {
                    var duct = Duct.Create(doc, systemType.Id, ductType.Id, level.Id, startPt, endPt);
                    if (duct == null)
                    {
                        tx.RollBack();
                        return CommandResult.Fail("Duct.Create returned null.");
                    }

                    // Apply cross-section if supplied.
                    if (diameter.HasValue)
                    {
                        SetParam(duct, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM, diameter.Value * MmToFeet);
                    }
                    else
                    {
                        if (width.HasValue)
                            SetParam(duct, BuiltInParameter.RBS_CURVE_WIDTH_PARAM, width.Value * MmToFeet);
                        if (height.HasValue)
                            SetParam(duct, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM, height.Value * MmToFeet);
                    }

                    double lengthMm = 0;
                    var lengthParam = duct.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
                    if (lengthParam != null && lengthParam.HasValue)
                        lengthMm = lengthParam.AsDouble() * FeetToMm;
                    else
                        lengthMm = startPt.DistanceTo(endPt) * FeetToMm;

                    var result = new
                    {
                        created = true,
                        duct_id = RevitCompat.GetId(duct.Id),
                        duct_type = ductType.Name,
                        system_type = systemType.Name,
                        level = level.Name,
                        length_mm = Math.Round(lengthMm, 1),
                        error = (string)null
                    };

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
                    return CommandResult.Ok(result);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Failed to create duct: " + ex.Message);
                }
            }
        }

        private static void SetParam(Element element, BuiltInParameter bip, double valueFeet)
        {
            var p = element.get_Parameter(bip);
            if (p != null && !p.IsReadOnly)
                p.Set(valueFeet);
        }
    }
}
