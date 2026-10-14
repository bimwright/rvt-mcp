using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class RaycastFromPointHandler : IRevitCommand
    {
        public string Name => "raycast_from_point";
        public string Description => "Cast a ray from a 3D origin point in a given direction to find the nearest element hit within a 3D view.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""x"", ""y"", ""z"", ""dir_x"", ""dir_y"", ""dir_z"", ""view_3d_id""],
  ""properties"": {
    ""x"": { ""type"": ""number"" },
    ""y"": { ""type"": ""number"" },
    ""z"": { ""type"": ""number"" },
    ""dir_x"": { ""type"": ""number"" },
    ""dir_y"": { ""type"": ""number"" },
    ""dir_z"": { ""type"": ""number"" },
    ""view_3d_id"": { ""type"": ""integer"" },
    ""categories"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""max_distance"": { ""type"": ""number"", ""default"": 100000.0, ""description"": ""Maximum hit distance in mm."" }
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

            var xToken = request["x"];
            var yToken = request["y"];
            var zToken = request["z"];
            var dirXToken = request["dir_x"] ?? request["dirX"];
            var dirYToken = request["dir_y"] ?? request["dirY"];
            var dirZToken = request["dir_z"] ?? request["dirZ"];
            var view3dIdToken = request["view_3d_id"] ?? request["view3DId"];

            if (xToken == null || yToken == null || zToken == null ||
                dirXToken == null || dirYToken == null || dirZToken == null ||
                view3dIdToken == null)
            {
                return CommandResult.Fail("x, y, z, dir_x, dir_y, dir_z, and view_3d_id are all required.");
            }

            double x = xToken.Value<double>();
            double y = yToken.Value<double>();
            double z = zToken.Value<double>();
            double dirX = dirXToken.Value<double>();
            double dirY = dirYToken.Value<double>();
            double dirZ = dirZToken.Value<double>();
            long view3dId = view3dIdToken.Value<long>();

            if (!RevitCompat.CanRepresentElementId(view3dId))
                return CommandResult.Fail(RevitCompat.ElementIdRangeError(view3dId));

            var view3D = doc.GetElement(RevitCompat.ToElementId(view3dId)) as View3D;
            if (view3D == null)
                return CommandResult.Fail($"view_3d_id {view3dId} does not resolve to a View3D element.");

            if (view3D.IsTemplate)
                return CommandResult.Fail("Cannot perform raycasting in a template view.");

            XYZ dir = new XYZ(dirX, dirY, dirZ);
            double dirLen = dir.GetLength();
            if (dirLen < 1e-9)
                return CommandResult.Fail("Direction vector cannot be zero.");

            XYZ dirNormalized = dir.Normalize();

            var maxDistanceToken = request["max_distance"] ?? request["maxDistance"];
            double maxDistanceMm = maxDistanceToken != null ? maxDistanceToken.Value<double>() : 100000.0;
            if (maxDistanceMm <= 0)
                return CommandResult.Fail("max_distance must be a positive number.");

            double maxDistanceFeet = maxDistanceMm / FeetToMm;

            var categoriesToken = request["categories"] as JArray;
            List<ElementId> resolvedCatIds = null;
            if (categoriesToken != null && categoriesToken.Count > 0)
            {
                var catNames = categoriesToken.Select(t => t.Value<string>()).Where(s => !string.IsNullOrEmpty(s)).ToList();
                if (catNames.Count == 0)
                    return CommandResult.Fail("categories must contain at least one non-empty category name when supplied.");

                var resolvedCats = ResolveCategories(doc, catNames);
                if (resolvedCats.Count == 0)
                    return CommandResult.Fail("None of the specified categories could be resolved.");
                if (resolvedCats.Count < catNames.Count)
                    return CommandResult.Fail("One or more specified categories could not be resolved.");

                resolvedCatIds = resolvedCats.Select(c => c.Id).ToList();
            }

            XYZ originFeet = new XYZ(x / FeetToMm, y / FeetToMm, z / FeetToMm);

            ReferenceIntersector intersector;
            if (resolvedCatIds != null && resolvedCatIds.Count > 0)
            {
                var filter = new ElementMulticategoryFilter(resolvedCatIds);
                intersector = new ReferenceIntersector(filter, FindReferenceTarget.Element, view3D);
            }
            else
            {
                intersector = new ReferenceIntersector(view3D);
            }

            ReferenceWithContext nearestHit = null;
            try
            {
                nearestHit = intersector.FindNearest(originFeet, dirNormalized);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Raycasting failed: " + ex.Message);
            }

            if (nearestHit == null || nearestHit.Proximity > maxDistanceFeet)
            {
                return CommandResult.Ok(new
                {
                    unit = "mm",
                    origin = new { x = Math.Round(x, 3), y = Math.Round(y, 3), z = Math.Round(z, 3) },
                    direction = new { x = Math.Round(dirNormalized.X, 6), y = Math.Round(dirNormalized.Y, 6), z = Math.Round(dirNormalized.Z, 6) },
                    view_3d_id = view3dId,
                    hit = false,
                    error = (string)null
                });
            }

            var reference = nearestHit.GetReference();
            var proximityMm = nearestHit.Proximity * FeetToMm;
            var hitPtFeet = reference.GlobalPoint;

            Element hitElem = null;
            try
            {
                hitElem = doc.GetElement(reference.ElementId);
            }
            catch {}

            return CommandResult.Ok(new
            {
                unit = "mm",
                origin = new { x = Math.Round(x, 3), y = Math.Round(y, 3), z = Math.Round(z, 3) },
                direction = new { x = Math.Round(dirNormalized.X, 6), y = Math.Round(dirNormalized.Y, 6), z = Math.Round(dirNormalized.Z, 6) },
                view_3d_id = view3dId,
                hit = true,
                hit_element = hitElem != null ? new
                {
                    element_id = RevitCompat.GetId(hitElem.Id),
                    name = hitElem.Name ?? string.Empty,
                    category = hitElem.Category?.Name ?? string.Empty
                } : null,
                proximity = Math.Round(proximityMm, 3),
                global_point = new
                {
                    x = Math.Round(hitPtFeet.X * FeetToMm, 3),
                    y = Math.Round(hitPtFeet.Y * FeetToMm, 3),
                    z = Math.Round(hitPtFeet.Z * FeetToMm, 3)
                },
                reference_stable_representation = reference.ConvertToStableRepresentation(doc),
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
    }
}
