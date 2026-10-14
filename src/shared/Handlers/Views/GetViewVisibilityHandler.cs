using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class GetViewVisibilityHandler : IRevitCommand
    {
        public string Name => "get_view_visibility";

        public string Description =>
            "Report a view's visibility/graphics state: hidden categories, applied view filters, " +
            "detail level, discipline, scale, view template, and whether graphics overrides are allowed. " +
            "Read-only. Optional include_category_list lists every model category with its hidden state.";

        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""view_id"":{""type"":""integer"",""description"":""If omitted, active view.""},
    ""include_category_list"":{""type"":""boolean"",""default"":false,""description"":""If true, list every model category with its hidden state (large).""}
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
            bool includeCategoryList = request.Value<bool?>("include_category_list") ?? false;

            // ----- Resolve the view (active when omitted) -----
            View view;
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

                view = targetElement as View;
                if (view == null)
                    return CommandResult.Fail("Element " + viewIdParam.Value + " is not a View.");
            }
            else
            {
                view = doc.ActiveView;
                if (view == null)
                    return CommandResult.Fail("No active view.");
            }

            // ----- Core view metadata -----
            string detailLevel = "Coarse";
            try { detailLevel = view.DetailLevel.ToString(); } catch { }

            string discipline = null;
            try { discipline = view.Discipline.ToString(); } catch { }

            int scale = 0;
            try { scale = view.Scale; } catch { }

            bool graphicsOverridesAllowed = false;
            try { graphicsOverridesAllowed = view.AreGraphicsOverridesAllowed(); } catch { }

            // ----- View template -----
            string viewTemplateId = null;
            string viewTemplateName = null;
            try
            {
                var templateId = view.ViewTemplateId;
                if (templateId != null && templateId != ElementId.InvalidElementId)
                {
                    viewTemplateId = RevitCompat.GetId(templateId).ToString();
                    var templateElement = doc.GetElement(templateId);
                    if (templateElement != null)
                        viewTemplateName = templateElement.Name;
                }
            }
            catch { }

            // ----- Applied view filters -----
            var appliedFilters = new List<object>();
            try
            {
                var filterIds = view.GetFilters();
                if (filterIds != null)
                {
                    foreach (var filterId in filterIds)
                    {
                        string filterName = null;
                        try
                        {
                            var filterElement = doc.GetElement(filterId);
                            if (filterElement != null)
                                filterName = filterElement.Name;
                        }
                        catch { }

                        bool? visible = null;
                        try { visible = view.GetFilterVisibility(filterId); }
                        catch { }

                        appliedFilters.Add(new
                        {
                            id = RevitCompat.GetId(filterId).ToString(),
                            name = filterName,
                            visible = visible
                        });
                    }
                }
            }
            catch
            {
                // View type does not support filters.
            }

            // ----- Categories: hidden state -----
            var hiddenCategories = new List<string>();
            var allCategories = new List<object>();

            try
            {
                var categories = doc.Settings.Categories;
                if (categories != null)
                {
                    foreach (Category category in categories)
                    {
                        if (category == null)
                            continue;

                        try
                        {
                            // Only model categories with visibility control on this view.
                            if (category.CategoryType != CategoryType.Model)
                                continue;
                            if (!category.get_AllowsVisibilityControl(view))
                                continue;

                            bool hidden = view.GetCategoryHidden(category.Id);

                            if (hidden)
                                hiddenCategories.Add(category.Name);

                            if (includeCategoryList)
                            {
                                allCategories.Add(new
                                {
                                    name = category.Name,
                                    hidden = hidden
                                });
                            }
                        }
                        catch
                        {
                            // Some categories throw on certain view types; skip them.
                        }
                    }
                }
            }
            catch { }

            hiddenCategories.Sort(StringComparer.OrdinalIgnoreCase);

            return CommandResult.Ok(new
            {
                view_id = RevitCompat.GetId(view.Id).ToString(),
                view_name = view.Name,
                view_type = view.ViewType.ToString(),
                detail_level = detailLevel,
                discipline = discipline,
                scale = scale,
                graphics_overrides_allowed = graphicsOverridesAllowed,
                view_template_id = viewTemplateId,
                view_template_name = viewTemplateName,
                applied_filter_count = appliedFilters.Count,
                applied_filters = appliedFilters.ToArray(),
                hidden_category_count = hiddenCategories.Count,
                hidden_categories = hiddenCategories.ToArray(),
                categories = includeCategoryList ? allCategories.ToArray() : null
            });
        }
    }
}
