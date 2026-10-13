using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class TagElementsHandler : IRevitCommand
    {
        public string Name => "tag_elements";
        public string Description => "Tag one or more elements in a view.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""element_ids""],
  ""properties"": {
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""minItems"": 1 },
    ""view_id"": { ""type"": ""integer"" },
    ""tag_type_id"": { ""type"": ""integer"" },
    ""orientation"": { ""type"": ""string"", ""enum"": [""Horizontal"", ""Vertical""], ""default"": ""Horizontal"" },
    ""leader"": { ""type"": ""boolean"", ""default"": false },
    ""offset_x"": { ""type"": ""number"", ""default"": 0 },
    ""offset_y"": { ""type"": ""number"", ""default"": 0 }
  }
}";

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
            catch (JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            // Parse inputs
            var elementIdsToken = request["element_ids"];
            if (elementIdsToken == null || elementIdsToken.Type != JTokenType.Array)
                return CommandResult.Fail("element_ids array is required.");

            var elementIds = new List<long>();
            foreach (var token in elementIdsToken)
            {
                if (token.Type == JTokenType.Integer)
                {
                    elementIds.Add(token.Value<long>());
                }
            }

            if (elementIds.Count == 0)
                return CommandResult.Fail("element_ids must contain at least one element ID.");

            long? viewId = request.Value<long?>("view_id");
            long? tagTypeId = request.Value<long?>("tag_type_id");
            string orientation = request.Value<string>("orientation") ?? "Horizontal";
            bool leader = request.Value<bool>("leader");
            double offsetX = request.Value<double>("offset_x"); // mm
            double offsetY = request.Value<double>("offset_y"); // mm

            // Resolve View
            View view = null;
            if (viewId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(viewId.Value))
                    return CommandResult.Fail("view_id " + RevitCompat.ElementIdRangeError(viewId.Value));

                view = doc.GetElement(RevitCompat.ToElementId(viewId.Value)) as View;
                if (view == null)
                    return CommandResult.Fail("View ID " + viewId.Value + " does not resolve to a View.");
            }
            else
            {
                view = doc.ActiveView;
            }

            if (view == null)
                return CommandResult.Fail("No target view could be resolved.");

            if (!EnsureViewCanHostAnnotation(view, out var viewError))
                return CommandResult.Fail(viewError);

            // Resolve Tag Type
            FamilySymbol tagType = null;
            if (tagTypeId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(tagTypeId.Value))
                    return CommandResult.Fail("tag_type_id " + RevitCompat.ElementIdRangeError(tagTypeId.Value));

                tagType = doc.GetElement(RevitCompat.ToElementId(tagTypeId.Value)) as FamilySymbol;
                if (tagType == null)
                    return CommandResult.Fail("Tag type ID " + tagTypeId.Value + " does not resolve to a FamilySymbol.");
            }

            // Get visible elements in the view to verify visibility
            var visibleIds = new HashSet<long>();
            try
            {
                var visibleElems = new FilteredElementCollector(doc, view.Id)
                    .WhereElementIsNotElementType()
                    .ToElementIds();
                foreach (var id in visibleElems)
                {
                    visibleIds.Add(RevitCompat.GetId(id));
                }
            }
            catch { }

            var createdTags = new JArray();
            var failedElements = new JArray();
            int createdCount = 0;

            using (var tx = new Transaction(doc, "RvtMcp: tag elements"))
            {
                tx.Start();
                try
                {
                    if (tagType != null && !tagType.IsActive)
                    {
                        tagType.Activate();
                        doc.Regenerate();
                    }

                    var tagOrientation = TagOrientation.Horizontal;
                    if (string.Equals(orientation, "Vertical", StringComparison.OrdinalIgnoreCase))
                    {
                        tagOrientation = TagOrientation.Vertical;
                    }

                    var right = view.RightDirection;
                    var up = view.UpDirection;
                    var offset = (right * (offsetX / 304.8)) + (up * (offsetY / 304.8));

                    foreach (var elemId in elementIds)
                    {
                        if (!RevitCompat.CanRepresentElementId(elemId))
                        {
                            failedElements.Add(new JObject
                            {
                                ["element_id"] = elemId,
                                ["error"] = RevitCompat.ElementIdRangeError(elemId)
                            });
                            continue;
                        }

                        var elem = doc.GetElement(RevitCompat.ToElementId(elemId));
                        if (elem == null)
                        {
                            failedElements.Add(new JObject
                            {
                                ["element_id"] = elemId,
                                ["error"] = "Element not found."
                            });
                            continue;
                        }

                        if (!visibleIds.Contains(elemId))
                        {
                            failedElements.Add(new JObject
                            {
                                ["element_id"] = elemId,
                                ["error"] = "Element is not visible in target view."
                            });
                            continue;
                        }

                        try
                        {
                            // Determine tag location
                            XYZ basePoint = XYZ.Zero;
                            if (elem.Location is LocationPoint lp)
                            {
                                basePoint = lp.Point;
                            }
                            else if (elem.Location is LocationCurve lc)
                            {
                                basePoint = lc.Curve.Evaluate(0.5, true);
                            }
                            else
                            {
                                var bbox = elem.get_BoundingBox(view) ?? elem.get_BoundingBox(null);
                                if (bbox != null)
                                {
                                    basePoint = (bbox.Min + bbox.Max) * 0.5;
                                }
                            }

                            var tagPoint = basePoint + offset;
                            Element tag;
                            if (elem is Room room)
                            {
                                if (!(view is ViewPlan)) throw new ArgumentException("Room tags require a plan view.");
                                if (tagType != null && RevitCompat.GetId(tagType.Category.Id) != (long)BuiltInCategory.OST_RoomTags)
                                    throw new ArgumentException("A room requires a Room Tags type.");
                                var roomTag = doc.Create.NewRoomTag(new LinkElementId(room.Id), new UV(tagPoint.X, tagPoint.Y), view.Id);
                                roomTag.HasLeader = leader;
                                roomTag.TagOrientation = tagOrientation == TagOrientation.Vertical
                                    ? SpatialElementTagOrientation.Vertical : SpatialElementTagOrientation.Horizontal;
                                tag = roomTag;
                            }
                            else
                            {
                                var independentTag = IndependentTag.Create(doc, view.Id, new Reference(elem), leader, TagMode.TM_ADDBY_CATEGORY, tagOrientation, tagPoint);
                                tag = independentTag;
                            }
                            if (tag == null)
                            {
                                failedElements.Add(new JObject
                                {
                                    ["element_id"] = elemId,
                                    ["error"] = "IndependentTag.Create returned null."
                                });
                                continue;
                            }

                            if (tagType != null)
                            {
                                tag.ChangeTypeId(tagType.Id);
                            }

                            var headPosition = tag is RoomTag resolvedRoomTag
                                ? resolvedRoomTag.TagHeadPosition : ((IndependentTag)tag).TagHeadPosition;

                            createdCount++;
                            var resolvedTagType = doc.GetElement(tag.GetTypeId()) as FamilySymbol;

                            createdTags.Add(new JObject
                            {
                                ["tag_id"] = RevitCompat.GetId(tag.Id),
                                ["element_id"] = elemId,
                                ["tag_type_id"] = RevitCompat.GetId(tag.GetTypeId()),
                                ["tag_type_name"] = resolvedTagType?.Name ?? "Default",
                                ["head_position"] = new JObject
                                {
                                    ["unit"] = "mm",
                                    ["x"] = Math.Round(headPosition.X * 304.8, 1),
                                    ["y"] = Math.Round(headPosition.Y * 304.8, 1),
                                    ["z"] = Math.Round(headPosition.Z * 304.8, 1)
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                            failedElements.Add(new JObject
                            {
                                ["element_id"] = elemId,
                                ["error"] = ex.Message
                            });
                        }
                    }

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail("Tag elements transaction did not commit. Status: " + status);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Transaction aborted: " + ex.Message);
                }
            }

            var result = new JObject
            {
                ["tagged"] = createdCount > 0,
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["view_name"] = view.Name,
                ["requested"] = elementIds.Count,
                ["created"] = createdCount,
                ["tags"] = createdTags,
                ["failed"] = failedElements,
                ["error"] = null
            };
            if (createdCount == 0 && failedElements.Count > 0)
                return new CommandResult { Success = false, Error = "No elements could be tagged. See failed for individual errors.", Data = result };
            return CommandResult.Ok(result);
        }

        private static bool EnsureViewCanHostAnnotation(View view, out string error)
        {
            error = null;
            if (view == null)
            {
                error = "Target view is null.";
                return false;
            }
            if (view.IsTemplate)
            {
                error = "Cannot add annotations to a view template.";
                return false;
            }
            var vt = view.ViewType;
            if (vt == ViewType.Schedule || vt == ViewType.ColumnSchedule || vt == ViewType.ProjectBrowser || vt == ViewType.SystemBrowser || vt == ViewType.Legend)
            {
                error = "View type '" + vt + "' does not support annotations.";
                return false;
            }
            return true;
        }
    }
}
