using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class GetElementBoundingBoxHandler : IRevitCommand
    {
        public string Name => "get_element_bounding_box";
        public string Description => "Get the bounding box of one or more elements, optionally relative to a view, and optionally including transform data.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""element_ids""],
  ""properties"": {
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""minItems"": 1 },
    ""view_id"": { ""type"": ""integer"", ""description"": ""Optional view-specific bounding box. Omit for model bbox."" },
    ""include_transform"": { ""type"": ""boolean"", ""default"": false }
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
                return CommandResult.Fail("Limit exceeded: get_element_bounding_box accepts a maximum of 500 element IDs. Narrow the query to continue.");

            var viewIdToken = request["view_id"] ?? request["viewId"];
            View view = null;
            long? resolvedViewId = null;
            string resolvedViewName = null;

            if (viewIdToken != null && viewIdToken.Type == JTokenType.Integer)
            {
                var vId = viewIdToken.Value<long>();
                if (!RevitCompat.CanRepresentElementId(vId))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(vId));

                view = doc.GetElement(RevitCompat.ToElementId(vId)) as View;
                if (view == null)
                    return CommandResult.Fail($"No view found with ID {vId}.");

                resolvedViewId = vId;
                resolvedViewName = view.Name;
            }

            var includeTransformToken = request["include_transform"] ?? request["includeTransform"];
            bool includeTransform = includeTransformToken != null && includeTransformToken.Value<bool>();

            var boxes = new List<object>();
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

                BoundingBoxXYZ bbox = null;
                try
                {
                    bbox = elem.get_BoundingBox(view);
                }
                catch (Exception ex)
                {
                    failed.Add(new { element_id = id, error = "Error retrieving bounding box: " + ex.Message });
                    continue;
                }

                if (bbox == null)
                {
                    failed.Add(new { element_id = id, error = "Bounding box is null for this element in the selected view context." });
                    continue;
                }

                try
                {
                    var minFeet = bbox.Min;
                    var maxFeet = bbox.Max;

                    var minMm = new { x = Math.Round(minFeet.X * FeetToMm, 3), y = Math.Round(minFeet.Y * FeetToMm, 3), z = Math.Round(minFeet.Z * FeetToMm, 3) };
                    var maxMm = new { x = Math.Round(maxFeet.X * FeetToMm, 3), y = Math.Round(maxFeet.Y * FeetToMm, 3), z = Math.Round(maxFeet.Z * FeetToMm, 3) };

                    var centerFeet = 0.5 * (minFeet + maxFeet);
                    var centerMm = new { x = Math.Round(centerFeet.X * FeetToMm, 3), y = Math.Round(centerFeet.Y * FeetToMm, 3), z = Math.Round(centerFeet.Z * FeetToMm, 3) };

                    var sizeMm = new
                    {
                        x = Math.Round((maxFeet.X - minFeet.X) * FeetToMm, 3),
                        y = Math.Round((maxFeet.Y - minFeet.Y) * FeetToMm, 3),
                        z = Math.Round((maxFeet.Z - minFeet.Z) * FeetToMm, 3)
                    };

                    object transformData = null;
                    bool hasTransform = false;

                    if (includeTransform && bbox.Transform != null)
                    {
                        hasTransform = true;
                        var t = bbox.Transform;
                        transformData = new
                        {
                            origin = new { x = Math.Round(t.Origin.X * FeetToMm, 3), y = Math.Round(t.Origin.Y * FeetToMm, 3), z = Math.Round(t.Origin.Z * FeetToMm, 3) },
                            basis_x = new { x = Math.Round(t.BasisX.X, 6), y = Math.Round(t.BasisX.Y, 6), z = Math.Round(t.BasisX.Z, 6) },
                            basis_y = new { x = Math.Round(t.BasisY.X, 6), y = Math.Round(t.BasisY.Y, 6), z = Math.Round(t.BasisY.Z, 6) },
                            basis_z = new { x = Math.Round(t.BasisZ.X, 6), y = Math.Round(t.BasisZ.Y, 6), z = Math.Round(t.BasisZ.Z, 6) }
                        };
                    }

                    boxes.Add(new
                    {
                        element_id = id,
                        unique_id = elem.UniqueId,
                        name = elem.Name ?? string.Empty,
                        category = elem.Category?.Name ?? string.Empty,
                        min = minMm,
                        max = maxMm,
                        center = centerMm,
                        size = sizeMm,
                        has_transform = hasTransform,
                        transform = transformData
                    });
                }
                catch (Exception ex)
                {
                    failed.Add(new { element_id = id, error = "Processing geometry failed: " + ex.Message });
                }
            }

            return CommandResult.Ok(new
            {
                unit = "mm",
                requested = elementIdsList.Count,
                returned = boxes.Count,
                view_id = resolvedViewId,
                view_name = resolvedViewName,
                boxes = boxes,
                failed = failed,
                truncated = false,
                error = (string)null
            });
        }
    }
}
