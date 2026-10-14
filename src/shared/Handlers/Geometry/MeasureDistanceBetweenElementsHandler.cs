using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class MeasureDistanceBetweenElementsHandler : IRevitCommand
    {
        public string Name => "measure_distance_between_elements";
        public string Description => "Measure shortest distance between two elements based on bounding boxes, element locations, or bounding box pre-filter.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""element_id_1"", ""element_id_2""],
  ""properties"": {
    ""element_id_1"": { ""type"": ""integer"" },
    ""element_id_2"": { ""type"": ""integer"" },
    ""strategy"": { ""type"": ""string"", ""enum"": [""bbox"", ""location"", ""solid_bbox_prefilter""], ""default"": ""bbox"" }
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

            var elem1Token = request["element_id_1"] ?? request["elementId1"];
            var elem2Token = request["element_id_2"] ?? request["elementId2"];

            if (elem1Token == null || elem1Token.Type != JTokenType.Integer)
                return CommandResult.Fail("element_id_1 is required (integer).");
            if (elem2Token == null || elem2Token.Type != JTokenType.Integer)
                return CommandResult.Fail("element_id_2 is required (integer).");

            long id1 = elem1Token.Value<long>();
            long id2 = elem2Token.Value<long>();

            if (!RevitCompat.CanRepresentElementId(id1))
                return CommandResult.Fail(RevitCompat.ElementIdRangeError(id1));
            if (!RevitCompat.CanRepresentElementId(id2))
                return CommandResult.Fail(RevitCompat.ElementIdRangeError(id2));

            Element e1 = null;
            Element e2 = null;

            try
            {
                e1 = doc.GetElement(RevitCompat.ToElementId(id1));
                e2 = doc.GetElement(RevitCompat.ToElementId(id2));
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Failed to fetch elements: " + ex.Message);
            }

            if (e1 == null)
                return CommandResult.Fail($"Element with ID {id1} was not found.");
            if (e2 == null)
                return CommandResult.Fail($"Element with ID {id2} was not found.");

            var strategyStr = (request["strategy"] ?? request["strategy"])?.Value<string>() ?? "bbox";
            strategyStr = strategyStr.ToLowerInvariant();

            BoundingBoxXYZ bbox1 = null;
            BoundingBoxXYZ bbox2 = null;

            try
            {
                bbox1 = e1.get_BoundingBox(null);
                bbox2 = e2.get_BoundingBox(null);
            }
            catch {}

            var limitations = new List<string>();

            if (strategyStr == "bbox" || strategyStr == "solid_bbox_prefilter")
            {
                if (bbox1 == null)
                    return CommandResult.Fail($"Bounding box for element {id1} is unavailable.");
                if (bbox2 == null)
                    return CommandResult.Fail($"Bounding box for element {id2} is unavailable.");

                XYZ ptA, ptB;
                double distanceFeet = DistanceBetweenBoxes(bbox1, bbox2, out ptA, out ptB);
                double distanceMm = distanceFeet * FeetToMm;
                bool intersects = distanceFeet <= 1e-7;

                if (strategyStr == "bbox")
                {
                    limitations.Add("bbox distance is axis-aligned-box distance, not exact solid-to-solid clearance");
                }
                else
                {
                    limitations.Add("solid_bbox_prefilter overlaps mean bounding boxes overlap. Exact solid intersection was not run.");
                }

                return CommandResult.Ok(new
                {
                    unit = "mm",
                    element_id_1 = id1,
                    element_id_2 = id2,
                    strategy = strategyStr,
                    distance = Math.Round(distanceMm, 3),
                    intersects = intersects,
                    point_1 = new { x = Math.Round(ptA.X * FeetToMm, 3), y = Math.Round(ptA.Y * FeetToMm, 3), z = Math.Round(ptA.Z * FeetToMm, 3) },
                    point_2 = new { x = Math.Round(ptB.X * FeetToMm, 3), y = Math.Round(ptB.Y * FeetToMm, 3), z = Math.Round(ptB.Z * FeetToMm, 3) },
                    bbox_1 = FormatBbox(bbox1),
                    bbox_2 = FormatBbox(bbox2),
                    limitations = limitations,
                    error = (string)null
                });
            }
            else if (strategyStr == "location")
            {
                XYZ ptA, ptB;
                double distanceFeet;
                string locError;
                if (!TryMeasureLocationDistance(e1, e2, out distanceFeet, out ptA, out ptB, out locError))
                {
                    return CommandResult.Fail($"Location measurement strategy failed: {locError}");
                }

                double distanceMm = distanceFeet * FeetToMm;
                bool intersects = distanceFeet <= 1e-7;

                limitations.Add("location distance is measured between geometric center points or closest points along curve paths.");

                return CommandResult.Ok(new
                {
                    unit = "mm",
                    element_id_1 = id1,
                    element_id_2 = id2,
                    strategy = "location",
                    distance = Math.Round(distanceMm, 3),
                    intersects = intersects,
                    point_1 = new { x = Math.Round(ptA.X * FeetToMm, 3), y = Math.Round(ptA.Y * FeetToMm, 3), z = Math.Round(ptA.Z * FeetToMm, 3) },
                    point_2 = new { x = Math.Round(ptB.X * FeetToMm, 3), y = Math.Round(ptB.Y * FeetToMm, 3), z = Math.Round(ptB.Z * FeetToMm, 3) },
                    bbox_1 = bbox1 != null ? FormatBbox(bbox1) : null,
                    bbox_2 = bbox2 != null ? FormatBbox(bbox2) : null,
                    limitations = limitations,
                    error = (string)null
                });
            }
            else
            {
                return CommandResult.Fail($"Unknown strategy '{strategyStr}'. Supported values: bbox, location, solid_bbox_prefilter.");
            }
        }

        private static object FormatBbox(BoundingBoxXYZ bbox)
        {
            if (bbox == null) return null;
            return new
            {
                min = new { x = Math.Round(bbox.Min.X * FeetToMm, 3), y = Math.Round(bbox.Min.Y * FeetToMm, 3), z = Math.Round(bbox.Min.Z * FeetToMm, 3) },
                max = new { x = Math.Round(bbox.Max.X * FeetToMm, 3), y = Math.Round(bbox.Max.Y * FeetToMm, 3), z = Math.Round(bbox.Max.Z * FeetToMm, 3) }
            };
        }

        private static double DistanceBetweenBoxes(BoundingBoxXYZ boxA, BoundingBoxXYZ boxB, out XYZ ptA, out XYZ ptB)
        {
            double ptAx, ptAy, ptAz;
            double ptBx, ptBy, ptBz;

            // X
            if (boxA.Max.X < boxB.Min.X)
            {
                ptAx = boxA.Max.X;
                ptBx = boxB.Min.X;
            }
            else if (boxB.Max.X < boxA.Min.X)
            {
                ptAx = boxA.Min.X;
                ptBx = boxB.Max.X;
            }
            else
            {
                double mid = 0.5 * (Math.Max(boxA.Min.X, boxB.Min.X) + Math.Min(boxA.Max.X, boxB.Max.X));
                ptAx = mid;
                ptBx = mid;
            }

            // Y
            if (boxA.Max.Y < boxB.Min.Y)
            {
                ptAy = boxA.Max.Y;
                ptBy = boxB.Min.Y;
            }
            else if (boxB.Max.Y < boxA.Min.Y)
            {
                ptAy = boxA.Min.Y;
                ptBy = boxB.Max.Y;
            }
            else
            {
                double mid = 0.5 * (Math.Max(boxA.Min.Y, boxB.Min.Y) + Math.Min(boxA.Max.Y, boxB.Max.Y));
                ptAy = mid;
                ptBy = mid;
            }

            // Z
            if (boxA.Max.Z < boxB.Min.Z)
            {
                ptAz = boxA.Max.Z;
                ptBz = boxB.Min.Z;
            }
            else if (boxB.Max.Z < boxA.Min.Z)
            {
                ptAz = boxA.Min.Z;
                ptBz = boxB.Max.Z;
            }
            else
            {
                double mid = 0.5 * (Math.Max(boxA.Min.Z, boxB.Min.Z) + Math.Min(boxA.Max.Z, boxB.Max.Z));
                ptAz = mid;
                ptBz = mid;
            }

            ptA = new XYZ(ptAx, ptAy, ptAz);
            ptB = new XYZ(ptBx, ptBy, ptBz);
            return ptA.DistanceTo(ptB);
        }

        private static bool TryMeasureLocationDistance(Element e1, Element e2, out double distance, out XYZ ptA, out XYZ ptB, out string error)
        {
            distance = 0;
            ptA = null;
            ptB = null;
            error = null;

            Location loc1 = e1.Location;
            Location loc2 = e2.Location;

            if (loc1 == null || loc2 == null)
            {
                error = "One or both elements do not expose a Location property.";
                return false;
            }

            XYZ p1 = null;
            Curve c1 = null;
            if (loc1 is LocationPoint lp1) p1 = lp1.Point;
            else if (loc1 is LocationCurve lc1) c1 = lc1.Curve;

            XYZ p2 = null;
            Curve c2 = null;
            if (loc2 is LocationPoint lp2) p2 = lp2.Point;
            else if (loc2 is LocationCurve lc2) c2 = lc2.Curve;

            if (p1 != null && p2 != null)
            {
                ptA = p1;
                ptB = p2;
                distance = p1.DistanceTo(p2);
                return true;
            }
            else if (p1 != null && c2 != null)
            {
                ptA = p1;
                IntersectionResult result = c2.Project(p1);
                if (result != null)
                {
                    ptB = result.XYZPoint;
                    distance = p1.DistanceTo(ptB);
                    return true;
                }
                // Fallback to endpoints
                var start = c2.GetEndPoint(0);
                var end = c2.GetEndPoint(1);
                double dStart = p1.DistanceTo(start);
                double dEnd = p1.DistanceTo(end);
                if (dStart < dEnd) { ptB = start; distance = dStart; }
                else { ptB = end; distance = dEnd; }
                return true;
            }
            else if (c1 != null && p2 != null)
            {
                ptB = p2;
                IntersectionResult result = c1.Project(p2);
                if (result != null)
                {
                    ptA = result.XYZPoint;
                    distance = ptA.DistanceTo(p2);
                    return true;
                }
                // Fallback to endpoints
                var start = c1.GetEndPoint(0);
                var end = c1.GetEndPoint(1);
                double dStart = p2.DistanceTo(start);
                double dEnd = p2.DistanceTo(end);
                if (dStart < dEnd) { ptA = start; distance = dStart; }
                else { ptA = end; distance = dEnd; }
                return true;
            }
            else if (c1 != null && c2 != null)
            {
                var s1 = c1.GetEndPoint(0);
                var e1pt = c1.GetEndPoint(1);
                var s2 = c2.GetEndPoint(0);
                var e2pt = c2.GetEndPoint(1);

                double minDist = double.MaxValue;

                // Project c1 endpoints onto c2
                IntersectionResult r1_s = c2.Project(s1);
                if (r1_s != null && s1.DistanceTo(r1_s.XYZPoint) < minDist)
                {
                    minDist = s1.DistanceTo(r1_s.XYZPoint);
                    ptA = s1; ptB = r1_s.XYZPoint;
                }
                IntersectionResult r1_e = c2.Project(e1pt);
                if (r1_e != null && e1pt.DistanceTo(r1_e.XYZPoint) < minDist)
                {
                    minDist = e1pt.DistanceTo(r1_e.XYZPoint);
                    ptA = e1pt; ptB = r1_e.XYZPoint;
                }

                // Project c2 endpoints onto c1
                IntersectionResult r2_s = c1.Project(s2);
                if (r2_s != null && s2.DistanceTo(r2_s.XYZPoint) < minDist)
                {
                    minDist = s2.DistanceTo(r2_s.XYZPoint);
                    ptB = s2; ptA = r2_s.XYZPoint;
                }
                IntersectionResult r2_e = c1.Project(e2pt);
                if (r2_e != null && e2pt.DistanceTo(r2_e.XYZPoint) < minDist)
                {
                    minDist = e2pt.DistanceTo(r2_e.XYZPoint);
                    ptB = e2pt; ptA = r2_e.XYZPoint;
                }

                // Endpoint-endpoint matches
                double d_s1s2 = s1.DistanceTo(s2);
                if (d_s1s2 < minDist) { minDist = d_s1s2; ptA = s1; ptB = s2; }
                double d_s1e2 = s1.DistanceTo(e2pt);
                if (d_s1e2 < minDist) { minDist = d_s1e2; ptA = s1; ptB = e2pt; }
                double d_e1s2 = e1pt.DistanceTo(s2);
                if (d_e1s2 < minDist) { minDist = d_e1s2; ptA = e1pt; ptB = s2; }
                double d_e1e2 = e1pt.DistanceTo(e2pt);
                if (d_e1e2 < minDist) { minDist = d_e1e2; ptA = e1pt; ptB = e2pt; }

                distance = minDist;
                return true;
            }

            error = "Unsupported location geometry types.";
            return false;
        }
    }
}
