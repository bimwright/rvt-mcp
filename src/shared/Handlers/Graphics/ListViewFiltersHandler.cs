using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ListViewFiltersHandler : IRevitCommand
    {
        public string Name => "list_view_filters";

        public string Description =>
            "List all ParameterFilterElement (view filter) definitions in the document, including the " +
            "categories each filter targets. Optional: view_id to list only filters applied to a single " +
            "view (with override summary); include_usage to map every filter to the views it is applied to.";

        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""view_id"":{""type"":""integer"",""description"":""If supplied, only list filters applied to this view, with their override summary.""},
    ""include_usage"":{""type"":""boolean"",""default"":false,""description"":""If true (and view_id omitted), list which views each filter is applied to.""}
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
                request = string.IsNullOrWhiteSpace(paramsJson)
                    ? new JObject()
                    : JObject.Parse(paramsJson);
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail("Invalid JSON parameters: " + ex.Message);
            }

            long? viewIdParam = request.Value<long?>("view_id");
            bool includeUsage = request.Value<bool?>("include_usage") ?? false;

            // All filter definitions in the document.
            var filterElements = new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .ToList();

            // ----- view_id branch: only filters applied to one specific view -----
            if (viewIdParam.HasValue)
            {
                Element targetElement;
                try
                {
                    targetElement = doc.GetElement(RevitCompat.ToElementId(viewIdParam.Value));
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    return CommandResult.Fail(ex.Message);
                }

                var targetView = targetElement as View;
                if (targetView == null)
                    return CommandResult.Fail("Element " + viewIdParam.Value + " is not a View.");

                ICollection<ElementId> appliedFilterIds;
                try
                {
                    appliedFilterIds = targetView.GetFilters();
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail("View does not support filters: " + ex.Message);
                }

                var appliedSet = new HashSet<long>(appliedFilterIds.Select(RevitCompat.GetId));
                var filterById = filterElements.ToDictionary(f => RevitCompat.GetId(f.Id), f => f);

                var viewResults = new List<object>();
                foreach (var filterId in appliedFilterIds)
                {
                    ParameterFilterElement pfe;
                    if (!filterById.TryGetValue(RevitCompat.GetId(filterId), out pfe) || pfe == null)
                        continue;

                    var categories = ResolveCategoryNames(doc, pfe);

                    bool? visible = null;
                    bool? enabled = null;
                    int overriddenSettings = 0;
                    try { visible = targetView.GetFilterVisibility(filterId); } catch { }
                    try
                    {
                        var ovr = targetView.GetFilterOverrides(filterId);
                        if (ovr != null)
                            overriddenSettings = CountOverrides(ovr);
                    }
                    catch { }
                    try { enabled = targetView.GetIsFilterEnabled(filterId); } catch { }

                    viewResults.Add(new
                    {
                        id = RevitCompat.GetId(pfe.Id).ToString(),
                        name = pfe.Name,
                        category_count = categories.Count,
                        categories = categories.ToArray(),
                        visible = visible,
                        enabled = enabled,
                        overridden_setting_count = overriddenSettings
                    });
                }

                return CommandResult.Ok(new
                {
                    view_id = viewIdParam.Value,
                    view_name = targetView.Name,
                    total_filters = viewResults.Count,
                    filters = viewResults.ToArray()
                });
            }

            // ----- usage map (only when include_usage and no view_id) -----
            Dictionary<long, List<string>> usageByFilterId = null;
            if (includeUsage)
            {
                usageByFilterId = new Dictionary<long, List<string>>();
                var views = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .ToList();

                foreach (var view in views)
                {
                    ICollection<ElementId> viewFilterIds;
                    try
                    {
                        viewFilterIds = view.GetFilters();
                    }
                    catch
                    {
                        // View type does not support filters.
                        continue;
                    }

                    if (viewFilterIds == null || viewFilterIds.Count == 0)
                        continue;

                    foreach (var fId in viewFilterIds)
                    {
                        var key = RevitCompat.GetId(fId);
                        List<string> list;
                        if (!usageByFilterId.TryGetValue(key, out list))
                        {
                            list = new List<string>();
                            usageByFilterId[key] = list;
                        }
                        list.Add(view.Name);
                    }
                }
            }

            // ----- default branch: all filter definitions -----
            var sortedFilters = filterElements
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var results = new List<object>();
            foreach (var pfe in sortedFilters)
            {
                try
                {
                    var categories = ResolveCategoryNames(doc, pfe);

                    List<string> appliedViews = null;
                    int appliedViewCount = 0;
                    if (usageByFilterId != null)
                    {
                        if (usageByFilterId.TryGetValue(RevitCompat.GetId(pfe.Id), out appliedViews))
                        {
                            appliedViews.Sort(StringComparer.OrdinalIgnoreCase);
                            appliedViewCount = appliedViews.Count;
                        }
                        else
                        {
                            appliedViews = new List<string>();
                        }
                    }

                    results.Add(new
                    {
                        id = RevitCompat.GetId(pfe.Id).ToString(),
                        name = pfe.Name,
                        category_count = categories.Count,
                        categories = categories.ToArray(),
                        applied_to_view_count = appliedViewCount,
                        applied_to_views = appliedViews == null ? null : appliedViews.ToArray()
                    });
                }
                catch
                {
                    // Skip filters that fail introspection.
                }
            }

            return CommandResult.Ok(new
            {
                total_filters = results.Count,
                include_usage = includeUsage,
                filters = results.ToArray()
            });
        }

        /// <summary>Resolve the category ids targeted by a filter to readable category names.</summary>
        private static List<string> ResolveCategoryNames(Document doc, ParameterFilterElement pfe)
        {
            var names = new List<string>();
            ICollection<ElementId> categoryIds;
            try
            {
                categoryIds = pfe.GetCategories();
            }
            catch
            {
                return names;
            }

            if (categoryIds == null)
                return names;

            foreach (var catId in categoryIds)
            {
                string name = null;
                try
                {
                    var cat = Category.GetCategory(doc, catId);
                    if (cat != null)
                        name = cat.Name;
                }
                catch { }

                if (string.IsNullOrEmpty(name))
                {
                    try
                    {
                        name = Enum.IsDefined(typeof(BuiltInCategory), (int)RevitCompat.GetId(catId))
                            ? ((BuiltInCategory)(int)RevitCompat.GetId(catId)).ToString()
                            : "<category " + RevitCompat.GetId(catId) + ">";
                    }
                    catch
                    {
                        name = "<category " + RevitCompat.GetId(catId) + ">";
                    }
                }

                names.Add(name);
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>Count the non-default override settings present in an OverrideGraphicSettings.</summary>
        private static int CountOverrides(OverrideGraphicSettings ovr)
        {
            int count = 0;
            try { if (ovr.ProjectionLineColor != null && ovr.ProjectionLineColor.IsValid) count++; } catch { }
            try { if (ovr.CutLineColor != null && ovr.CutLineColor.IsValid) count++; } catch { }
            try { if (ovr.SurfaceForegroundPatternColor != null && ovr.SurfaceForegroundPatternColor.IsValid) count++; } catch { }
            try { if (ovr.SurfaceBackgroundPatternColor != null && ovr.SurfaceBackgroundPatternColor.IsValid) count++; } catch { }
            try { if (ovr.CutForegroundPatternColor != null && ovr.CutForegroundPatternColor.IsValid) count++; } catch { }
            try { if (ovr.CutBackgroundPatternColor != null && ovr.CutBackgroundPatternColor.IsValid) count++; } catch { }
            try { if (ovr.ProjectionLineWeight > 0) count++; } catch { }
            try { if (ovr.CutLineWeight > 0) count++; } catch { }
            try { if (ovr.Transparency > 0) count++; } catch { }
            try { if (ovr.Halftone) count++; } catch { }
            try { if (ovr.DetailLevel != ViewDetailLevel.Undefined) count++; } catch { }
            return count;
        }
    }
}
