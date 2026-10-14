using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class SetMaterialThermalAssetHandler : IRevitCommand
    {
        public string Name => "set_material_thermal_asset";
        public string Description => "Set or create a thermal property asset for a material";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""material_id"": { ""type"": ""integer"" },
    ""material_name"": { ""type"": ""string"" },
    ""asset_name"": { ""type"": ""string"" },
    ""conductivity_w_per_m_k"": { ""type"": ""number"" },
    ""specific_heat_j_per_kg_k"": { ""type"": ""number"" },
    ""emissivity"": { ""type"": ""number"" },
    ""permeability"": { ""type"": ""number"" },
    ""density_kg_per_m3"": { ""type"": ""number"" }
  }
}";

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

            var materialId = request.Value<long?>("material_id");
            var materialName = request.Value<string>("material_name") ?? "";

            Material mat = null;
            var allMats = new FilteredElementCollector(doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .ToList();

            if (materialId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(materialId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(materialId.Value));

                var elId = RevitCompat.ToElementId(materialId.Value);
                mat = doc.GetElement(elId) as Material;
                if (mat == null)
                    return CommandResult.Fail($"Material with ID {materialId} not found.");
            }
            else if (!string.IsNullOrEmpty(materialName))
            {
                var matching = allMats
                    .Where(m => m.Name.Equals(materialName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matching.Count == 0)
                    return CommandResult.Fail($"Material with name '{materialName}' not found.");
                if (matching.Count > 1)
                    return CommandResult.Fail($"Multiple materials found with name '{materialName}'. Please use material_id.");

                mat = matching[0];
            }
            else
            {
                return CommandResult.Fail("Either material_id or material_name must be supplied.");
            }

            var assetName = request.Value<string>("asset_name") ?? "";
            var conductivityWPerMK = request.Value<double?>("conductivity_w_per_m_k");
            var specificHeatJPerKgK = request.Value<double?>("specific_heat_j_per_kg_k");
            var emissivity = request.Value<double?>("emissivity");
            var permeability = request.Value<double?>("permeability");
            var densityKgPerM3 = request.Value<double?>("density_kg_per_m3");

            int attemptedCount = 0;
            if (conductivityWPerMK.HasValue) attemptedCount++;
            if (specificHeatJPerKgK.HasValue) attemptedCount++;
            if (emissivity.HasValue) attemptedCount++;
            if (permeability.HasValue) attemptedCount++;
            if (densityKgPerM3.HasValue) attemptedCount++;

            if (attemptedCount == 0)
                return CommandResult.Fail("No thermal fields were provided for update.");

            // Validations
            if (conductivityWPerMK.HasValue && conductivityWPerMK.Value < 0)
                return CommandResult.Fail("conductivity_w_per_m_k cannot be negative.");
            if (specificHeatJPerKgK.HasValue && specificHeatJPerKgK.Value < 0)
                return CommandResult.Fail("specific_heat_j_per_kg_k cannot be negative.");
            if (emissivity.HasValue && (emissivity.Value < 0.0 || emissivity.Value > 1.0))
                return CommandResult.Fail("emissivity must be between 0.0 and 1.0.");
            if (permeability.HasValue && permeability.Value < 0)
                return CommandResult.Fail("permeability cannot be negative.");
            if (densityKgPerM3.HasValue && densityKgPerM3.Value < 0)
                return CommandResult.Fail("density_kg_per_m3 cannot be negative.");

            if (string.IsNullOrWhiteSpace(assetName))
            {
                assetName = mat.Name + " Thermal Asset";
            }

            var fields = new Dictionary<string, object>();
            if (conductivityWPerMK.HasValue)
                fields["conductivity_w_per_m_k"] = new { status = "set", value = conductivityWPerMK.Value };
            if (specificHeatJPerKgK.HasValue)
                fields["specific_heat_j_per_kg_k"] = new { status = "set", value = specificHeatJPerKgK.Value };
            if (emissivity.HasValue)
                fields["emissivity"] = new { status = "set", value = emissivity.Value };
            if (permeability.HasValue)
                fields["permeability"] = new { status = "set", value = permeability.Value };
            if (densityKgPerM3.HasValue)
                fields["density_kg_per_m3"] = new { status = "set", value = densityKgPerM3.Value };

            using (var tx = new Transaction(doc, "RvtMcp: set material thermal asset"))
            {
                tx.Start();
                try
                {
                    var asset = new ThermalAsset(assetName, ThermalMaterialType.Solid);

                    if (conductivityWPerMK.HasValue)
                    {
                        double condInternal = ConvertToInternal(conductivityWPerMK.Value, UnitTypeId.WattsPerMeterKelvin);
                        asset.ThermalConductivity = condInternal;
                    }
                    if (specificHeatJPerKgK.HasValue)
                    {
                        asset.SpecificHeat = ConvertToInternal(specificHeatJPerKgK.Value, UnitTypeId.JoulesPerKilogramDegreeCelsius);
                    }
                    if (emissivity.HasValue)
                    {
                        asset.Emissivity = emissivity.Value;
                    }
                    if (permeability.HasValue)
                    {
                        asset.Permeability = permeability.Value;
                    }
                    if (densityKgPerM3.HasValue)
                    {
                        asset.Density = ConvertToInternal(densityKgPerM3.Value, UnitTypeId.KilogramsPerCubicMeter);
                    }

                    var pse = PropertySetElement.Create(doc, asset);
                    mat.ThermalAssetId = pse.Id;

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail($"Transaction did not commit. Status: {status}.");

                    return CommandResult.Ok(new
                    {
                        updated = true,
                        material_id = RevitCompat.GetId(mat.Id),
                        thermal_asset_id = RevitCompat.GetId(pse.Id),
                        asset_name = assetName,
                        fields,
                        error = (string)null
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Failed to set material thermal asset: " + ex.Message);
                }
            }
        }

        private static double ConvertToInternal(double value, ForgeTypeId typeId)
        {
            try
            {
                return UnitUtils.ConvertToInternalUnits(value, typeId);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Unsupported thermal asset unit '{typeId?.TypeId}': {ex.Message}", ex);
            }
        }
    }
}
