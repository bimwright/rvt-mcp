using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateLightingFixtureHandler : IRevitCommand
    {
        public string Name => "create_lighting_fixture";

        public string Description =>
            "Place a lighting fixture family instance at a point. Coordinates in mm. " +
            "Provide type_id (a Lighting Fixture FamilySymbol ElementId) and x/y/z. " +
            "Optionally specify level_id (defaults to nearest level to z) and host_id " +
            "(a ceiling element ElementId) for hosted placement.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""type_id"",""x"",""y"",""z""],
  ""properties"": {
    ""type_id"": {""type"":""integer"",""description"":""FamilySymbol ElementId for a Lighting Fixture family type.""},
    ""x"": {""type"":""number""}, ""y"": {""type"":""number""}, ""z"": {""type"":""number""},
    ""level_id"": {""type"":""integer"",""description"":""Level ElementId. If omitted, nearest to z.""},
    ""host_id"": {""type"":""integer"",""description"":""Optional host element (ceiling) ElementId for hosted placement.""}
  }
}";

        private const double MmToFeet = 1.0 / 304.8;

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

            var typeId = request.Value<long?>("type_id");
            if (!typeId.HasValue)
                return CommandResult.Fail("Parameter 'type_id' is required.");

            var x = request.Value<double>("x");
            var y = request.Value<double>("y");
            var z = request.Value<double>("z");
            var levelId = request.Value<long?>("level_id");
            var hostId = request.Value<long?>("host_id");

            var point = new XYZ(x * MmToFeet, y * MmToFeet, z * MmToFeet);

            // Resolve FamilySymbol.
            if (!RevitCompat.CanRepresentElementId(typeId.Value))
                return CommandResult.Fail(RevitCompat.ElementIdRangeError(typeId.Value));
            var symbol = doc.GetElement(RevitCompat.ToElementId(typeId.Value)) as FamilySymbol;
            if (symbol == null)
                return CommandResult.Fail(
                    $"FamilySymbol with ID {typeId.Value} not found. Use get_available_family_types to find valid IDs.");

            // Verify category is Lighting Fixtures.
            if (symbol.Category == null ||
                symbol.Category.Id == null ||
                RevitCompat.GetId(symbol.Category.Id) != (long)BuiltInCategory.OST_LightingFixtures)
            {
                return CommandResult.Fail(
                    $"FamilySymbol with ID {typeId.Value} is not a Lighting Fixture " +
                    $"(category: {symbol.Category?.Name ?? "none"}).");
            }

            string placementWarning = null;
            try
            {
                var placementType = symbol.Family.FamilyPlacementType;
                if ((placementType == FamilyPlacementType.OneLevelBasedHosted ||
                     placementType == FamilyPlacementType.WorkPlaneBased) &&
                    !hostId.HasValue)
                {
                    return CommandResult.Ok(new
                    {
                        created = false,
                        instance_id = (long?)null,
                        type_name = symbol.Name,
                        family_name = symbol.FamilyName,
                        level = (string)null,
                        hosted = false,
                        error = "This lighting fixture family (FamilyPlacementType=" + placementType + ") requires a host element. Provide host_id (a ceiling)."
                    });
                }

                if (hostId.HasValue && placementType == FamilyPlacementType.OneLevelBased)
                {
                    hostId = null;
                    placementWarning = "host_id ignored — family is not hosted";
                }
            }
            catch
            {
                // Some family metadata can throw; keep the previous permissive behavior.
            }

            // Resolve host element (optional).
            Element host = null;
            if (hostId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(hostId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(hostId.Value));
                host = doc.GetElement(RevitCompat.ToElementId(hostId.Value));
                if (host == null)
                    return CommandResult.Fail($"Host element with ID {hostId.Value} not found.");
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
                double zFeet = z * MmToFeet;
                level = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(lv => Math.Abs(lv.Elevation - zFeet))
                    .FirstOrDefault();
            }
            if (level == null)
                return CommandResult.Fail("No level found in the project.");

            using (var tx = new Transaction(doc, "RvtMcp: create lighting fixture"))
            {
                tx.Start();
                try
                {
                    if (!symbol.IsActive)
                    {
                        symbol.Activate();
                        doc.Regenerate();
                    }

                    FamilyInstance instance;
                    bool hosted;
                    if (host != null)
                    {
                        instance = doc.Create.NewFamilyInstance(
                            point, symbol, host, level, StructuralType.NonStructural);
                        hosted = true;
                    }
                    else
                    {
                        instance = doc.Create.NewFamilyInstance(
                            point, symbol, level, StructuralType.NonStructural);
                        hosted = false;
                    }

                    if (instance == null)
                    {
                        tx.RollBack();
                        return CommandResult.Fail("NewFamilyInstance returned null.");
                    }

                    var result = new
                    {
                        created = true,
                        instance_id = RevitCompat.GetId(instance.Id),
                        type_name = symbol.Name,
                        family_name = symbol.FamilyName,
                        level = level.Name,
                        hosted = hosted,
                        warning = placementWarning,
                        error = (string)null
                    };

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
                    return CommandResult.Ok(result);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Failed to create lighting fixture: " + ex.Message);
                }
            }
        }
    }
}
