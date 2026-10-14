using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ComputeElementVolumeHandler : IRevitCommand
    {
        public string Name => "compute_element_volume";
        public string Description => "Compute the geometric solid volume of one or more elements in cubic meters (m3).";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""element_ids""],
  ""properties"": {
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""minItems"": 1 },
    ""detail_level"": { ""type"": ""string"", ""enum"": [""Coarse"", ""Medium"", ""Fine""], ""default"": ""Medium"" }
  }
}";

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
                return CommandResult.Fail("Limit exceeded: compute_element_volume accepts a maximum of 500 element IDs. Narrow the query to continue.");

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
            double totalVolumeM3 = 0.0;

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
                double volumeFeet = 0;
                var limitations = new List<string>();

                try
                {
                    var geom = elem.get_Geometry(options);
                    if (geom != null)
                    {
                        volumeFeet = SumSolidsVolume(geom, ref solidCount);
                    }
                    else
                    {
                        limitations.Add("No geometry available under the selected view detail level options.");
                    }
                }
                catch (Exception ex)
                {
                    failed.Add(new { element_id = id, error = "Geometry volume computation failed: " + ex.Message });
                    continue;
                }

                if (solidCount == 0 || volumeFeet <= 1e-9)
                {
                    limitations.Add("No valid non-empty solid geometry found for this element.");
                }

                double volumeM3 = Math.Round(volumeFeet * CubicFeetToCubicMeters, 6);
                totalVolumeM3 += volumeM3;

                elementsData.Add(new
                {
                    element_id = id,
                    name = elem.Name ?? string.Empty,
                    category = elem.Category?.Name ?? string.Empty,
                    solid_count = solidCount,
                    volume_m3 = volumeM3,
                    source = "solid_geometry",
                    limitations = limitations
                });
            }

            return CommandResult.Ok(new
            {
                unit = "m3",
                requested = elementIdsList.Count,
                returned = elementsData.Count,
                elements = elementsData,
                total_volume_m3 = Math.Round(totalVolumeM3, 6),
                failed = failed,
                error = (string)null
            });
        }

        private static double SumSolidsVolume(GeometryObject geomObj, ref int solidCount)
        {
            if (geomObj == null) return 0.0;

            double volume = 0.0;
            if (geomObj is Solid solid)
            {
                if (solid.Volume > 1e-9 && solid.Faces.Size > 0)
                {
                    solidCount++;
                    volume += solid.Volume;
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
                            volume += SumSolidsVolume(nested, ref solidCount);
                        }
                    }
                }
                catch {}
            }
            else if (geomObj is GeometryElement geomElem)
            {
                foreach (GeometryObject obj in geomElem)
                {
                    volume += SumSolidsVolume(obj, ref solidCount);
                }
            }
            return volume;
        }
    }
}
