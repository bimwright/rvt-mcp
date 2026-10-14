using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ComputeElementAreaHandler : IRevitCommand
    {
        public string Name => "compute_element_area";
        public string Description => "Compute the total face area of one or more elements in square meters (m2).";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""element_ids""],
  ""properties"": {
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""minItems"": 1 },
    ""detail_level"": { ""type"": ""string"", ""enum"": [""Coarse"", ""Medium"", ""Fine""], ""default"": ""Medium"" }
  }
}";

        private const double SquareFeetToSquareMeters = 0.09290304;

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
                return CommandResult.Fail("Limit exceeded: compute_element_area accepts a maximum of 500 element IDs. Narrow the query to continue.");

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

            var options = new Options
            {
                DetailLevel = detailLevelEnum,
                ComputeReferences = false,
                IncludeNonVisibleObjects = false
            };

            var elementsData = new List<dynamic>();
            var failed = new List<object>();
            double totalAreaM2 = 0.0;

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

                int solidCount = 0;
                int faceCount = 0;
                double areaFeet = 0.0;
                var limitations = new List<string>();

                try
                {
                    var geom = elem.get_Geometry(options);
                    if (geom != null)
                    {
                        areaFeet = SumSolidsArea(geom, ref solidCount, ref faceCount);
                    }
                    else
                    {
                        limitations.Add("No geometry available under the selected view detail level options.");
                    }
                }
                catch (Exception ex)
                {
                    failed.Add(new { element_id = id, error = "Geometry area computation failed: " + ex.Message });
                    continue;
                }

                if (solidCount == 0 || faceCount == 0 || areaFeet <= 1e-9)
                {
                    limitations.Add("No valid solid faces geometry found for this element.");
                }

                double areaM2 = Math.Round(areaFeet * SquareFeetToSquareMeters, 6);
                totalAreaM2 += areaM2;

                elementsData.Add(new
                {
                    element_id = id,
                    name = elem.Name ?? string.Empty,
                    category = elem.Category?.Name ?? string.Empty,
                    solid_count = solidCount,
                    face_count = faceCount,
                    area_m2 = areaM2,
                    source = "solid_faces",
                    limitations = limitations
                });
            }

            return CommandResult.Ok(new
            {
                unit = "m2",
                requested = elementIdsList.Count,
                returned = elementsData.Count,
                elements = elementsData,
                total_area_m2 = Math.Round(totalAreaM2, 6),
                failed = failed,
                error = (string)null
            });
        }

        private static double SumSolidsArea(GeometryObject geomObj, ref int solidCount, ref int faceCount)
        {
            if (geomObj == null) return 0.0;

            double area = 0.0;
            if (geomObj is Solid solid)
            {
                if (solid.Faces.Size > 0)
                {
                    solidCount++;
                    foreach (Face face in solid.Faces)
                    {
                        if (face.Area > 1e-9)
                        {
                            faceCount++;
                            area += face.Area;
                        }
                    }
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
                            area += SumSolidsArea(nested, ref solidCount, ref faceCount);
                        }
                    }
                }
                catch {}
            }
            else if (geomObj is GeometryElement geomElem)
            {
                foreach (GeometryObject obj in geomElem)
                {
                    area += SumSolidsArea(obj, ref solidCount, ref faceCount);
                }
            }
            return area;
        }
    }
}
