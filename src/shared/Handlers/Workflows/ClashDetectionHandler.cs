using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ClashDetectionHandler : IRevitCommand
    {
        public string Name => "clash_detection";
        public string Description => "Detect clashes between elements of category A and category B, using axis-aligned bounding boxes and optional precise solid geometry intersection checks.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""categories_a"", ""categories_b""],
  ""properties"": {
    ""categories_a"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1 },
    ""categories_b"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1 },
    ""view_id"": { ""type"": ""integer"" },
    ""strategy"": { ""type"": ""string"", ""enum"": [""bbox"", ""bbox_then_solid""], ""default"": ""bbox_then_solid"" },
    ""max_pairs"": { ""type"": ""integer"", ""default"": 1000, ""minimum"": 1, ""maximum"": 10000 },
    ""max_results"": { ""type"": ""integer"", ""default"": 100, ""minimum"": 1, ""maximum"": 500 }
  }
}";

        private const double FeetToMm = 304.8;
        private const double CubicFeetToCubicMeters = 0.028316846592;

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

            var catsAToken = request["categories_a"] ?? request["categoriesA"];
            var catsBToken = request["categories_b"] ?? request["categoriesB"];

            if (catsAToken == null || catsAToken.Type != JTokenType.Array)
                return CommandResult.Fail("categories_a is required (array of strings).");
            if (catsBToken == null || catsBToken.Type != JTokenType.Array)
                return CommandResult.Fail("categories_b is required (array of strings).");

            var catsA = catsAToken.Select(t => t.Value<string>()).Where(s => !string.IsNullOrEmpty(s)).ToList();
            var catsB = catsBToken.Select(t => t.Value<string>()).Where(s => !string.IsNullOrEmpty(s)).ToList();

            if (catsA.Count == 0 || catsB.Count == 0)
                return CommandResult.Fail("Both categories_a and categories_b must contain at least one non-empty category name.");

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
            View view = null;
            if (viewIdToken != null && viewIdToken.Type == JTokenType.Integer)
            {
                var vId = viewIdToken.Value<long>();
                if (!RevitCompat.CanRepresentElementId(vId))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(vId));

                view = doc.GetElement(RevitCompat.ToElementId(vId)) as View;
                if (view == null)
                    return CommandResult.Fail($"No view found with ID {vId}.");
            }

            var strategyStr = (request["strategy"] ?? request["strategy"])?.Value<string>() ?? "bbox_then_solid";
            if (!strategyStr.Equals("bbox", StringComparison.OrdinalIgnoreCase) &&
                !strategyStr.Equals("bbox_then_solid", StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.Fail("strategy must be one of: bbox, bbox_then_solid.");
            }
            bool doSolidCheck = strategyStr.Equals("bbox_then_solid", StringComparison.OrdinalIgnoreCase);

            // Resolve categories
            var resolvedCatsA = ResolveCategories(doc, catsA);
            var resolvedCatsB = ResolveCategories(doc, catsB);

            var warnings = new List<string>();
            if (resolvedCatsA.Count < catsA.Count)
                warnings.Add("Some categories in categories_a could not be resolved.");
            if (resolvedCatsB.Count < catsB.Count)
                warnings.Add("Some categories in categories_b could not be resolved.");

            if (resolvedCatsA.Count == 0)
                return CommandResult.Fail("No categories in categories_a could be resolved in the document.");
            if (resolvedCatsB.Count == 0)
                return CommandResult.Fail("No categories in categories_b could be resolved in the document.");

            // Collect elements
            var elementsA = GetElementsOfCategories(doc, resolvedCatsA, view);
            var elementsB = GetElementsOfCategories(doc, resolvedCatsB, view);

            // Fetch and cache bounding boxes and elements
            var validElemsA = new List<(Element Element, BoundingBoxXYZ Box)>();
            foreach (var elem in elementsA)
            {
                try
                {
                    var bbox = elem.get_BoundingBox(view);
                    if (bbox != null) validElemsA.Add((elem, bbox));
                }
                catch {}
            }

            var validElemsB = new List<(Element Element, BoundingBoxXYZ Box)>();
            foreach (var elem in elementsB)
            {
                try
                {
                    var bbox = elem.get_BoundingBox(view);
                    if (bbox != null) validElemsB.Add((elem, bbox));
                }
                catch {}
            }

            int scannedA = validElemsA.Count;
            int scannedB = validElemsB.Count;
            long pairsConsidered = (long)scannedA * scannedB;

            var clashes = new List<object>();
            var failed = new List<object>();
            var solidCache = new Dictionary<long, List<Solid>>();
            var options = new Options
            {
                DetailLevel = ViewDetailLevel.Medium,
                ComputeReferences = false,
                IncludeNonVisibleObjects = false
            };

            int pairsChecked = 0;
            int bboxCandidates = 0;
            int solidTests = 0;
            bool truncated = false;
            string truncationReason = null;

            // Run collision checks
            foreach (var itemA in validElemsA)
            {
                if (clashes.Count >= maxResults)
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

                foreach (var itemB in validElemsB)
                {
                    if (clashes.Count >= maxResults)
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

                    // Skip self-intersection if identical
                    if (itemA.Element.Id == itemB.Element.Id) continue;

                    pairsChecked++;

                    if (BboxesOverlap(itemA.Box, itemB.Box))
                    {
                        bboxCandidates++;
                        bool solidIntersects = false;
                        double intersectionVolume = 0.0;
                        string pairError = null;

                        if (doSolidCheck)
                        {
                            solidTests++;
                            try
                            {
                                var solidsA = GetCachedSolids(itemA.Element, solidCache, options);
                                var solidsB = GetCachedSolids(itemB.Element, solidCache, options);

                                foreach (var solidA in solidsA)
                                {
                                    if (solidIntersects) break;
                                    foreach (var solidB in solidsB)
                                    {
                                        try
                                        {
                                            Solid intersectionSolid = BooleanOperationsUtils.ExecuteBooleanOperation(
                                                solidA, solidB, BooleanOperationsType.Intersect);

                                            if (intersectionSolid != null && intersectionSolid.Volume > 1e-9)
                                            {
                                                solidIntersects = true;
                                                intersectionVolume += intersectionSolid.Volume * CubicFeetToCubicMeters;
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            pairError = "Boolean intersection failed: " + ex.Message;
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                pairError = "Geometry solid extraction failed: " + ex.Message;
                            }
                        }

                        if (pairError != null)
                        {
                            failed.Add(new
                            {
                                a_element_id = RevitCompat.GetId(itemA.Element.Id),
                                b_element_id = RevitCompat.GetId(itemB.Element.Id),
                                error = pairError
                            });
                        }

                        // Record clash
                        if (pairError == null && (!doSolidCheck || solidIntersects))
                        {
                            var overlapBox = GetBboxOverlap(itemA.Box, itemB.Box);

                            clashes.Add(new
                            {
                                a = new
                                {
                                    element_id = RevitCompat.GetId(itemA.Element.Id),
                                    category = itemA.Element.Category?.Name ?? string.Empty,
                                    name = itemA.Element.Name ?? string.Empty
                                },
                                b = new
                                {
                                    element_id = RevitCompat.GetId(itemB.Element.Id),
                                    category = itemB.Element.Category?.Name ?? string.Empty,
                                    name = itemB.Element.Name ?? string.Empty
                                },
                                bbox_intersects = true,
                                solid_intersects = solidIntersects,
                                intersection_volume_m3 = Math.Round(intersectionVolume, 6),
                                bbox_overlap = new
                                {
                                    unit = "mm",
                                    min = new { x = Math.Round(overlapBox.Min.X * FeetToMm, 3), y = Math.Round(overlapBox.Min.Y * FeetToMm, 3), z = Math.Round(overlapBox.Min.Z * FeetToMm, 3) },
                                    max = new { x = Math.Round(overlapBox.Max.X * FeetToMm, 3), y = Math.Round(overlapBox.Max.Y * FeetToMm, 3), z = Math.Round(overlapBox.Max.Z * FeetToMm, 3) }
                                },
                                confidence = solidIntersects ? "solid" : "bbox",
                                error = (string)null
                            });
                        }
                    }
                }
            }

            return CommandResult.Ok(new
            {
                strategy = doSolidCheck ? "bounding_box_prefilter_then_solid" : "bounding_box",
                categories_a = catsA,
                categories_b = catsB,
                scanned_a = scannedA,
                scanned_b = scannedB,
                pairs_considered = pairsChecked, // represents the actual checked count before limit
                bbox_candidates = bboxCandidates,
                solid_tests = solidTests,
                returned = clashes.Count,
                limit = maxResults,
                truncated = truncated,
                truncation_reason = truncationReason,
                clashes = clashes,
                failed = failed,
                warnings = warnings,
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

        private static List<Element> GetElementsOfCategories(Document doc, ICollection<Category> categories, View view)
        {
            if (categories == null || categories.Count == 0) return new List<Element>();

            var catIds = categories.Select(c => c.Id).ToList();
            var filter = new ElementMulticategoryFilter(catIds);

            FilteredElementCollector collector;
            if (view != null)
            {
                collector = new FilteredElementCollector(doc, view.Id);
            }
            else
            {
                collector = new FilteredElementCollector(doc);
            }

            return collector
                .WherePasses(filter)
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

        private static List<Solid> GetCachedSolids(Element elem, Dictionary<long, List<Solid>> cache, Options options)
        {
            long id = RevitCompat.GetId(elem.Id);
            if (cache.TryGetValue(id, out List<Solid> solids))
            {
                return solids;
            }

            solids = new List<Solid>();
            try
            {
                var geom = elem.get_Geometry(options);
                if (geom != null)
                {
                    ExtractSolids(geom, solids);
                }
            }
            catch {}

            cache[id] = solids;
            return solids;
        }

        private static void ExtractSolids(GeometryObject geomObj, List<Solid> solids)
        {
            if (geomObj == null) return;
            if (geomObj is Solid solid)
            {
                if (solid.Volume > 1e-9 && solid.Faces.Size > 0)
                {
                    solids.Add(solid);
                }
            }
            else if (geomObj is GeometryInstance instance)
            {
                try
                {
                    var instGeom = instance.GetInstanceGeometry();
                    if (instGeom != null)
                    {
                        foreach (GeometryObject nested in instGeom)
                        {
                            ExtractSolids(nested, solids);
                        }
                    }
                }
                catch {}
            }
            else if (geomObj is GeometryElement geomElem)
            {
                foreach (GeometryObject obj in geomElem)
                {
                    ExtractSolids(obj, solids);
                }
            }
        }
    }
}
