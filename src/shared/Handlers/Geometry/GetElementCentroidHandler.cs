using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class GetElementCentroidHandler : IRevitCommand
    {
        public string Name => "get_element_centroid";
        public string Description => "Compute the geometric centroid of one or more elements using either bounding box centers or volume-weighted solid centroids.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""element_ids""],
  ""properties"": {
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""minItems"": 1 },
    ""strategy"": { ""type"": ""string"", ""enum"": [""bbox"", ""solid_then_bbox""], ""default"": ""solid_then_bbox"" }
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

            var elementIdsToken = request["element_ids"] ?? request["elementIds"];
            if (elementIdsToken == null || elementIdsToken.Type != JTokenType.Array)
                return CommandResult.Fail("element_ids is required (array).");

            var elementIdsList = new List<long>();
            foreach (var token in elementIdsToken)
            {
                if (token.Type == JTokenType.Integer)
                {
                    elementIdsList.Add(token.Value<long>());
                }
            }

            if (elementIdsList.Count == 0)
                return CommandResult.Fail("element_ids must contain at least one ID.");

            if (elementIdsList.Count > 500)
                return CommandResult.Fail("Limit exceeded: get_element_centroid accepts a maximum of 500 element IDs. Narrow the query to continue.");

            var strategyStr = (request["strategy"] ?? request["strategy"])?.Value<string>() ?? "solid_then_bbox";
            if (!strategyStr.Equals("bbox", StringComparison.OrdinalIgnoreCase) &&
                !strategyStr.Equals("solid_then_bbox", StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.Fail("strategy must be one of: bbox, solid_then_bbox.");
            }
            bool trySolid = strategyStr.Equals("solid_then_bbox", StringComparison.OrdinalIgnoreCase);

            var options = new Options
            {
                DetailLevel = ViewDetailLevel.Medium,
                ComputeReferences = false,
                IncludeNonVisibleObjects = false
            };

            var centroids = new List<object>();
            var failed = new List<object>();

            foreach (var id in elementIdsList)
            {
                if (!RevitCompat.CanRepresentElementId(id))
                {
                    failed.Add(new { element_id = id, error = RevitCompat.ElementIdRangeError(id) });
                    continue;
                }

                Element elem = null;
                try
                {
                    elem = doc.GetElement(RevitCompat.ToElementId(id));
                }
                catch (Exception ex)
                {
                    failed.Add(new { element_id = id, error = "Failed to fetch element: " + ex.Message });
                    continue;
                }

                if (elem == null)
                {
                    failed.Add(new { element_id = id, error = "Element not found." });
                    continue;
                }

                XYZ computedCentroid = null;
                double? volumeM3 = null;
                bool fallbackUsed = false;
                string usedStrategy = "bbox";
                var limitations = new List<string>();

                bool centroidSuccess = false;

                if (trySolid)
                {
                    XYZ solidCentroid;
                    double totalVol;
                    string solidErr;
                    if (TryComputeSolidCentroid(elem, options, out solidCentroid, out totalVol, out solidErr))
                    {
                        computedCentroid = solidCentroid;
                        volumeM3 = totalVol * CubicFeetToCubicMeters;
                        usedStrategy = "solid";
                        centroidSuccess = true;
                    }
                    else
                    {
                        limitations.Add("Solid centroid computation failed: " + solidErr);
                        fallbackUsed = true;
                    }
                }

                if (!centroidSuccess)
                {
                    BoundingBoxXYZ bbox = null;
                    try
                    {
                        bbox = elem.get_BoundingBox(null);
                    }
                    catch {}

                    if (bbox != null)
                    {
                        computedCentroid = 0.5 * (bbox.Min + bbox.Max);
                        usedStrategy = "bbox";
                    }
                    else
                    {
                        failed.Add(new { element_id = id, error = "Both solid geometry and bounding box were unavailable for this element." });
                        continue;
                    }
                }

                centroids.Add(new
                {
                    element_id = id,
                    name = elem.Name ?? string.Empty,
                    category = elem.Category?.Name ?? string.Empty,
                    strategy = usedStrategy,
                    centroid = new
                    {
                        x = Math.Round(computedCentroid.X * FeetToMm, 3),
                        y = Math.Round(computedCentroid.Y * FeetToMm, 3),
                        z = Math.Round(computedCentroid.Z * FeetToMm, 3)
                    },
                    volume_m3 = volumeM3.HasValue ? (double?)Math.Round(volumeM3.Value, 6) : null,
                    fallback_used = fallbackUsed,
                    limitations = limitations
                });
            }

            return CommandResult.Ok(new
            {
                unit = "mm",
                requested = elementIdsList.Count,
                returned = centroids.Count,
                centroids = centroids,
                failed = failed,
                error = (string)null
            });
        }

        private static bool TryComputeSolidCentroid(Element elem, Options options, out XYZ centroid, out double totalVolume, out string err)
        {
            centroid = null;
            totalVolume = 0;
            err = null;

            try
            {
                var geom = elem.get_Geometry(options);
                if (geom == null)
                {
                    err = "Element has no geometry.";
                    return false;
                }

                var solids = new List<Solid>();
                ExtractSolids(geom, solids);

                if (solids.Count == 0)
                {
                    err = "Element has no solid geometry.";
                    return false;
                }

                double sumX = 0;
                double sumY = 0;
                double sumZ = 0;
                double sumVol = 0;

                foreach (var solid in solids)
                {
                    if (solid.Volume > 1e-9)
                    {
                        try
                        {
                            XYZ solidCentroid = solid.ComputeCentroid();
                            if (solidCentroid != null)
                            {
                                sumX += solidCentroid.X * solid.Volume;
                                sumY += solidCentroid.Y * solid.Volume;
                                sumZ += solidCentroid.Z * solid.Volume;
                                sumVol += solid.Volume;
                            }
                        }
                        catch
                        {
                            // Ignore this solid if centroid calculation fails, continue with others
                        }
                    }
                }

                if (sumVol > 1e-9)
                {
                    centroid = new XYZ(sumX / sumVol, sumY / sumVol, sumZ / sumVol);
                    totalVolume = sumVol;
                    return true;
                }

                err = "No solids with positive volume and valid centroids found.";
                return false;
            }
            catch (Exception ex)
            {
                err = "Exception during centroid computation: " + ex.Message;
                return false;
            }
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
