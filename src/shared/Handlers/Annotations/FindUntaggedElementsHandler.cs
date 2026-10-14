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
    public class FindUntaggedElementsHandler : IRevitCommand
    {
        public string Name => "find_untagged_elements";
        public string Description => "Find all elements in the target view of a given category that do not have tags.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""category""],
  ""properties"": {
    ""category"": { ""type"": ""string"" },
    ""view_id"": { ""type"": ""integer"" },
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

            // Collect visible elements in the view
            var visibleElements = new List<Element>();
            try
            {
                visibleElements = new FilteredElementCollector(doc, view.Id)
                    .OfCategoryId(cat.Id)
                    .WhereElementIsNotElementType()
                    .ToList();
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Failed to collect visible elements: " + ex.Message);
            }

            // Collect tagged IDs in the view
            var taggedElementIds = new HashSet<long>();

            // 1. Collect standard IndependentTag tagged elements
            try
            {
                var tags = new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(IndependentTag))
                    .Cast<IndependentTag>();
                foreach (var tag in tags)
                {
                    foreach (var tid in GetTaggedElementIds(tag))
                    {
                        taggedElementIds.Add(tid);
                    }
                }
            }
            catch { }

            // 2. Collect RoomTag tagged elements (for OST_Rooms)
            try
            {
                var roomTags = new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(RoomTag))
                    .Cast<RoomTag>();
                foreach (var rt in roomTags)
                {
                    if (rt.Room != null)
                        taggedElementIds.Add(RevitCompat.GetId(rt.Room.Id));
                }
            }
            catch { }

            // 3. Collect AreaTag tagged elements (for OST_Areas)
            try
            {
                var areaTags = new FilteredElementCollector(doc, view.Id)
                    .OfCategory(BuiltInCategory.OST_AreaTags)
                    .WhereElementIsNotElementType();
                foreach (var at in areaTags)
                {
                    var areaProp = at.GetType().GetProperty("Area");
                    if (areaProp != null)
                    {
                        var areaElem = areaProp.GetValue(at) as Element;
                        if (areaElem != null)
                            taggedElementIds.Add(RevitCompat.GetId(areaElem.Id));
                    }
                }
            }
            catch { }

            // Subtract tagged from visible
            var untagged = new List<Element>();
            int taggedCount = 0;

            foreach (var elem in visibleElements)
            {
                var id = RevitCompat.GetId(elem.Id);
                if (taggedElementIds.Contains(id))
                {
                    taggedCount++;
                }
                else
                {
                    untagged.Add(elem);
                }
            }

            bool truncated = untagged.Count > limit;
            var finalUntagged = untagged.Take(limit).ToList();

            var untaggedArray = new JArray();
            foreach (var elem in finalUntagged)
            {
                untaggedArray.Add(new JObject
                {
                    ["element_id"] = RevitCompat.GetId(elem.Id),
                    ["name"] = elem.Name,
                    ["category"] = cat.Name
                });
            }

            return CommandResult.Ok(new JObject
            {
                ["category"] = cat.Name,
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["view_name"] = view.Name,
                ["visible_count"] = visibleElements.Count,
                ["tagged_count"] = taggedCount,
                ["untagged_count"] = untagged.Count,
                ["returned"] = finalUntagged.Count,
                ["limit"] = limit,
                ["truncated"] = truncated,
                ["untagged"] = untaggedArray,
                ["error"] = null
            });
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
