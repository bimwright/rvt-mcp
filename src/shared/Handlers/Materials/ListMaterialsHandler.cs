using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ListMaterialsHandler : IRevitCommand
    {
        public string Name => "list_materials";
        public string Description => "List and filter materials in the active document";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""name_pattern"": { ""type"": ""string"" },
    ""class_filter"": { ""type"": ""string"" },
    ""include_assets"": { ""type"": ""boolean"", ""default"": true },
    ""include_use_count"": { ""type"": ""boolean"", ""default"": false },
    ""limit"": { ""type"": ""integer"", ""default"": 1000, ""minimum"": 1, ""maximum"": 10000 }
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

            var namePattern = request.Value<string>("name_pattern") ?? "";
            var classFilter = request.Value<string>("class_filter") ?? "";
            var includeAssets = request.Value<bool?>("include_assets") ?? true;
            var includeUseCount = request.Value<bool?>("include_use_count") ?? false;
            var limit = request.Value<int?>("limit") ?? 1000;

            if (limit < 1) limit = 1;
            if (limit > 10000) limit = 10000;

            var collector = new FilteredElementCollector(doc).OfClass(typeof(Material));
            var allMaterials = collector.Cast<Material>().ToList();

            var useCounts = new Dictionary<long, int>();
            if (includeUseCount)
            {
                var nonTypeElements = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .ToList();
                foreach (var el in nonTypeElements)
                {
                    try
                    {
                        var matIds = el.GetMaterialIds(false);
                        if (matIds != null)
                        {
                            foreach (ElementId mId in matIds)
                            {
                                long val = RevitCompat.GetId(mId);
                                if (useCounts.ContainsKey(val))
                                    useCounts[val]++;
                                else
                                    useCounts[val] = 1;
                            }
                        }
                    }
                    catch
                    {
                        // Safely skip any single element material query error
                    }
                }
            }

            var materialsList = new List<object>();
            int skipped = 0;

            foreach (var mat in allMaterials)
            {
                try
                {
                    if (!string.IsNullOrEmpty(namePattern) && mat.Name.IndexOf(namePattern, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    if (!string.IsNullOrEmpty(classFilter) && (mat.MaterialClass == null || mat.MaterialClass.IndexOf(classFilter, StringComparison.OrdinalIgnoreCase) < 0))
                        continue;

                    long matId = RevitCompat.GetId(mat.Id);

                    object colorObj = null;
                    if (mat.Color != null)
                    {
                        colorObj = new { red = (int)mat.Color.Red, green = (int)mat.Color.Green, blue = (int)mat.Color.Blue };
                    }

                    materialsList.Add(new
                    {
                        id = matId,
                        name = mat.Name,
                        material_class = mat.MaterialClass ?? "",
                        material_category = mat.MaterialCategory ?? "",
                        color = colorObj,
                        transparency = mat.Transparency,
                        appearance_asset_id = includeAssets ? GetValidIdOrNull(mat.AppearanceAssetId) : null,
                        structural_asset_id = includeAssets ? GetValidIdOrNull(mat.StructuralAssetId) : null,
                        thermal_asset_id = includeAssets ? GetValidIdOrNull(mat.ThermalAssetId) : null,
                        use_count = includeUseCount ? (useCounts.TryGetValue(matId, out var count) ? count : 0) : (int?)null
                    });
                }
                catch
                {
                    skipped++;
                }
            }

            var resultList = materialsList.Take(limit).ToList();
            return CommandResult.Ok(new
            {
                total = materialsList.Count,
                returned = resultList.Count,
                limit_hit = materialsList.Count > limit,
                skipped,
                materials = resultList
            });
        }

        private static long? GetValidIdOrNull(ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId) return null;
            long val = RevitCompat.GetId(id);
            return val == -1 ? null : (long?)val;
        }
    }
}
