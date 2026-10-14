using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class FindElementsInVolumeHandler : IRevitCommand
    {
        public string Name => "find_elements_in_volume";
        public string Description => "Find elements inside or intersecting an axis-aligned 3D volume or a room's bounding box.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""volume"": {
      ""type"": ""object"",
      ""properties"": {
        ""min"": { ""type"": ""object"" },
        ""max"": { ""type"": ""object"" }
      }
    },
    ""room_id"": { ""type"": ""integer"" },
    ""categories"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""view_id"": { ""type"": ""integer"" },
    ""match"": { ""type"": ""string"", ""enum"": [""inside"", ""intersects""], ""default"": ""intersects"" },
    ""limit"": { ""type"": ""integer"", ""default"": 200, ""maximum"": 500 }
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

            var volumeToken = request["volume"];
            var roomIdToken = request["room_id"] ?? request["roomId"];

            if ((volumeToken == null && roomIdToken == null) || (volumeToken != null && roomIdToken != null))
                return CommandResult.Fail("Exactly one of 'volume' or 'room_id' must be supplied.");

            BoundingBoxXYZ queryBbox = null;
            string sourceStr = "";
            var limitations = new List<string>();

            if (volumeToken != null)
            {
                sourceStr = "axis_aligned_volume";
                var minObj = volumeToken["min"] as JObject;
                var maxObj = volumeToken["max"] as JObject;

                if (minObj == null || maxObj == null)
                    return CommandResult.Fail("volume must contain 'min' and 'max' coordinates.");

                double minX = minObj.Value<double>("x");
                double minY = minObj.Value<double>("y");
                double minZ = minObj.Value<double>("z");

                double maxX = maxObj.Value<double>("x");
                double maxY = maxObj.Value<double>("y");
                double maxZ = maxObj.Value<double>("z");

                if (minX > maxX || minY > maxY || minZ > maxZ)
                    return CommandResult.Fail("Invalid volume bounds: min coordinates cannot be greater than max coordinates.");

                queryBbox = new BoundingBoxXYZ
                {
                    Min = new XYZ(minX / FeetToMm, minY / FeetToMm, minZ / FeetToMm),
                    Max = new XYZ(maxX / FeetToMm, maxY / FeetToMm, maxZ / FeetToMm)
                };
            }
            else
            {
                sourceStr = "room_bounding_box";
                long rId = roomIdToken.Value<long>();
                if (!RevitCompat.CanRepresentElementId(rId))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(rId));

                var room = doc.GetElement(RevitCompat.ToElementId(rId)) as Room;
                if (room == null)
                    return CommandResult.Fail($"Room with ID {rId} was not found.");

                queryBbox = room.get_BoundingBox(null);
                if (queryBbox == null)
                    return CommandResult.Fail($"Bounding box for room {rId} is null or unavailable.");

                limitations.Add("Room mode uses the coarse axis-aligned bounding box of the room, not exact analytical boundaries.");
            }

            var categoriesToken = request["categories"] as JArray;
            List<ElementId> resolvedCatIds = null;
            if (categoriesToken != null && categoriesToken.Count > 0)
            {
                var catNames = categoriesToken.Select(t => t.Value<string>()).Where(s => !string.IsNullOrEmpty(s)).ToList();
                var resolvedCats = ResolveCategories(doc, catNames);
                if (resolvedCats.Count > 0)
                {
                    resolvedCatIds = resolvedCats.Select(c => c.Id).ToList();
                }
            }

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

            var matchType = (request["match"] ?? request["match"])?.Value<string>() ?? "intersects";
            matchType = matchType.ToLowerInvariant();
            if (matchType != "inside" && matchType != "intersects")
                return CommandResult.Fail("match must be 'inside' or 'intersects'.");

            var limitToken = request["limit"];
            int limit = limitToken != null ? limitToken.Value<int>() : 200;
            if (limit > 500)
                return CommandResult.Fail("Limit exceeded: limit cannot exceed the hard maximum of 500.");

            Outline outline = new Outline(queryBbox.Min, queryBbox.Max);
            ElementFilter boxFilter;
            if (matchType == "inside")
            {
                boxFilter = new BoundingBoxIsInsideFilter(outline);
            }
            else
            {
                boxFilter = new BoundingBoxIntersectsFilter(outline);
            }

            FilteredElementCollector collector;
            if (viewObj != null)
            {
                collector = new FilteredElementCollector(doc, viewObj.Id);
            }
            else
            {
                collector = new FilteredElementCollector(doc);
            }

            collector.WherePasses(boxFilter).WhereElementIsNotElementType();

            if (resolvedCatIds != null && resolvedCatIds.Count > 0)
            {
                var catFilter = new ElementMulticategoryFilter(resolvedCatIds);
                collector.WherePasses(catFilter);
            }

            var matchedElements = new List<object>();
            bool truncated = false;
            int totalScanned = 0;

            foreach (var elem in collector)
            {
                totalScanned++;
                if (matchedElements.Count >= limit)
                {
                    truncated = true;
                    continue;
                }

                BoundingBoxXYZ elemBbox = null;
                try
                {
                    elemBbox = elem.get_BoundingBox(viewObj);
                }
                catch {}

                object bboxMm = null;
                if (elemBbox != null)
                {
                    bboxMm = new
                    {
                        min = new { x = Math.Round(elemBbox.Min.X * FeetToMm, 3), y = Math.Round(elemBbox.Min.Y * FeetToMm, 3), z = Math.Round(elemBbox.Min.Z * FeetToMm, 3) },
                        max = new { x = Math.Round(elemBbox.Max.X * FeetToMm, 3), y = Math.Round(elemBbox.Max.Y * FeetToMm, 3), z = Math.Round(elemBbox.Max.Z * FeetToMm, 3) }
                    };
                }

                matchedElements.Add(new
                {
                    element_id = RevitCompat.GetId(elem.Id),
                    name = elem.Name ?? string.Empty,
                    category = elem.Category?.Name ?? string.Empty,
                    bounding_box = bboxMm
                });
            }

            return CommandResult.Ok(new
            {
                unit = "mm",
                source = sourceStr,
                match = matchType,
                volume = new
                {
                    min = new { x = Math.Round(queryBbox.Min.X * FeetToMm, 3), y = Math.Round(queryBbox.Min.Y * FeetToMm, 3), z = Math.Round(queryBbox.Min.Z * FeetToMm, 3) },
                    max = new { x = Math.Round(queryBbox.Max.X * FeetToMm, 3), y = Math.Round(queryBbox.Max.Y * FeetToMm, 3), z = Math.Round(queryBbox.Max.Z * FeetToMm, 3) }
                },
                scanned = totalScanned,
                returned = matchedElements.Count,
                limit = limit,
                truncated = truncated,
                elements = matchedElements,
                failed = new List<object>(),
                limitations = limitations,
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
