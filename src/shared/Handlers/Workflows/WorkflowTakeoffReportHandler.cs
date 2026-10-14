using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class WorkflowTakeoffReportHandler : IRevitCommand
    {
        public string Name => "workflow_takeoff_report";
        public string Description => "Generate category, quantity, material, and optional cost takeoff reports with optional JSON/CSV export.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""categories"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""include_materials"": { ""type"": ""boolean"", ""default"": true },
    ""include_quantities"": { ""type"": ""boolean"", ""default"": true },
    ""include_cost"": { ""type"": ""boolean"", ""default"": false },
    ""output_path"": { ""type"": ""string"" },
    ""limit_per_category"": { ""type"": ""integer"", ""default"": 500, ""minimum"": 1, ""maximum"": 5000 },
    ""output"": { ""type"": ""string"", ""enum"": [""inline"", ""file""], ""default"": ""inline"" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No active document is available.");

            JObject request;
            try
            {
                request = WorkflowSupport.ParseParams(paramsJson);
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            string[] categoryNames;
            try
            {
                categoryNames = WorkflowSupport.ReadStringArray(request, "categories");
            }
            catch (Exception ex)
            {
                return CommandResult.Fail(ex.Message);
            }

            var includeMaterials = request.Value<bool?>("include_materials") ?? true;
            var includeQuantities = request.Value<bool?>("include_quantities") ?? true;
            var includeCost = request.Value<bool?>("include_cost") ?? false;
            var outputPath = request.Value<string>("output_path");
            var limitPerCategory = request.Value<int?>("limit_per_category") ?? 500;

            if (limitPerCategory < 1 || limitPerCategory > 5000)
                return CommandResult.Fail("limit_per_category must be between 1 and 5000.");
            if (!includeMaterials && !includeQuantities && !includeCost)
                return CommandResult.Fail("At least one of include_materials, include_quantities, or include_cost must be true.");

            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                var error = WorkflowSupport.ValidateRootedPath(outputPath, "output_path", true);
                if (error != null)
                    return CommandResult.Fail(error);
                var ext = (Path.GetExtension(outputPath) ?? string.Empty).ToLowerInvariant();
                if (ext != ".json" && ext != ".csv")
                    return CommandResult.Fail("output_path must end with .json or .csv.");
            }

            if (categoryNames == null || categoryNames.Length == 0)
            {
                categoryNames = new[]
                {
                    "Walls", "Floors", "Roofs", "Ceilings", "Doors", "Windows",
                    "Structural Framing", "Structural Columns", "Ducts", "Pipes"
                };
            }

            var steps = new JArray();
            var warnings = new List<string>();
            var categorySummary = new JArray();
            var quantities = new JArray();
            var materialBuckets = new Dictionary<long, MaterialBucket>();
            var costFields = new JArray();
            var truncationWarnings = new List<string>();

            foreach (var categoryName in categoryNames)
            {
                if (!WorkflowSupport.TryResolveCategory(doc, categoryName, out var category, out var categoryError))
                {
                    warnings.Add(categoryError);
                    continue;
                }

                var elements = WorkflowSupport.CollectInstances(doc, category, limitPerCategory, out var truncated);
                if (truncated)
                    truncationWarnings.Add("Category '" + category.Name + "' was truncated at " + limitPerCategory.ToString(CultureInfo.InvariantCulture) + " elements.");

                var categoryArea = 0.0;
                var categoryVolume = 0.0;
                var elementRows = new JArray();

                foreach (var element in elements)
                {
                    var elementArea = ReadAreaM2(element);
                    var elementVolume = ReadVolumeM3(element);
                    categoryArea += elementArea;
                    categoryVolume += elementVolume;

                    if (includeQuantities)
                    {
                        elementRows.Add(new JObject
                        {
                            ["element_id"] = RevitCompat.GetId(element.Id),
                            ["name"] = WorkflowSupport.SafeName(element),
                            ["category"] = category.Name,
                            ["area_m2"] = Math.Round(elementArea, 4),
                            ["volume_m3"] = Math.Round(elementVolume, 4)
                        });
                    }

                    if (includeMaterials || includeCost)
                        AccumulateMaterials(doc, element, category.Name, materialBuckets, includeCost);
                }

                categorySummary.Add(new JObject
                {
                    ["category"] = category.Name,
                    ["element_count"] = elements.Count,
                    ["area_m2"] = Math.Round(categoryArea, 4),
                    ["volume_m3"] = Math.Round(categoryVolume, 4),
                    ["truncated"] = truncated
                });

                if (includeQuantities)
                {
                    quantities.Add(new JObject
                    {
                        ["category"] = category.Name,
                        ["elements"] = elementRows
                    });
                }
            }

            var materialSummary = new JArray();
            foreach (var bucket in materialBuckets.Values.OrderByDescending(b => b.VolumeM3).ThenByDescending(b => b.AreaM2))
            {
                var materialObj = new JObject
                {
                    ["material_id"] = bucket.MaterialId,
                    ["material_name"] = bucket.MaterialName,
                    ["element_count"] = bucket.ElementIds.Count,
                    ["area_m2"] = Math.Round(bucket.AreaM2, 4),
                    ["volume_m3"] = Math.Round(bucket.VolumeM3, 4),
                    ["categories"] = JObject.FromObject(bucket.CategoryCounts)
                };

                if (includeCost)
                {
                    materialObj["unit_cost"] = bucket.UnitCost.HasValue ? new JValue(bucket.UnitCost.Value) : JValue.CreateNull();
                    materialObj["estimated_cost"] = bucket.UnitCost.HasValue ? new JValue(Math.Round(bucket.UnitCost.Value * bucket.VolumeM3, 2)) : JValue.CreateNull();
                    if (bucket.UnitCost.HasValue)
                    {
                        costFields.Add(new JObject
                        {
                            ["material_id"] = bucket.MaterialId,
                            ["material_name"] = bucket.MaterialName,
                            ["unit_cost"] = bucket.UnitCost.Value,
                            ["quantity_basis"] = "volume_m3",
                            ["estimated_cost"] = Math.Round(bucket.UnitCost.Value * bucket.VolumeM3, 2)
                        });
                    }
                }

                materialSummary.Add(materialObj);
            }

            steps.Add(WorkflowSupport.Step(
                "Category Quantities",
                "element collectors",
                "succeeded",
                "Collect model elements and summarize rough category quantities.",
                new { categories = categorySummary.Count }));

            if (includeMaterials)
            {
                steps.Add(WorkflowSupport.Step(
                    "Material Takeoff",
                    "get_material_takeoff",
                    "succeeded",
                    "Aggregate material areas and volumes by material and category.",
                    new { materials = materialSummary.Count }));
            }

            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                try
                {
                    var exportRows = BuildOutputRows(categorySummary, materialSummary, quantities, includeMaterials, includeQuantities);
                    WorkflowSupport.WriteJsonOrCsv(outputPath, exportRows, OutputColumns(exportRows));
                    steps.Add(WorkflowSupport.Step(
                        "Export Takeoff Report",
                        "file_export",
                        "succeeded",
                        "Write takeoff report to " + outputPath,
                        new { output_path = outputPath, rows = exportRows.Count }));
                }
                catch (Exception ex)
                {
                    steps.Add(WorkflowSupport.Step("Export Takeoff Report", "file_export", "failed", "Write takeoff report to " + outputPath, null, ex.Message));
                    return CommandResult.Ok(BuildEnvelope("failed", steps, warnings, categorySummary, materialSummary, quantities, costFields, outputPath, truncationWarnings));
                }
            }

            return CommandResult.Ok(BuildEnvelope("succeeded", steps, warnings, categorySummary, materialSummary, quantities, costFields, outputPath, truncationWarnings));
        }

        private JObject BuildEnvelope(
            string status,
            JArray steps,
            IEnumerable<string> warnings,
            JArray categorySummary,
            JArray materialSummary,
            JArray quantities,
            JArray costFields,
            string outputPath,
            IEnumerable<string> truncationWarnings)
        {
            var allWarnings = new List<string>();
            if (warnings != null)
                allWarnings.AddRange(warnings);
            if (truncationWarnings != null)
                allWarnings.AddRange(truncationWarnings);

            var envelope = WorkflowSupport.Envelope(
                Name,
                false,
                status,
                steps,
                Array.Empty<long>(),
                Array.Empty<long>(),
                allWarnings,
                WorkflowSupport.Rollback("None", false, "Read-only model scan; optional file export does not mutate Revit."));
            envelope["category_summary"] = categorySummary ?? new JArray();
            envelope["material_summary"] = materialSummary ?? new JArray();
            envelope["quantities"] = quantities ?? new JArray();
            envelope["cost_fields"] = costFields ?? new JArray();
            envelope["output_path"] = string.IsNullOrWhiteSpace(outputPath) ? JValue.CreateNull() : new JValue(outputPath);
            envelope["truncation_warnings"] = WorkflowSupport.ToJArray(truncationWarnings);
            return envelope;
        }

        private static void AccumulateMaterials(Document doc, Element element, string categoryName, Dictionary<long, MaterialBucket> buckets, bool includeCost)
        {
            try
            {
                foreach (var materialId in element.GetMaterialIds(false))
                {
                    var material = doc.GetElement(materialId) as Material;
                    if (material == null)
                        continue;

                    var rawId = RevitCompat.GetId(materialId);
                    if (!buckets.TryGetValue(rawId, out var bucket))
                    {
                        bucket = new MaterialBucket
                        {
                            MaterialId = rawId,
                            MaterialName = material.Name,
                            UnitCost = includeCost ? ReadMaterialCost(material) : null
                        };
                        buckets[rawId] = bucket;
                    }

                    bucket.AreaM2 += SafeMaterialArea(element, materialId);
                    bucket.VolumeM3 += SafeMaterialVolume(element, materialId);
                    bucket.ElementIds.Add(RevitCompat.GetId(element.Id));
                    bucket.CategoryCounts[categoryName] = bucket.CategoryCounts.TryGetValue(categoryName, out var count) ? count + 1 : 1;
                }
            }
            catch { }
        }

        private static double ReadAreaM2(Element element)
        {
            try
            {
                var param = element.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)
                    ?? element.LookupParameter("Area");
                if (param != null && param.StorageType == StorageType.Double)
                    return param.AsDouble() * WorkflowSupport.SquareFeetToSquareMeters;
            }
            catch { }
            return 0.0;
        }

        private static double ReadVolumeM3(Element element)
        {
            try
            {
                var param = element.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED)
                    ?? element.LookupParameter("Volume");
                if (param != null && param.StorageType == StorageType.Double)
                    return param.AsDouble() * WorkflowSupport.CubicFeetToCubicMeters;
            }
            catch { }
            return 0.0;
        }

        private static double SafeMaterialArea(Element element, ElementId materialId)
        {
            try { return element.GetMaterialArea(materialId, false) * WorkflowSupport.SquareFeetToSquareMeters; }
            catch { return 0.0; }
        }

        private static double SafeMaterialVolume(Element element, ElementId materialId)
        {
            try { return element.GetMaterialVolume(materialId) * WorkflowSupport.CubicFeetToCubicMeters; }
            catch { return 0.0; }
        }

        private static double? ReadMaterialCost(Material material)
        {
            foreach (var name in new[] { "Cost", "Material Cost", "Unit Cost" })
            {
                try
                {
                    var p = material.LookupParameter(name);
                    if (p == null)
                        continue;
                    if (p.StorageType == StorageType.Double)
                        return p.AsDouble();
                    if (p.StorageType == StorageType.Integer)
                        return p.AsInteger();
                    if (p.StorageType == StorageType.String &&
                        double.TryParse(p.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                        return parsed;
                }
                catch { }
            }
            return null;
        }

        private static JArray BuildOutputRows(JArray categorySummary, JArray materialSummary, JArray quantities, bool includeMaterials, bool includeQuantities)
        {
            var rows = new JArray();
            foreach (var category in categorySummary.OfType<JObject>())
            {
                var row = new JObject(category)
                {
                    ["row_type"] = "category"
                };
                rows.Add(row);
            }

            if (includeMaterials)
            {
                foreach (var material in materialSummary.OfType<JObject>())
                {
                    var row = new JObject(material)
                    {
                        ["row_type"] = "material"
                    };
                    rows.Add(row);
                }
            }

            if (includeQuantities)
            {
                foreach (var group in quantities.OfType<JObject>())
                {
                    var elements = group["elements"] as JArray;
                    if (elements == null)
                        continue;
                    foreach (var element in elements.OfType<JObject>())
                    {
                        var row = new JObject(element)
                        {
                            ["row_type"] = "element"
                        };
                        rows.Add(row);
                    }
                }
            }

            return rows;
        }

        private static string[] OutputColumns(JArray rows)
        {
            var columns = new List<string>();
            foreach (var row in rows.OfType<JObject>())
            {
                foreach (var property in row.Properties())
                {
                    if (!columns.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                        columns.Add(property.Name);
                }
            }
            return columns.ToArray();
        }

        private class MaterialBucket
        {
            public long MaterialId;
            public string MaterialName;
            public double AreaM2;
            public double VolumeM3;
            public double? UnitCost;
            public HashSet<long> ElementIds = new HashSet<long>();
            public Dictionary<string, int> CategoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
