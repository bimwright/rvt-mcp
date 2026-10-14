using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class GetElementGeometryHandler : IRevitCommand
    {
        public string Name => "get_element_geometry";
        public string Description => "Get geometric details (counts of solids, faces, edges, instances, meshes, vertex count) and optional vertex samples for one or more elements.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""element_ids""],
  ""properties"": {
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""minItems"": 1 },
    ""detail_level"": { ""type"": ""string"", ""enum"": [""Coarse"", ""Medium"", ""Fine""], ""default"": ""Medium"" },
    ""include_samples"": { ""type"": ""boolean"", ""default"": false },
    ""sample_limit"": { ""type"": ""integer"", ""default"": 20, ""minimum"": 0, ""maximum"": 50 }
  }
}";

        private const double FeetToMm = 304.8;

        private struct GeometryCounts
        {
            public int SolidCount;
            public int NonEmptySolidCount;
            public int FaceCount;
            public int EdgeCount;
            public int CurveCount;
            public int MeshCount;
            public int InstanceCount;
            public List<object> VertexSamples;
        }

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

            if (elementIdsList.Count > 100)
                return CommandResult.Fail("Limit exceeded: get_element_geometry accepts a maximum of 100 element IDs by default (hard limit 500). Narrow the query to continue.");

            var detailLevelStr = (request["detail_level"] ?? request["detailLevel"])?.Value<string>() ?? "Medium";
            ViewDetailLevel detailLevelEnum = ViewDetailLevel.Medium;
            if (detailLevelStr.Equals("Coarse", StringComparison.OrdinalIgnoreCase))
                detailLevelEnum = ViewDetailLevel.Coarse;
            else if (detailLevelStr.Equals("Medium", StringComparison.OrdinalIgnoreCase))
                detailLevelEnum = ViewDetailLevel.Medium;
            else if (detailLevelStr.Equals("Fine", StringComparison.OrdinalIgnoreCase))
                detailLevelEnum = ViewDetailLevel.Fine;
            else
                return CommandResult.Fail("detail_level must be one of: Coarse, Medium, Fine.");

            var includeSamplesToken = request["include_samples"] ?? request["includeSamples"];
            bool includeSamples = includeSamplesToken != null && includeSamplesToken.Value<bool>();

            var sampleLimitToken = request["sample_limit"] ?? request["sampleLimit"];
            int sampleLimit = sampleLimitToken != null ? sampleLimitToken.Value<int>() : 20;
            if (sampleLimit < 0)
                return CommandResult.Fail("sample_limit must be at least 0.");
            if (sampleLimit > 50)
                return CommandResult.Fail("sample_limit must be 50 or less.");

            var results = new List<object>();
            var failed = new List<object>();

            var options = new Options
            {
                DetailLevel = detailLevelEnum,
                ComputeReferences = false,
                IncludeNonVisibleObjects = false
            };

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

                // Get bounding box
                object bboxData = null;
                try
                {
                    var bbox = elem.get_BoundingBox(null);
                    if (bbox != null)
                    {
                        var minMm = new { x = Math.Round(bbox.Min.X * FeetToMm, 3), y = Math.Round(bbox.Min.Y * FeetToMm, 3), z = Math.Round(bbox.Min.Z * FeetToMm, 3) };
                        var maxMm = new { x = Math.Round(bbox.Max.X * FeetToMm, 3), y = Math.Round(bbox.Max.Y * FeetToMm, 3), z = Math.Round(bbox.Max.Z * FeetToMm, 3) };
                        var centerMm = new
                        {
                            x = Math.Round(0.5 * (bbox.Min.X + bbox.Max.X) * FeetToMm, 3),
                            y = Math.Round(0.5 * (bbox.Min.Y + bbox.Max.Y) * FeetToMm, 3),
                            z = Math.Round(0.5 * (bbox.Min.Z + bbox.Max.Z) * FeetToMm, 3)
                        };
                        var sizeMm = new
                        {
                            x = Math.Round((bbox.Max.X - bbox.Min.X) * FeetToMm, 3),
                            y = Math.Round((bbox.Max.Y - bbox.Min.Y) * FeetToMm, 3),
                            z = Math.Round((bbox.Max.Z - bbox.Min.Z) * FeetToMm, 3)
                        };
                        bboxData = new { min = minMm, max = maxMm, center = centerMm, size = sizeMm };
                    }
                }
                catch {}

                // Extract geometry
                GeometryCounts counts = new GeometryCounts
                {
                    VertexSamples = new List<object>()
                };

                var limitations = new List<string>();

                try
                {
                    var geomElem = elem.get_Geometry(options);
                    if (geomElem == null)
                    {
                        limitations.Add("No geometry visible or available under the specified detail level options.");
                    }
                    else
                    {
                        foreach (GeometryObject geomObj in geomElem)
                        {
                            TraverseGeometry(geomObj, ref counts, includeSamples, sampleLimit, limitations);
                        }
                    }
                }
                catch (Exception ex)
                {
                    failed.Add(new { element_id = id, error = "Geometry extraction failed: " + ex.Message });
                    continue;
                }

                results.Add(new
                {
                    element_id = id,
                    name = elem.Name ?? string.Empty,
                    category = elem.Category?.Name ?? string.Empty,
                    unit = "mm",
                    bounding_box = bboxData,
                    geometry = new
                    {
                        solid_count = counts.SolidCount,
                        non_empty_solid_count = counts.NonEmptySolidCount,
                        face_count = counts.FaceCount,
                        edge_count = counts.EdgeCount,
                        curve_count = counts.CurveCount,
                        mesh_count = counts.MeshCount,
                        instance_count = counts.InstanceCount,
                        vertex_sample_count = counts.VertexSamples.Count,
                        vertex_samples = includeSamples ? counts.VertexSamples : null
                    },
                    limitations = limitations
                });
            }

            return CommandResult.Ok(new
            {
                requested = elementIdsList.Count,
                returned = results.Count,
                detail_level = detailLevelStr,
                results = results,
                failed = failed,
                truncated = false,
                error = (string)null
            });
        }

        private void TraverseGeometry(GeometryObject geomObj, ref GeometryCounts counts, bool includeSamples, int sampleLimit, List<string> limitations)
        {
            if (geomObj == null) return;

            if (geomObj is Solid solid)
            {
                counts.SolidCount++;
                if (solid.Volume > 1e-9)
                {
                    counts.NonEmptySolidCount++;
                }

                try
                {
                    var faces = solid.Faces;
                    if (faces != null)
                    {
                        counts.FaceCount += faces.Size;
                    }
                }
                catch {}

                try
                {
                    var edges = solid.Edges;
                    if (edges != null)
                    {
                        counts.EdgeCount += edges.Size;
                        if (includeSamples && counts.VertexSamples.Count < sampleLimit)
                        {
                            foreach (Edge edge in edges)
                            {
                                if (counts.VertexSamples.Count >= sampleLimit) break;
                                var pts = edge.Tessellate();
                                if (pts != null)
                                {
                                    foreach (XYZ pt in pts)
                                    {
                                        if (counts.VertexSamples.Count >= sampleLimit) break;
                                        counts.VertexSamples.Add(new { x = Math.Round(pt.X * FeetToMm, 3), y = Math.Round(pt.Y * FeetToMm, 3), z = Math.Round(pt.Z * FeetToMm, 3) });
                                    }
                                }
                            }
                        }
                    }
                }
                catch {}
            }
            else if (geomObj is GeometryInstance instance)
            {
                counts.InstanceCount++;
                try
                {
                    // For instance geometries, traverse both instance geometry
                    var instGeom = instance.GetInstanceGeometry();
                    if (instGeom != null)
                    {
                        foreach (GeometryObject nestedObj in instGeom)
                        {
                            TraverseGeometry(nestedObj, ref counts, includeSamples, sampleLimit, limitations);
                        }
                    }
                }
                catch (Exception ex)
                {
                    limitations.Add("Failed to traverse instanced geometry: " + ex.Message);
                }
            }
            else if (geomObj is Mesh mesh)
            {
                counts.MeshCount++;
                if (includeSamples && counts.VertexSamples.Count < sampleLimit)
                {
                    try
                    {
                        var vertices = mesh.Vertices;
                        if (vertices != null)
                        {
                            foreach (XYZ pt in vertices)
                            {
                                if (counts.VertexSamples.Count >= sampleLimit) break;
                                counts.VertexSamples.Add(new { x = Math.Round(pt.X * FeetToMm, 3), y = Math.Round(pt.Y * FeetToMm, 3), z = Math.Round(pt.Z * FeetToMm, 3) });
                            }
                        }
                    }
                    catch {}
                }
            }
            else if (geomObj is Curve curve)
            {
                counts.CurveCount++;
                if (includeSamples && counts.VertexSamples.Count < sampleLimit)
                {
                    try
                    {
                        var pts = curve.Tessellate();
                        if (pts != null)
                        {
                            foreach (XYZ pt in pts)
                            {
                                if (counts.VertexSamples.Count >= sampleLimit) break;
                                counts.VertexSamples.Add(new { x = Math.Round(pt.X * FeetToMm, 3), y = Math.Round(pt.Y * FeetToMm, 3), z = Math.Round(pt.Z * FeetToMm, 3) });
                            }
                        }
                    }
                    catch {}
                }
            }
        }
    }
}
