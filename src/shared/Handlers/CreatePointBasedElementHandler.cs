using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreatePointBasedElementHandler : IRevitCommand
    {
        public string Name => "create_point_based_element";
        public string Description => "Create a OneLevelBased or OneLevelBasedHosted family at model coordinates in mm. " +
            "Hosted families require host_id; no host is inferred. Non-hosted families ignore host_id with a warning. " +
            "Work-plane/face and other placement types are unsupported. Host and actual position are verified within 1 mm before commit.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""typeId"":{""type"":""integer""},""x"":{""type"":""number""},""y"":{""type"":""number""},""z"":{""type"":""number""},""level"":{""type"":""string""},""host_id"":{""type"":""integer"",""description"":""Required for OneLevelBasedHosted families; ignored with a warning for OneLevelBased families. Local host ElementId; no automatic host selection.""}},""required"":[""typeId"",""x"",""y""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            try { return ExecuteCore(app, paramsJson); }
            catch (Exception ex) { return CommandResult.Fail($"Failed to create instance: {ex.Message}"); }
        }

        private CommandResult ExecuteCore(UIApplication app, string paramsJson)
        {
            var doc = app?.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var typeId = request.Value<long?>("typeId");
            if (!typeId.HasValue) return CommandResult.Fail("typeId is required.");
            var x = Coordinate(request, "x");
            var y = Coordinate(request, "y");
            var z = request["z"] == null || request["z"].Type == JTokenType.Null ? 0 : Coordinate(request, "z");
            var levelName = request.Value<string>("level");
            var hostId = request.Value<long?>("host_id");

            var point = new XYZ(x / 304.8, y / 304.8, z / 304.8);

            var familySymbol = doc.GetElement(RevitCompat.ToElementId(typeId.Value)) as FamilySymbol;
            if (familySymbol == null)
                return CommandResult.Fail($"Family type with ID {typeId} not found. Use get_available_family_types to find valid IDs.");

            // Metadata failures must not fall through to the unhosted overload.
            var placementType = familySymbol.Family.FamilyPlacementType;
            Element host = null;
            string warning = null;
            switch (placementType)
            {
                case FamilyPlacementType.OneLevelBased:
                    if (hostId.HasValue) warning = "host_id ignored: this OneLevelBased family does not require a host.";
                    break;
                case FamilyPlacementType.OneLevelBasedHosted:
                    if (!hostId.HasValue)
                        return CommandResult.Fail("FamilyPlacementType=OneLevelBasedHosted requires host_id. Select the intended host element; no host was inferred and nothing was created.");
                    host = doc.GetElement(RevitCompat.ToElementId(hostId.Value));
                    if (host == null) return CommandResult.Fail($"Host element {hostId.Value} was not found in the active document.");
                    // Conventional hosted doors/windows require a wall, not an arbitrary element or linked model.
                    var categoryId = familySymbol.Category == null ? (long?)null : RevitCompat.GetId(familySymbol.Category.Id);
                    if ((categoryId == (long)BuiltInCategory.OST_Doors || categoryId == (long)BuiltInCategory.OST_Windows) && !(host is Wall))
                        return CommandResult.Fail("This hosted door/window requires host_id of a wall in the active document.");
                    break;
                case FamilyPlacementType.WorkPlaneBased:
                    return CommandResult.Fail("FamilyPlacementType=WorkPlaneBased requires an explicit face/work plane and placement orientation. This tool does not support that placement; host_id alone is insufficient. Nothing was created.");
                default:
                    return CommandResult.Fail($"FamilyPlacementType={placementType} is not supported by this point-placement tool. Nothing was created.");
            }

            // Find level
            Level level = null;
            if (!string.IsNullOrEmpty(levelName))
            {
                level = new FilteredElementCollector(doc).OfClass(typeof(Level))
                    .Cast<Level>()
                    .FirstOrDefault(lv => lv.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
                if (level == null) return CommandResult.Fail($"Level '{levelName}' was not found.");
            }
            if (level == null)
            {
                level = new FilteredElementCollector(doc).OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(lv => lv.Elevation)
                    .FirstOrDefault();
            }
            if (level == null)
                return CommandResult.Fail("No level found in the project.");

            using (var tx = new Transaction(doc, "MCP: Create family instance"))
            {
                tx.Start();
                try
                {
                    if (!familySymbol.IsActive)
                    {
                        familySymbol.Activate();
                        doc.Regenerate();
                    }

                    var instance = host == null
                        ? doc.Create.NewFamilyInstance(point, familySymbol, level, StructuralType.NonStructural)
                        : doc.Create.NewFamilyInstance(point, familySymbol, host, level, StructuralType.NonStructural);
                    if (instance == null) throw new InvalidOperationException("NewFamilyInstance returned null.");
                    doc.Regenerate();
                    var actualHost = instance.Host;
                    if (host != null && (actualHost == null || actualHost.Id != host.Id))
                        throw new InvalidOperationException("Revit did not assign the requested host. Placement was rolled back.");
                    var actualPoint = (instance.Location as LocationPoint)?.Point;
                    if (actualPoint == null || actualPoint.DistanceTo(point) > 1.0 / 304.8)
                        throw new InvalidOperationException("Revit did not place the instance within 1 mm of the requested model coordinates. Check the host, level and family placement constraints. Placement was rolled back.");

                    var result = new
                    {
                        elementId = RevitCompat.GetId(instance.Id),
                        familyName = familySymbol.FamilyName,
                        typeName = familySymbol.Name,
                        category = instance.Category?.Name,
                        placement_type = placementType.ToString(),
                        host_id = actualHost == null ? (long?)null : RevitCompat.GetId(actualHost.Id),
                        location_mm = new { x = actualPoint.X * 304.8, y = actualPoint.Y * 304.8, z = actualPoint.Z * 304.8 },
                        warning
                    };
                    if (tx.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Revit did not commit family placement.");
                    return CommandResult.Ok(result);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to create instance: {ex.Message}");
                }
            }
        }

        private static double Coordinate(JObject request, string name)
        {
            var value = request.Value<double?>(name);
            if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
                throw new ArgumentException($"{name} must be a finite number in mm.");
            return value.Value;
        }
    }
}
