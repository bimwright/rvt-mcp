using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ProjectPointOntoFaceHandler : IRevitCommand
    {
        public string Name => "project_point_onto_face";
        public string Description => "Project a 3D point onto a specific face of a Revit element and retrieve the projected point, distance, normal, and UV coordinates.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""element_id"", ""x"", ""y"", ""z""],
  ""properties"": {
    ""element_id"": { ""type"": ""integer"" },
    ""x"": { ""type"": ""number"" },
    ""y"": { ""type"": ""number"" },
    ""z"": { ""type"": ""number"" },
    ""face_index"": { ""type"": ""integer"", ""default"": 0, ""minimum"": 0 },
    ""detail_level"": { ""type"": ""string"", ""enum"": [""Coarse"", ""Medium"", ""Fine""], ""default"": ""Medium"" }
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

            var elemIdToken = request["element_id"] ?? request["elementId"];
            var xToken = request["x"];
            var yToken = request["y"];
            var zToken = request["z"];

            if (elemIdToken == null || elemIdToken.Type != JTokenType.Integer)
                return CommandResult.Fail("element_id is required (integer).");
            if (xToken == null || yToken == null || zToken == null)
                return CommandResult.Fail("x, y, and z are all required (numbers).");

            long id = elemIdToken.Value<long>();
            if (!RevitCompat.CanRepresentElementId(id))
                return CommandResult.Fail(RevitCompat.ElementIdRangeError(id));

            double x = xToken.Value<double>();
            double y = yToken.Value<double>();
            double z = zToken.Value<double>();

            var faceIndexToken = request["face_index"] ?? request["faceIndex"];
            int faceIndex = faceIndexToken != null ? faceIndexToken.Value<int>() : 0;
            if (faceIndex < 0)
                return CommandResult.Fail("face_index must be non-negative.");

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

            Element elem = null;
            try
            {
                elem = doc.GetElement(RevitCompat.ToElementId(id));
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Failed to fetch element: " + ex.Message);
            }

            if (elem == null)
                return CommandResult.Fail($"Element with ID {id} was not found.");

            var options = new Options
            {
                DetailLevel = detailLevelEnum,
                ComputeReferences = false,
                IncludeNonVisibleObjects = false
            };

            List<Face> faces = GetElementFaces(elem, options);
            if (faces.Count == 0)
                return CommandResult.Fail($"No solid faces found for element {id}.");

            if (faceIndex >= faces.Count)
                return CommandResult.Fail($"face_index {faceIndex} is out of range. This element only has {faces.Count} faces (0 to {faces.Count - 1}).");

            Face selectedFace = faces[faceIndex];
            XYZ pointFeet = new XYZ(x / FeetToMm, y / FeetToMm, z / FeetToMm);

            IntersectionResult result = null;
            try
            {
                result = selectedFace.Project(pointFeet);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Projection failed: " + ex.Message);
            }

            if (result == null)
            {
                return CommandResult.Ok(new
                {
                    unit = "mm",
                    element_id = id,
                    input_point = new { x = Math.Round(x, 3), y = Math.Round(y, 3), z = Math.Round(z, 3) },
                    face_index = faceIndex,
                    face_count = faces.Count,
                    projected = false,
                    projected_point = (object)null,
                    distance = (object)null,
                    uv = (object)null,
                    normal = (object)null,
                    error = "Point could not be projected onto the selected face."
                });
            }

            XYZ projectedPt = result.XYZPoint;
            double distanceFeet = pointFeet.DistanceTo(projectedPt);
            UV uv = result.UVPoint;
            XYZ normal = new XYZ(0, 0, 0);

            try
            {
                normal = selectedFace.ComputeNormal(uv);
            }
            catch {}

            return CommandResult.Ok(new
            {
                unit = "mm",
                element_id = id,
                input_point = new { x = Math.Round(x, 3), y = Math.Round(y, 3), z = Math.Round(z, 3) },
                face_index = faceIndex,
                face_count = faces.Count,
                projected = true,
                projected_point = new
                {
                    x = Math.Round(projectedPt.X * FeetToMm, 3),
                    y = Math.Round(projectedPt.Y * FeetToMm, 3),
                    z = Math.Round(projectedPt.Z * FeetToMm, 3)
                },
                distance = Math.Round(distanceFeet * FeetToMm, 3),
                uv = new { u = Math.Round(uv.U, 6), v = Math.Round(uv.V, 6) },
                normal = new { x = Math.Round(normal.X, 6), y = Math.Round(normal.Y, 6), z = Math.Round(normal.Z, 6) },
                error = (string)null
            });
        }

        private static List<Face> GetElementFaces(Element elem, Options options)
        {
            var faces = new List<Face>();
            try
            {
                var geom = elem.get_Geometry(options);
                if (geom != null)
                {
                    ExtractFaces(geom, faces);
                }
            }
            catch {}
            return faces;
        }

        private static void ExtractFaces(GeometryObject geomObj, List<Face> faces)
        {
            if (geomObj == null) return;
            if (geomObj is Solid solid)
            {
                if (solid.Faces.Size > 0)
                {
                    foreach (Face face in solid.Faces)
                    {
                        faces.Add(face);
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
                            ExtractFaces(nested, faces);
                        }
                    }
                }
                catch {}
            }
            else if (geomObj is GeometryElement geomElem)
            {
                foreach (GeometryObject obj in geomElem)
                {
                    ExtractFaces(obj, faces);
                }
            }
        }
    }
}
