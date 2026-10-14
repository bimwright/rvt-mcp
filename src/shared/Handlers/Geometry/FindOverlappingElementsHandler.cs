using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class FindOverlappingElementsHandler : IRevitCommand
    {
        public string Name => "find_overlapping_elements";
        public string Description => "Find potentially overlapping elements of the same category, using bounding box intersection analysis.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""category""],
  ""properties"": {
    ""category"": { ""type"": ""string"" },
    ""view_id"": { ""type"": ""integer"" },
    ""max_pairs"": { ""type"": ""integer"", ""default"": 1000, ""maximum"": 10000 },
    ""max_results"": { ""type"": ""integer"", ""default"": 100, ""maximum"": 500 }
  }
}";

        private const double FeetToMm = 304.8;

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
            catch (Exception ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            var categoryName = request.Value<string>("category");
            if (string.IsNullOrEmpty(categoryName))
                return CommandResult.Fail("category is required (string).");

            var maxPairsToken = request["max_pairs"] ?? request["maxPairs"];
            int maxPairs = maxPairsToken != null ? maxPairsToken.Value<int>() : 1000;
            if (maxPairs < 1)
                return CommandResult.Fail("max_pairs must be at least 1.");
            if (maxPairs > 10000)
                return CommandResult.Fail("Limit exceeded: max_pairs exceeds the hard limit of 10000.");

            var maxResultsToken = request["max_results"] ?? request["maxResults"];
            int maxResults = maxResultsToken != null ? maxResultsToken.Value<int>() : 100;
            if (maxResults < 1)
                return CommandResult.Fail("max_results must be at least 1.");
            if (maxResults > 500)
                return CommandResult.Fail("Limit exceeded: max_results exceeds the hard limit of 500.");

            var viewIdToken = request["view_id"] ?? request["viewId"];
            View viewObj = null;
            if (viewIdToken != null && viewIdToken.Type == JTokenType.Integer)
            {
                long vId = viewIdToken.Value<long>();
                if (!RevitCompat.CanRepresentElementId(vId))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(vId));

                viewObj = doc.GetElement(RevitCompat.ToElementId(vId)) as View;
                if (viewObj == null)
                    return CommandResult.Fail($"View with ID {vId} was not found.");
            }

            // Resolve category
            var resolvedCats = ResolveCategories(doc, new[] { categoryName });
            if (resolvedCats.Count == 0)
                return CommandResult.Fail($"Category '{categoryName}' could not be resolved in the document.");

            var category = resolvedCats.First();

            // Collect elements
            var elements = GetElementsOfCategory(doc, category, viewObj);

            // Extract bounding boxes
            var validElements = new List<(Element Element, BoundingBoxXYZ Box)>();
            int skippedNoBbox = 0;
            foreach (var elem in elements)
            {
                try
                {
                    var bbox = elem.get_BoundingBox(viewObj);
                    if (bbox != null)
                    {
                        validElements.Add((elem, bbox));
                    }
                    else
                    {
                        skippedNoBbox++;
                    }
                }
                catch
                {
                    skippedNoBbox++;
                }
            }

            int scanned = validElements.Count;
            var overlaps = new List<object>();

            int pairsChecked = 0;
            bool truncated = false;
            string truncationReason = null;

            // Perform pairwise triangular check to avoid self-pairs and redundant checks
            for (int i = 0; i < scanned; i++)
            {
                if (overlaps.Count >= maxResults)
                {
                    truncated = true;
                    truncationReason = "max_results";
                    break;
                }
                if (pairsChecked >= maxPairs)
                {
                    truncated = true;
                    truncationReason = "max_pairs";
                    break;
                }

                var itemA = validElements[i];

                for (int j = i + 1; j < scanned; j++)
                {
                    if (overlaps.Count >= maxResults)
                    {
                        truncated = true;
                        truncationReason = "max_results";
                        break;
                    }
                    if (pairsChecked >= maxPairs)
                    {
                        truncated = true;
                        truncationReason = "max_pairs";
                        break;
                    }

                    var itemB = validElements[j];

                    pairsChecked++;

                    if (BboxesOverlap(itemA.Box, itemB.Box))
                    {
                        var overlapBox = GetBboxOverlap(itemA.Box, itemB.Box);
                        overlaps.Add(new
                        {
                            a = new
                            {
                                element_id = RevitCompat.GetId(itemA.Element.Id),
                                name = itemA.Element.Name ?? string.Empty
                            },
                            b = new
                            {
                                element_id = RevitCompat.GetId(itemB.Element.Id),
                                name = itemB.Element.Name ?? string.Empty
                            },
                            overlap_box = new
                            {
                                unit = "mm",
                                min = new { x = Math.Round(overlapBox.Min.X * FeetToMm, 3), y = Math.Round(overlapBox.Min.Y * FeetToMm, 3), z = Math.Round(overlapBox.Min.Z * FeetToMm, 3) },
                                max = new { x = Math.Round(overlapBox.Max.X * FeetToMm, 3), y = Math.Round(overlapBox.Max.Y * FeetToMm, 3), z = Math.Round(overlapBox.Max.Z * FeetToMm, 3) }
                            }
                        });
                    }
                }
            }

            return CommandResult.Ok(new
            {
                strategy = "bounding_box",
                category = category.Name,
                scanned = scanned,
                pairs_considered = pairsChecked,
                returned = overlaps.Count,
                limit = maxResults,
                truncated = truncated,
                truncation_reason = truncationReason,
                overlaps = overlaps,
                skipped_no_bbox = skippedNoBbox,
                warnings = new List<string> { "bbox overlap is a pre-clash filter, not proof of solid intersection" },
                error = (string)null
            });
        }

        private static ICollection<Category> ResolveCategories(Document doc, IEnumerable<string> categoryNames)
        {
            var resolved = new List<Category>();
            var allCategories = doc.Settings.Categories;

            foreach (var name in categoryNames)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;

                Category found = null;
                foreach (Category cat in allCategories)
                {
                    if (cat.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        found = cat;
                        break;
                    }
                }

                if (found == null)
                {
                    foreach (BuiltInCategory bic in Enum.GetValues(typeof(BuiltInCategory)))
                    {
                        if (bic.ToString().Equals(name, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                found = Category.GetCategory(doc, bic);
                                if (found != null) break;
                            }
                            catch {}
                        }
                    }
                }

                if (found != null)
                {
                    resolved.Add(found);
                }
            }
            return resolved;
        }

        private static List<Element> GetElementsOfCategory(Document doc, Category category, View view)
        {
            FilteredElementCollector collector;
            if (view != null)
                collector = new FilteredElementCollector(doc, view.Id);
            else
                collector = new FilteredElementCollector(doc);

            return collector
                .OfCategoryId(category.Id)
                .WhereElementIsNotElementType()
                .ToElements()
                .ToList();
        }

        private static bool BboxesOverlap(BoundingBoxXYZ boxA, BoundingBoxXYZ boxB)
        {
            if (boxA == null || boxB == null) return false;
            return (boxA.Min.X <= boxB.Max.X && boxA.Max.X >= boxB.Min.X) &&
                   (boxA.Min.Y <= boxB.Max.Y && boxA.Max.Y >= boxB.Min.Y) &&
                   (boxA.Min.Z <= boxB.Max.Z && boxA.Max.Z >= boxB.Min.Z);
        }

        private static BoundingBoxXYZ GetBboxOverlap(BoundingBoxXYZ boxA, BoundingBoxXYZ boxB)
        {
            var min = new XYZ(Math.Max(boxA.Min.X, boxB.Min.X), Math.Max(boxA.Min.Y, boxB.Min.Y), Math.Max(boxA.Min.Z, boxB.Min.Z));
            var max = new XYZ(Math.Min(boxA.Max.X, boxB.Max.X), Math.Min(boxA.Max.Y, boxB.Max.Y), Math.Min(boxA.Max.Z, boxB.Max.Z));
            var res = new BoundingBoxXYZ();
            res.Min = min;
            res.Max = max;
            return res;
        }
    }
}
