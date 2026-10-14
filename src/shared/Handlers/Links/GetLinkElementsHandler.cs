using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class GetLinkElementsHandler : IRevitCommand
    {
        private const double FeetToMm = 304.8;

        public string Name => "get_link_elements";

        public string Description => "Read elements from a linked Revit document through a link instance. Reads are strictly read-only.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""link_instance_id""],
  ""properties"": {
    ""link_instance_id"": { ""type"": ""integer"" },
    ""category"": { ""type"": ""string"" },
    ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 5000 },
    ""include_bounding_box"": { ""type"": ""boolean"" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            long linkInstanceId = 0;
            string category = "";
            int limit = 500;
            bool includeBoundingBox = false;

            try
            {
                var request = JObject.Parse(paramsJson);
                linkInstanceId = request.Value<long>("link_instance_id");
                if (request["category"] != null)
                    category = request.Value<string>("category") ?? "";
                if (request["limit"] != null)
                    limit = request.Value<int>("limit");
                if (request["include_bounding_box"] != null)
                    includeBoundingBox = request.Value<bool>("include_bounding_box");
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            if (limit < 1 || limit > 5000)
                return CommandResult.Fail("limit must be between 1 and 5000.");

            if (!RevitCompat.CanRepresentElementId(linkInstanceId))
                return CommandResult.Fail("link_instance_id " + RevitCompat.ElementIdRangeError(linkInstanceId));

            var instanceId = RevitCompat.ToElementId(linkInstanceId);
            var linkInstance = doc.GetElement(instanceId) as RevitLinkInstance;
            if (linkInstance == null)
                return CommandResult.Fail($"Revit link instance with ID {linkInstanceId} not found.");

            var linkDoc = linkInstance.GetLinkDocument();
            if (linkDoc == null)
                return CommandResult.Fail($"The link instance {linkInstanceId} is unloaded or its document is unavailable.");

            var warnings = new List<string>();

            // Setup collector in the linked document. Read-only context, no transaction!
            var collector = new FilteredElementCollector(linkDoc)
                .WhereElementIsNotElementType();

            BuiltInCategory? matchedBic = null;
            if (!string.IsNullOrEmpty(category))
            {
                foreach (BuiltInCategory bic in Enum.GetValues(typeof(BuiltInCategory)))
                {
                    try
                    {
                        var c = Category.GetCategory(linkDoc, bic);
                        if (c != null && c.Name.Equals(category, StringComparison.OrdinalIgnoreCase))
                        {
                            matchedBic = bic;
                            break;
                        }
                    }
                    catch { }
                }

                if (matchedBic.HasValue)
                {
                    collector = collector.OfCategory(matchedBic.Value);
                }
            }

            List<Element> elements;
            try
            {
                elements = collector.ToList();
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"Failed to collect elements from linked document: {ex.Message}");
            }

            // If we couldn't match a BuiltInCategory but a category filter was requested, filter manually
            if (!string.IsNullOrEmpty(category) && !matchedBic.HasValue)
            {
                elements = elements
                    .Where(el => el.Category != null && el.Category.Name.Equals(category, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (elements.Count == 0)
                {
                    warnings.Add($"Category '{category}' did not match any BuiltInCategory and no matching elements were found.");
                }
            }

            int total = elements.Count;
            var returnedElements = elements.Take(limit).ToList();

            Transform totalTransform = Transform.Identity;
            try
            {
                totalTransform = linkInstance.GetTotalTransform();
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not retrieve total transform for link instance: {ex.Message}");
            }

            var elementsList = new List<object>();

            foreach (var element in returnedElements)
            {
                string elCategory = "";
                try
                {
                    elCategory = element.Category?.Name ?? "";
                }
                catch { }

                string familyName = "";
                try
                {
                    var fi = element as FamilyInstance;
                    if (fi != null && fi.Symbol != null)
                    {
                        familyName = fi.Symbol.FamilyName;
                    }
                }
                catch { }

                string typeName = "";
                try
                {
                    var typeId = element.GetTypeId();
                    if (typeId != ElementId.InvalidElementId)
                    {
                        var typeElement = linkDoc.GetElement(typeId);
                        if (typeElement != null)
                            typeName = typeElement.Name;
                    }
                }
                catch { }

                object hostBbox = null;
                if (includeBoundingBox)
                {
                    try
                    {
                        var bbox = element.get_BoundingBox(null);
                        if (bbox != null)
                        {
                            XYZ hostMin = totalTransform.OfPoint(bbox.Min);
                            XYZ hostMax = totalTransform.OfPoint(bbox.Max);

                            double minX = Math.Min(hostMin.X, hostMax.X);
                            double minY = Math.Min(hostMin.Y, hostMax.Y);
                            double minZ = Math.Min(hostMin.Z, hostMax.Z);
                            double maxX = Math.Max(hostMin.X, hostMax.X);
                            double maxY = Math.Max(hostMin.Y, hostMax.Y);
                            double maxZ = Math.Max(hostMin.Z, hostMax.Z);

                            hostBbox = new
                            {
                                min = new { x_mm = Math.Round(minX * FeetToMm, 3), y_mm = Math.Round(minY * FeetToMm, 3), z_mm = Math.Round(minZ * FeetToMm, 3) },
                                max = new { x_mm = Math.Round(maxX * FeetToMm, 3), y_mm = Math.Round(maxY * FeetToMm, 3), z_mm = Math.Round(maxZ * FeetToMm, 3) }
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Could not transform bounding box for element {RevitCompat.GetId(element.Id)}: {ex.Message}");
                    }
                }

                elementsList.Add(new
                {
                    linked_element_id = RevitCompat.GetId(element.Id),
                    linked_unique_id = element.UniqueId,
                    name = element.Name,
                    category = elCategory,
                    family = familyName,
                    type = typeName,
                    host_bbox = hostBbox
                });
            }

            return CommandResult.Ok(new
            {
                link_instance = new
                {
                    element_id = RevitCompat.GetId(linkInstance.Id),
                    name = linkInstance.Name,
                    type_id = RevitCompat.GetId(linkInstance.GetTypeId())
                },
                linked_document = new
                {
                    title = linkDoc.Title,
                    path = linkDoc.PathName,
                    is_read_only_context = true
                },
                transform_to_host = new
                {
                    origin = new
                    {
                        x_mm = Math.Round(totalTransform.Origin.X * FeetToMm, 3),
                        y_mm = Math.Round(totalTransform.Origin.Y * FeetToMm, 3),
                        z_mm = Math.Round(totalTransform.Origin.Z * FeetToMm, 3)
                    }
                },
                total = total,
                returned = elementsList.Count,
                elements = elementsList,
                read_limitations = new[]
                {
                    "Linked documents are read-only in this tool and must not be saved, closed, or modified."
                },
                warnings = warnings
            });
        }
    }
}
