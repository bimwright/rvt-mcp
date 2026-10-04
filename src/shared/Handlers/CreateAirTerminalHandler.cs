using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateAirTerminalHandler : IRevitCommand
    {
        public string Name => "create_air_terminal";

        public string Description =>
            "Place an air terminal (diffuser/grille) family instance at a point. " +
            "Coordinates in mm. Provide type_id (an Air Terminal FamilySymbol ElementId). " +
            "Optionally specify level_id (nearest to z if omitted) and host_id " +
            "(a duct or ceiling ElementId) for hosted placement.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""type_id"",""x"",""y"",""z""],
  ""properties"": {
    ""type_id"": {""type"":""integer"",""description"":""FamilySymbol ElementId for an Air Terminal family type.""},
    ""x"": {""type"":""number""},
    ""y"": {""type"":""number""},
    ""z"": {""type"":""number""},
    ""level_id"": {""type"":""integer"",""description"":""Level ElementId. If omitted, nearest to z.""},
    ""host_id"": {""type"":""integer"",""description"":""Optional host element (duct/ceiling) ElementId for hosted placement.""}
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

            var typeId = request.Value<long>("type_id");
            var x = request.Value<double>("x");
            var y = request.Value<double>("y");
            var z = request.Value<double>("z");
            var levelId = request.Value<long?>("level_id");
            var hostId = request.Value<long?>("host_id");

            var location = new XYZ(x * MmToFeet, y * MmToFeet, z * MmToFeet);

            // Resolve FamilySymbol.
            if (!RevitCompat.CanRepresentElementId(typeId))
                return CommandResult.Fail(RevitCompat.ElementIdRangeError(typeId));
            var symbol = doc.GetElement(RevitCompat.ToElementId(typeId)) as FamilySymbol;
            if (symbol == null)
                return CommandResult.Fail($"Family type with ID {typeId} not found. Use get_available_family_types to find valid IDs.");

            // Verify the symbol's category is Air Terminals.
            var category = symbol.Category;
            if (category == null || (BuiltInCategory)RevitCompat.GetId(category.Id) != BuiltInCategory.OST_DuctTerminal)
            {
                return CommandResult.Ok(new
                {
                    created = false,
                    instance_id = (long?)null,
                    type_name = symbol.Name,
                    family_name = symbol.FamilyName,
                    level = (string)null,
                    hosted = false,
                    error = $"Family type {typeId} is not an Air Terminal (category OST_DuctTerminal). " +
                            $"Its category is '{category?.Name ?? "unknown"}'."
                });
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
                        error = "This air terminal family (FamilyPlacementType=" + placementType + ") requires a host element. Provide host_id (a duct or ceiling)."
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

            // Resolve Level (explicit or nearest to z).
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

            // Resolve optional host element.
            Element host = null;
            if (hostId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(hostId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(hostId.Value));
                host = doc.GetElement(RevitCompat.ToElementId(hostId.Value));
                if (host == null)
                    return CommandResult.Fail($"Host element with ID {hostId.Value} not found.");
            }

            using (var tx = new Transaction(doc, "RvtMcp: create air terminal"))
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
                            location, symbol, host, level, StructuralType.NonStructural);
                        hosted = true;
                    }
                    else
                    {
                        instance = doc.Create.NewFamilyInstance(
                            location, symbol, level, StructuralType.NonStructural);
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
                    return CommandResult.Fail("Failed to create air terminal: " + ex.Message);
                }
            }
        }
    }
}
