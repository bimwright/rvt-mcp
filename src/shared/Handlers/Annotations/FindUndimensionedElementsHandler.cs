using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class FindUndimensionedElementsHandler : IRevitCommand
    {
        public string Name => "find_undimensioned_elements";
        public string Description => "Find all elements in the target view of a given category that are not dimensioned.";
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

            // Collect dimensioned element IDs
            var dimensionedElementIds = new HashSet<long>();
            try
            {
                var dimensions = new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(Dimension))
                    .Cast<Dimension>();
                foreach (var dim in dimensions)
                {
                    foreach (var id in GetDimensionedElementIds(dim))
                    {
                        dimensionedElementIds.Add(id);
                    }
                }
            }
            catch { }

            // Subtract dimensioned from visible
            var undimensioned = new List<Element>();
            int dimensionedCount = 0;

            foreach (var elem in visibleElements)
            {
                var id = RevitCompat.GetId(elem.Id);
                if (dimensionedElementIds.Contains(id))
                {
                    dimensionedCount++;
                }
                else
                {
                    undimensioned.Add(elem);
                }
            }

            bool truncated = undimensioned.Count > limit;
            var finalUndimensioned = undimensioned.Take(limit).ToList();

            var undimensionedArray = new JArray();
            foreach (var elem in finalUndimensioned)
            {
                undimensionedArray.Add(new JObject
                {
                    ["element_id"] = RevitCompat.GetId(elem.Id),
                    ["name"] = elem.Name,
                    ["category"] = cat.Name
                });
            }

            var warnings = new List<string> { "Only dimensions with resolvable references in the target view are considered." };

            return CommandResult.Ok(new JObject
            {
                ["category"] = cat.Name,
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["view_name"] = view.Name,
                ["visible_count"] = visibleElements.Count,
                ["dimensioned_count"] = dimensionedCount,
                ["undimensioned_count"] = undimensioned.Count,
                ["returned"] = finalUndimensioned.Count,
                ["limit"] = limit,
                ["truncated"] = truncated,
                ["undimensioned"] = undimensionedArray,
                ["warnings"] = JArray.FromObject(warnings),
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

        private static HashSet<long> GetDimensionedElementIds(Dimension dim)
        {
            var ids = new HashSet<long>();
            if (dim == null) return ids;

            try
            {
                var refs = dim.References;
                if (refs != null)
                {
                    foreach (Reference r in refs)
                    {
                        if (r != null)
                        {
                            var hostId = r.ElementId;
                            if (hostId != null && hostId != ElementId.InvalidElementId)
                            {
                                ids.Add(RevitCompat.GetId(hostId));
                            }
                        }
                    }
                    return ids;
                }
            }
            catch { }

            try
            {
                var prop = dim.GetType().GetProperty("References");
                if (prop != null)
                {
                    var refs = prop.GetValue(dim) as System.Collections.IEnumerable;
                    if (refs != null)
                    {
                        foreach (var r in refs)
                        {
                            var elemIdProp = r.GetType().GetProperty("ElementId");
                            if (elemIdProp != null)
                            {
                                var hostId = elemIdProp.GetValue(r) as ElementId;
                                if (hostId != null && hostId != ElementId.InvalidElementId)
                                    ids.Add(RevitCompat.GetId(hostId));
                            }
                        }
                        if (ids.Count > 0) return ids;
                    }
                }
            }
            catch { }

            return ids;
        }
    }
}
