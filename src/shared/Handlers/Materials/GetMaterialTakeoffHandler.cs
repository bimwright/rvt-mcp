using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class GetMaterialTakeoffHandler : IRevitCommand
    {
        public string Name => "get_material_takeoff";
        public string Description => "Calculate detailed material takeoff grouped by material and category";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""category_filter"": { ""type"": ""string"" },
    ""material_name_pattern"": { ""type"": ""string"" },
    ""include_elements"": { ""type"": ""boolean"", ""default"": false },
    ""element_limit"": { ""type"": ""integer"", ""default"": 100, ""minimum"": 0, ""maximum"": 10000 },
    ""output"": { ""type"": ""string"", ""enum"": [""inline"", ""file""], ""default"": ""inline"" }
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

            var categoryFilter = request.Value<string>("category_filter") ?? "";
            var materialNamePattern = request.Value<string>("material_name_pattern") ?? "";
            var includeElements = request.Value<bool?>("include_elements") ?? false;
            var elementLimit = request.Value<int?>("element_limit") ?? 100;

            if (elementLimit < 0) elementLimit = 0;
            if (elementLimit > 10000) elementLimit = 10000;

            var collector = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType();

            var elementsList = collector.ToList();
            if (!string.IsNullOrEmpty(categoryFilter))
            {
                elementsList = elementsList
                    .Where(el => el.Category != null && el.Category.Name.Equals(categoryFilter, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            int skippedElements = 0;
            var rawData = new Dictionary<long, List<ElementMaterialData>>();

            foreach (var el in elementsList)
            {
                try
                {
                    var matIds = el.GetMaterialIds(false);
                    if (matIds == null || matIds.Count == 0) continue;

                    string catName = el.Category?.Name ?? "Unassigned";

                    foreach (ElementId mId in matIds)
                    {
                        try
                        {
                            var mat = doc.GetElement(mId) as Material;
                            if (mat == null) continue;

                            if (!string.IsNullOrEmpty(materialNamePattern) && mat.Name.IndexOf(materialNamePattern, StringComparison.OrdinalIgnoreCase) < 0)
                                continue;

                            double sqftArea = el.GetMaterialArea(mId, false);
                            double cuftVolume = el.GetMaterialVolume(mId);

                            double areaM2 = sqftArea * 0.092903043596;
                            double volumeM3 = cuftVolume * 0.028316846592;

                            long mIdVal = RevitCompat.GetId(mId);
                            if (!rawData.ContainsKey(mIdVal))
                            {
                                rawData[mIdVal] = new List<ElementMaterialData>();
                            }

                            rawData[mIdVal].Add(new ElementMaterialData
                            {
                                ElementId = RevitCompat.GetId(el.Id),
                                Category = catName,
                                AreaM2 = areaM2,
                                VolumeM3 = volumeM3
                            });
                        }
                        catch
                        {
                            // Individual material query error
                        }
                    }
                }
                catch
                {
                    skippedElements++;
                }
            }

            var materialsTakeoffs = new List<object>();
            int totalUniqueElementCount = 0;
            bool truncatedElements = false;

            // Gather all distinct element IDs across all materials for the total count
            var allDistinctElementIds = new HashSet<long>();

            foreach (var kvp in rawData)
            {
                long matId = kvp.Key;
                var matElem = doc.GetElement(RevitCompat.ToElementId(matId)) as Material;
                string matName = matElem?.Name ?? "Unknown Material";

                var occurrences = kvp.Value;

                double totalAreaM2 = occurrences.Sum(o => o.AreaM2);
                double totalVolumeM3 = occurrences.Sum(o => o.VolumeM3);

                var uniqueElementIdsForMat = occurrences.Select(o => o.ElementId).Distinct().ToList();
                int elCountForMat = uniqueElementIdsForMat.Count;

                foreach (var id in uniqueElementIdsForMat)
                {
                    allDistinctElementIds.Add(id);
                }

                // Group by Category
                var categoriesGrouped = occurrences
                    .GroupBy(o => o.Category)
                    .Select(g => new
                    {
                        category = g.Key,
                        area_m2 = Math.Round(g.Sum(x => x.AreaM2), 2),
                        volume_m3 = Math.Round(g.Sum(x => x.VolumeM3), 4),
                        element_count = g.Select(x => x.ElementId).Distinct().Count()
                    })
                    .ToList();

                // Elements details
                List<object> elementsDetails = null;
                if (includeElements)
                {
                    var elementsListForMat = occurrences
                        .Select(o => new
                        {
                            element_id = o.ElementId,
                            category = o.Category,
                            area_m2 = Math.Round(o.AreaM2, 2),
                            volume_m3 = Math.Round(o.VolumeM3, 4)
                        })
                        .ToList();

                    if (elementsListForMat.Count > elementLimit)
                    {
                        truncatedElements = true;
                        elementsDetails = elementsListForMat.Take(elementLimit).Cast<object>().ToList();
                    }
                    else
                    {
                        elementsDetails = elementsListForMat.Cast<object>().ToList();
                    }
                }

                materialsTakeoffs.Add(new
                {
                    material_id = matId,
                    material_name = matName,
                    total_area_m2 = Math.Round(totalAreaM2, 2),
                    total_volume_m3 = Math.Round(totalVolumeM3, 4),
                    element_count = elCountForMat,
                    categories = categoriesGrouped,
                    elements = elementsDetails
                });
            }

            totalUniqueElementCount = allDistinctElementIds.Count;

            return CommandResult.Ok(new
            {
                material_count = rawData.Count,
                element_count = totalUniqueElementCount,
                skipped_elements = skippedElements,
                materials = materialsTakeoffs,
                truncated_elements = truncatedElements
            });
        }

        private class ElementMaterialData
        {
            public long ElementId { get; set; }
            public string Category { get; set; }
            public double AreaM2 { get; set; }
            public double VolumeM3 { get; set; }
        }
    }
}
