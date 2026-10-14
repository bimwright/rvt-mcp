using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class SetMaterialStructuralAssetHandler : IRevitCommand
    {
        public string Name => "set_material_structural_asset";
        public string Description => "Set or create a structural physical property asset for a material";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""material_id"": { ""type"": ""integer"" },
    ""material_name"": { ""type"": ""string"" },
    ""asset_name"": { ""type"": ""string"" },
    ""structural_class"": { ""type"": ""string"", ""default"": ""generic"" },
    ""density_kg_per_m3"": { ""type"": ""number"" },
    ""young_modulus_mpa"": { ""type"": ""number"" },
    ""poisson_ratio"": { ""type"": ""number"" },
    ""shear_modulus_mpa"": { ""type"": ""number"" }
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
            var structuralClassStr = request.Value<string>("structural_class") ?? "generic";
            var densityKgPerM3 = request.Value<double?>("density_kg_per_m3");
            var youngModulusMpa = request.Value<double?>("young_modulus_mpa");
            var poissonRatio = request.Value<double?>("poisson_ratio");
            var shearModulusMpa = request.Value<double?>("shear_modulus_mpa");

            int attemptedCount = 0;
            if (densityKgPerM3.HasValue) attemptedCount++;
            if (youngModulusMpa.HasValue) attemptedCount++;
            if (poissonRatio.HasValue) attemptedCount++;
            if (shearModulusMpa.HasValue) attemptedCount++;

            if (attemptedCount == 0)
                return CommandResult.Fail("No structural fields were provided for update.");

            // Validations
            if (densityKgPerM3.HasValue && densityKgPerM3.Value < 0)
                return CommandResult.Fail("density_kg_per_m3 cannot be negative.");
            if (youngModulusMpa.HasValue && youngModulusMpa.Value < 0)
                return CommandResult.Fail("young_modulus_mpa cannot be negative.");
            if (poissonRatio.HasValue && (poissonRatio.Value < 0.0 || poissonRatio.Value > 0.5))
                return CommandResult.Fail("poisson_ratio must be between 0.0 and 0.5.");
            if (shearModulusMpa.HasValue && shearModulusMpa.Value < 0)
                return CommandResult.Fail("shear_modulus_mpa cannot be negative.");

            if (string.IsNullOrWhiteSpace(assetName))
            {
                assetName = mat.Name + " Structural Asset";
            }

            StructuralAssetClass structuralClass = StructuralAssetClass.Generic;
            if (structuralClassStr.Equals("concrete", StringComparison.OrdinalIgnoreCase))
                structuralClass = StructuralAssetClass.Concrete;
            else if (structuralClassStr.Equals("metal", StringComparison.OrdinalIgnoreCase))
                structuralClass = StructuralAssetClass.Metal;
            else if (structuralClassStr.Equals("wood", StringComparison.OrdinalIgnoreCase))
                structuralClass = StructuralAssetClass.Wood;
            else if (structuralClassStr.Equals("liquid", StringComparison.OrdinalIgnoreCase))
                structuralClass = StructuralAssetClass.Liquid;
            else if (structuralClassStr.Equals("gas", StringComparison.OrdinalIgnoreCase))
                structuralClass = StructuralAssetClass.Gas;

            var fields = new Dictionary<string, object>();
            if (densityKgPerM3.HasValue)
                fields["density_kg_per_m3"] = new { status = "set", value = densityKgPerM3.Value };
            if (youngModulusMpa.HasValue)
                fields["young_modulus_mpa"] = new { status = "set", value = youngModulusMpa.Value };
            if (poissonRatio.HasValue)
                fields["poisson_ratio"] = new { status = "set", value = poissonRatio.Value };
            if (shearModulusMpa.HasValue)
                fields["shear_modulus_mpa"] = new { status = "set", value = shearModulusMpa.Value };

            using (var tx = new Transaction(doc, "RvtMcp: set material structural asset"))
            {
                tx.Start();
                try
                {
                    var asset = new StructuralAsset(assetName, structuralClass);
                    asset.Behavior = StructuralBehavior.Isotropic;

                    if (densityKgPerM3.HasValue)
                        asset.Density = ConvertToInternal(densityKgPerM3.Value, UnitTypeId.KilogramsPerCubicMeter);
                    if (youngModulusMpa.HasValue)
                    {
                        double youngInternal = ConvertToInternal(youngModulusMpa.Value, UnitTypeId.Megapascals);
                        asset.YoungModulus = new XYZ(youngInternal, youngInternal, youngInternal);
                    }
                    if (poissonRatio.HasValue)
                    {
                        double poissonVal = poissonRatio.Value;
                        asset.PoissonRatio = new XYZ(poissonVal, poissonVal, poissonVal);
                    }
                    if (shearModulusMpa.HasValue)
                    {
                        double shearInternal = ConvertToInternal(shearModulusMpa.Value, UnitTypeId.Megapascals);
                        asset.ShearModulus = new XYZ(shearInternal, shearInternal, shearInternal);
                    }

                    var pse = PropertySetElement.Create(doc, asset);
                    mat.StructuralAssetId = pse.Id;

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail($"Transaction did not commit. Status: {status}.");

                    return CommandResult.Ok(new
                    {
                        updated = true,
                        material_id = RevitCompat.GetId(mat.Id),
                        structural_asset_id = RevitCompat.GetId(pse.Id),
                        asset_name = assetName,
                        fields,
                        error = (string)null
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Failed to set material structural asset: " + ex.Message);
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
                throw new InvalidOperationException($"Unsupported structural asset unit '{typeId?.TypeId}': {ex.Message}", ex);
            }
        }
    }
}
