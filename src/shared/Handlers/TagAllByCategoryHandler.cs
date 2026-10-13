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
    public class TagAllByCategoryHandler : IRevitCommand
    {
        public string Name => "tag_all_by_category";
        public string Description => "Tag all elements of a specific category in a view.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""category""],
  ""properties"": {
    ""category"": { ""type"": ""string"" },
    ""view_id"": { ""type"": ""integer"" },
    ""tag_type_id"": { ""type"": ""integer"" },
    ""skip_existing"": { ""type"": ""boolean"", ""default"": true },
    ""leader"": { ""type"": ""boolean"", ""default"": false },
    ""dry_run"": { ""type"": ""boolean"", ""default"": false },
    ""limit"": { ""type"": ""integer"", ""default"": 200, ""maximum"": 500 }
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

            var categoryInput = request.Value<string>("category");
            if (string.IsNullOrWhiteSpace(categoryInput))
                return CommandResult.Fail("category is required.");

            long? viewId = request.Value<long?>("view_id");
            long? tagTypeId = request.Value<long?>("tag_type_id");
            bool skipExisting = request["skip_existing"] == null || request.Value<bool>("skip_existing");
            bool leader = request.Value<bool>("leader");
            bool dryRun = request.Value<bool>("dry_run");
            int limit = request["limit"] == null ? 200 : request.Value<int>("limit");

            if (limit > 500)
                return CommandResult.Fail("limit cannot exceed 500.");

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

            // Resolve Category
            var cat = ResolveCategory(doc, categoryInput);
            if (cat == null)
                return CommandResult.Fail("Category '" + categoryInput + "' could not be resolved.");
            var rooms = RevitCompat.GetId(cat.Id) == (long)BuiltInCategory.OST_Rooms;
            if (rooms && !(view is ViewPlan))
                return CommandResult.Fail("Room tags require a plan view.");

            // Resolve Tag Type if specified
            FamilySymbol tagType = null;
            if (tagTypeId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(tagTypeId.Value))
                    return CommandResult.Fail("tag_type_id " + RevitCompat.ElementIdRangeError(tagTypeId.Value));

                tagType = doc.GetElement(RevitCompat.ToElementId(tagTypeId.Value)) as FamilySymbol;
                if (tagType == null)
                    return CommandResult.Fail("Tag type ID " + tagTypeId.Value + " does not resolve to a FamilySymbol.");
            }
            if (rooms && tagType != null && (tagType.Category == null
                || RevitCompat.GetId(tagType.Category.Id) != (long)BuiltInCategory.OST_RoomTags))
                return CommandResult.Fail("A room requires a Room Tags type.");

            // Collect all visible elements of this category in the view
            List<Element> elements;
            try
            {
                elements = new FilteredElementCollector(doc, view.Id)
                    .OfCategoryId(cat.Id)
                    .WhereElementIsNotElementType()
                    .Where(e => !(e is Room room) || room.Area > 0)
                    .ToList();
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Failed to collect elements of category: " + ex.Message);
            }

            // Find existing tags in this view
            var taggedElementIds = new HashSet<long>();
            int alreadyTaggedCount = 0;
            try
            {
                if (rooms)
                {
                    foreach (var tag in new FilteredElementCollector(doc, view.Id)
                        .OfCategory(BuiltInCategory.OST_RoomTags).WhereElementIsNotElementType().Cast<RoomTag>()
                        .Where(t => !t.IsTaggingLink && t.Room != null))
                        taggedElementIds.Add(RevitCompat.GetId(tag.Room.Id));
                }
                else
                {
                    var tagsInView = new FilteredElementCollector(doc, view.Id)
                        .OfClass(typeof(IndependentTag))
                        .Cast<IndependentTag>();

                    foreach (var tag in tagsInView)
                    {
                        var taggedIds = GetTaggedElementIds(tag);
                        foreach (var tid in taggedIds)
                        {
                            taggedElementIds.Add(tid);
                        }
                    }
                }
            }
            catch { }

            // Filter candidates
            var candidates = new List<Element>();
            foreach (var elem in elements)
            {
                var elemId = RevitCompat.GetId(elem.Id);
                if (taggedElementIds.Contains(elemId))
                {
                    alreadyTaggedCount++;
                    if (skipExisting)
                    {
                        continue;
                    }
                }
                candidates.Add(elem);
            }

            bool truncated = false;
            if (candidates.Count > limit)
            {
                candidates = candidates.Take(limit).ToList();
                truncated = true;
            }

            var createdTags = new JArray();
            var failedElements = new JArray();
            int createdCount = 0;

            if (dryRun)
            {
                // In dry run, just report candidates that would be tagged
                foreach (var cand in candidates)
                {
                    createdTags.Add(new JObject
                    {
                        ["tag_id"] = -1,
                        ["element_id"] = RevitCompat.GetId(cand.Id)
                    });
                }
            }
            else
            {
                using (var tx = new Transaction(doc, "RvtMcp: tag all by category"))
                {
                    tx.Start();
                    try
                    {
                        if (tagType != null && !tagType.IsActive)
                        {
                            tagType.Activate();
                            doc.Regenerate();
                        }

                        var right = view.RightDirection;
                        var up = view.UpDirection;
                        // Default offset: 500mm in right/up direction depending on element type
                        var offset = (right * (500.0 / 304.8)) + (up * (500.0 / 304.8));

                        foreach (var elem in candidates)
                        {
                            var elemId = RevitCompat.GetId(elem.Id);
                            try
                            {
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
                                    var roomTag = doc.Create.NewRoomTag(new LinkElementId(room.Id), new UV(tagPoint.X, tagPoint.Y), view.Id);
                                    roomTag.HasLeader = leader;
                                    tag = roomTag;
                                }
                                else
                                    tag = IndependentTag.Create(doc, view.Id, new Reference(elem), leader, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, tagPoint);
                                if (tag != null)
                                {
                                    if (tagType != null)
                                    {
                                        tag.ChangeTypeId(tagType.Id);
                                    }

                                    createdCount++;
                                    createdTags.Add(new JObject
                                    {
                                        ["tag_id"] = RevitCompat.GetId(tag.Id),
                                        ["element_id"] = elemId
                                    });
                                }
                                else
                                {
                                    failedElements.Add(new JObject
                                    {
                                        ["element_id"] = elemId,
                                        ["error"] = "IndependentTag.Create returned null."
                                    });
                                }
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
                            return CommandResult.Fail("Tag all by category transaction did not commit. Status: " + status);
                    }
                    catch (Exception ex)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        return CommandResult.Fail("Transaction aborted: " + ex.Message);
                    }
                }
            }

            var result = new JObject
            {
                ["dry_run"] = dryRun,
                ["category"] = cat.Name,
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["view_name"] = view.Name,
                ["visible_count"] = elements.Count,
                ["already_tagged"] = alreadyTaggedCount,
                ["candidate_count"] = candidates.Count,
                ["created"] = createdCount,
                ["tags"] = createdTags,
                ["failed"] = failedElements,
                ["truncated"] = truncated,
                ["error"] = null
            };
            if (dryRun) result["would_create"] = candidates.Count;
            if (!dryRun && createdCount == 0 && failedElements.Count > 0)
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

        private static Category ResolveCategory(Document doc, string categoryName)
        {
            if (string.IsNullOrWhiteSpace(categoryName)) return null;

            foreach (Category cat in doc.Settings.Categories)
            {
                if (string.Equals(cat.Name, categoryName, StringComparison.OrdinalIgnoreCase))
                    return cat;
            }

            if (Enum.TryParse<BuiltInCategory>(categoryName, true, out var bic))
            {
                try { return Category.GetCategory(doc, bic); } catch { }
            }

            foreach (BuiltInCategory value in Enum.GetValues(typeof(BuiltInCategory)))
            {
                var name = value.ToString();
                if (name.Equals(categoryName, StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(categoryName, StringComparison.OrdinalIgnoreCase) ||
                    name.Replace("OST_", "").Equals(categoryName, StringComparison.OrdinalIgnoreCase))
                {
                    try { return Category.GetCategory(doc, value); } catch { }
                }
            }

            return null;
        }

        private static HashSet<long> GetTaggedElementIds(IndependentTag tag)
        {
            var ids = new HashSet<long>();
            if (tag == null) return ids;

            // 1. Try GetTaggedElementIds() (Revit 2022+)
            try
            {
                var method = tag.GetType().GetMethod("GetTaggedElementIds");
                if (method != null)
                {
                    var refs = method.Invoke(tag, null) as System.Collections.IEnumerable;
                    if (refs != null)
                    {
                        foreach (var linkId in refs)
                        {
                            var hostIdProp = linkId.GetType().GetProperty("HostElementId");
                            if (hostIdProp != null)
                            {
                                var hostId = hostIdProp.GetValue(linkId) as ElementId;
                                if (hostId != null && hostId != ElementId.InvalidElementId)
                                {
                                    ids.Add(RevitCompat.GetId(hostId));
                                }
                            }
                        }
                        if (ids.Count > 0) return ids;
                    }
                }
            }
            catch { }

            // 2. Try TaggedLocalElementId (Revit 2021 and older)
            try
            {
                var prop = tag.GetType().GetProperty("TaggedLocalElementId");
                if (prop != null)
                {
                    var val = prop.GetValue(tag) as ElementId;
                    if (val != null && val != ElementId.InvalidElementId)
                    {
                        ids.Add(RevitCompat.GetId(val));
                        return ids;
                    }
                }
            }
            catch { }

            // 3. Fallback: Try GetTaggedLocalElementIds() or direct fields
            try
            {
                var method = tag.GetType().GetMethod("GetTaggedLocalElementIds");
                if (method != null)
                {
                    var localIds = method.Invoke(tag, null) as ICollection<ElementId>;
                    if (localIds != null)
                    {
                        foreach (var lid in localIds)
                        {
                            if (lid != ElementId.InvalidElementId)
                                ids.Add(RevitCompat.GetId(lid));
                        }
                        return ids;
                    }
                }
            }
            catch { }

            return ids;
        }
    }
}
