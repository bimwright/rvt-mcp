using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class GetMaterialPropertiesHandler : IRevitCommand
    {
        public string Name => "get_material_properties";
        public string Description => "Get detailed properties of a material by ID or name";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""material_id"": { ""type"": ""integer"" },
    ""material_name"": { ""type"": ""string"" },
    ""include_assets"": { ""type"": ""boolean"", ""default"": true },
    ""include_parameters"": { ""type"": ""boolean"", ""default"": true }
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
            var includeAssets = request.Value<bool?>("include_assets") ?? true;
            var includeParameters = request.Value<bool?>("include_parameters") ?? true;

            Material mat = null;

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
                var allMats = new FilteredElementCollector(doc)
                    .OfClass(typeof(Material))
                    .Cast<Material>()
                    .Where(m => m.Name.Equals(materialName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (allMats.Count == 0)
                    return CommandResult.Fail($"Material with name '{materialName}' not found.");
                if (allMats.Count > 1)
                    return CommandResult.Fail($"Multiple materials found with name '{materialName}'. Please use material_id.");

                mat = allMats[0];
            }
            else
            {
                return CommandResult.Fail("Either material_id or material_name must be supplied.");
            }

            long resolvedId = RevitCompat.GetId(mat.Id);

            object colorObj = null;
            if (mat.Color != null)
            {
                colorObj = new { red = (int)mat.Color.Red, green = (int)mat.Color.Green, blue = (int)mat.Color.Blue };
            }

            var patternsObj = new
            {
                surface_foreground_pattern_id = GetValidIdOrNull(mat.SurfaceForegroundPatternId),
                surface_background_pattern_id = GetValidIdOrNull(mat.SurfaceBackgroundPatternId),
                cut_foreground_pattern_id = GetValidIdOrNull(mat.CutForegroundPatternId),
                cut_background_pattern_id = GetValidIdOrNull(mat.CutBackgroundPatternId)
            };

            var identityObj = new
            {
                manufacturer = GetIdentityField(mat, BuiltInParameter.ALL_MODEL_MANUFACTURER, "Manufacturer"),
                model = GetIdentityField(mat, BuiltInParameter.ALL_MODEL_MODEL, "Model"),
                cost = GetIdentityField(mat, BuiltInParameter.ALL_MODEL_COST, "Cost"),
                keynote = GetIdentityField(mat, BuiltInParameter.KEYNOTE_PARAM, "Keynote"),
                mark = GetIdentityField(mat, BuiltInParameter.ALL_MODEL_MARK, "Mark"),
                url = GetIdentityField(mat, BuiltInParameter.INVALID, "URL")
            };

            object appearanceAssetObj = null;
            if (includeAssets && mat.AppearanceAssetId != null && mat.AppearanceAssetId != ElementId.InvalidElementId)
            {
                var appElem = doc.GetElement(mat.AppearanceAssetId) as AppearanceAssetElement;
                if (appElem != null)
                {
                    appearanceAssetObj = new
                    {
                        id = RevitCompat.GetId(appElem.Id),
                        name = appElem.Name
                    };
                }
            }

            object structuralAssetObj = null;
            if (includeAssets && mat.StructuralAssetId != null && mat.StructuralAssetId != ElementId.InvalidElementId)
            {
                var propSet = doc.GetElement(mat.StructuralAssetId) as PropertySetElement;
                if (propSet != null)
                {
                    try
                    {
                        var asset = propSet.GetStructuralAsset();
                        if (asset != null)
                        {
                            double densityVal = ConvertFromInternal(asset.Density, UnitTypeId.KilogramsPerCubicMeter);
                            double youngVal = ConvertFromInternal(asset.YoungModulus.X, UnitTypeId.Megapascals);
                            double poissonVal = asset.PoissonRatio.X;
                            double shearVal = ConvertFromInternal(asset.ShearModulus.X, UnitTypeId.Megapascals);

                            structuralAssetObj = new
                            {
                                id = RevitCompat.GetId(propSet.Id),
                                name = propSet.Name,
                                density_kg_per_m3 = Math.Round(densityVal, 2),
                                young_modulus_mpa = Math.Round(youngVal, 2),
                                poisson_ratio = Math.Round(poissonVal, 4),
                                shear_modulus_mpa = Math.Round(shearVal, 2)
                            };
                        }
                    }
                    catch
                    {
                        structuralAssetObj = new
                        {
                            id = RevitCompat.GetId(propSet.Id),
                            name = propSet.Name
                        };
                    }
                }
            }

            object thermalAssetObj = null;
            if (includeAssets && mat.ThermalAssetId != null && mat.ThermalAssetId != ElementId.InvalidElementId)
            {
                var propSet = doc.GetElement(mat.ThermalAssetId) as PropertySetElement;
                if (propSet != null)
                {
                    try
                    {
                        var asset = propSet.GetThermalAsset();
                        if (asset != null)
                        {
                            double condVal = ConvertFromInternal(asset.ThermalConductivity, UnitTypeId.WattsPerMeterKelvin);
                            double specHeatVal = ConvertFromInternal(asset.SpecificHeat, UnitTypeId.JoulesPerKilogramDegreeCelsius);
                            double emissVal = asset.Emissivity;
                            double permVal = asset.Permeability;
                            double densityVal = ConvertFromInternal(asset.Density, UnitTypeId.KilogramsPerCubicMeter);

                            thermalAssetObj = new
                            {
                                id = RevitCompat.GetId(propSet.Id),
                                name = propSet.Name,
                                conductivity_w_per_m_k = Math.Round(condVal, 4),
                                specific_heat_j_per_kg_k = Math.Round(specHeatVal, 2),
                                emissivity = Math.Round(emissVal, 4),
                                permeability = Math.Round(permVal, 4),
                                density_kg_per_m3 = Math.Round(densityVal, 2)
                            };
                        }
                    }
                    catch
                    {
                        thermalAssetObj = new
                        {
                            id = RevitCompat.GetId(propSet.Id),
                            name = propSet.Name
                        };
                    }
                }
            }

            var parametersList = new List<object>();
            if (includeParameters)
            {
                foreach (Parameter parameter in mat.Parameters)
                {
                    if (parameter == null) continue;
                    try
                    {
                        var def = parameter.Definition;
                        string valueString = null;
                        object rawValue = null;
                        switch (parameter.StorageType)
                        {
                            case StorageType.String:
                                rawValue = parameter.AsString() ?? "";
                                valueString = parameter.AsString() ?? "";
                                break;
                            case StorageType.Integer:
                                rawValue = parameter.AsInteger();
                                valueString = parameter.AsInteger().ToString(CultureInfo.InvariantCulture);
                                break;
                            case StorageType.Double:
                                rawValue = parameter.AsDouble();
                                valueString = parameter.AsDouble().ToString("R", CultureInfo.InvariantCulture);
                                break;
                            case StorageType.ElementId:
                                var eId = parameter.AsElementId();
                                var eIdVal = GetValidIdOrNull(eId);
                                rawValue = eIdVal;
                                valueString = eIdVal?.ToString(CultureInfo.InvariantCulture);
                                break;
                        }

                        parametersList.Add(new
                        {
                            name = def?.Name,
                            id = RevitCompat.GetIdOrNull(parameter.Id),
                            storageType = parameter.StorageType.ToString(),
                            isReadOnly = parameter.IsReadOnly,
                            hasValue = parameter.HasValue,
                            rawValue,
                            valueString
                        });
                    }
                    catch { }
                }
            }

            return CommandResult.Ok(new
            {
                id = resolvedId,
                name = mat.Name,
                material_class = mat.MaterialClass ?? "",
                material_category = mat.MaterialCategory ?? "",
                color = colorObj,
                transparency = mat.Transparency,
                shininess = mat.Shininess,
                smoothness = mat.Smoothness,
                patterns = patternsObj,
                identity = identityObj,
                appearance_asset = appearanceAssetObj,
                structural_asset = structuralAssetObj,
                thermal_asset = thermalAssetObj,
                parameters = parametersList
            });
        }

        private static long? GetValidIdOrNull(ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId) return null;
            long val = RevitCompat.GetId(id);
            return val == -1 ? null : (long?)val;
        }

        private static string GetIdentityField(Material mat, BuiltInParameter bip, string name)
        {
            try
            {
                if (bip != BuiltInParameter.INVALID)
                {
                    var p = mat.get_Parameter(bip);
                    if (p != null && p.HasValue)
                    {
                        if (p.StorageType == StorageType.String) return p.AsString() ?? "";
                        return p.AsValueString() ?? "";
                    }
                }
            }
            catch { }

            try
            {
                var p = mat.LookupParameter(name);
                if (p != null && p.HasValue)
                {
                    if (p.StorageType == StorageType.String) return p.AsString() ?? "";
                    return p.AsValueString() ?? "";
                }
            }
            catch { }

            return "";
        }

        private static double ConvertFromInternal(double value, ForgeTypeId typeId)
        {
            return UnitUtils.ConvertFromInternalUnits(value, typeId);
        }
    }
}
