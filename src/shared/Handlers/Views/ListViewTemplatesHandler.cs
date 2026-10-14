using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ListViewTemplatesHandler : IRevitCommand
    {
        public string Name => "list_view_templates";
        public string Description => "List view templates with compatibility and controlled settings metadata.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""viewType"": {
      ""type"": ""string"",
      ""description"": ""Optional ViewType name filter such as FloorPlan, CeilingPlan, ThreeD, Section, DraftingView.""
    },
    ""viewId"": {
      ""type"": ""integer"",
      ""description"": ""Optional target view id. When supplied, compatibility is checked with View.IsValidViewTemplate.""
    },
    ""includeSettings"": { ""type"": ""boolean"", ""default"": true },
    ""includeUsage"": { ""type"": ""boolean"", ""default"": false },
    ""limit"": { ""type"": ""integer"", ""default"": 500, ""minimum"": 1, ""maximum"": 2000 }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            var viewTypeFilter = request.Value<string>("viewType");
            var viewIdInput = request.Value<long?>("viewId");
            var includeSettings = request.Value<bool?>("includeSettings") ?? true;
            var includeUsage = request.Value<bool?>("includeUsage") ?? false;
            var limit = request.Value<int?>("limit") ?? 500;

            if (limit < 1 || limit > 2000)
                return CommandResult.Fail("limit must be between 1 and 2000.");

            View targetView = null;
            if (viewIdInput.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(viewIdInput.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(viewIdInput.Value));

                targetView = doc.GetElement(RevitCompat.ToElementId(viewIdInput.Value)) as View;
                if (targetView == null || targetView.IsTemplate)
                    return CommandResult.Fail($"Element {viewIdInput.Value} is not a valid non-template View.");
            }

            var templates = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => v.IsTemplate)
                .ToList();

            var resultTemplates = new List<object>();

            // Collect all views once if we need to query usage
            List<View> nonTemplates = null;
            if (includeUsage)
            {
                nonTemplates = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => !v.IsTemplate)
                    .ToList();
            }

            foreach (var template in templates)
            {
                if (template == null) continue;

                if (!string.IsNullOrEmpty(viewTypeFilter) &&
                    !string.Equals(template.ViewType.ToString(), viewTypeFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (resultTemplates.Count >= limit) break;

                bool? isCompatibleWithTarget = null;
                if (targetView != null)
                {
                    isCompatibleWithTarget = targetView.IsValidViewTemplate(template.Id);
                }

                List<object> controlledSettingsList = null;
                List<object> nonControlledSettingsList = null;
                int controlledCount = 0;
                int nonControlledCount = 0;

                if (includeSettings)
                {
                    try
                    {
                        var nonControlledIds = template.GetNonControlledTemplateParameterIds();
                        var allParamIds = template.GetTemplateParameterIds();

                        var controlledIds = allParamIds.Where(id => !nonControlledIds.Any(ncId => RevitCompat.GetId(ncId) == RevitCompat.GetId(id))).ToList();

                        controlledCount = controlledIds.Count;
                        nonControlledCount = nonControlledIds.Count;

                        controlledSettingsList = new List<object>();
                        foreach (var pId in controlledIds)
                        {
                            var info = ResolveParameterInfo(template, pId);
                            controlledSettingsList.Add(info);
                        }

                        nonControlledSettingsList = new List<object>();
                        foreach (var pId in nonControlledIds)
                        {
                            var info = ResolveParameterInfo(template, pId);
                            nonControlledSettingsList.Add(info);
                        }
                    }
                    catch { }
                }

                List<object> appliedToViews = null;
                int appliedCount = 0;

                if (includeUsage && nonTemplates != null)
                {
                    appliedToViews = new List<object>();
                    var matchedViews = nonTemplates.Where(v => RevitCompat.GetId(v.ViewTemplateId) == RevitCompat.GetId(template.Id)).ToList();
                    appliedCount = matchedViews.Count;

                    foreach (var mv in matchedViews)
                    {
                        appliedToViews.Add(new
                        {
                            viewId = RevitCompat.GetId(mv.Id),
                            name = mv.Name,
                            viewType = mv.ViewType.ToString()
                        });
                    }
                }

                resultTemplates.Add(new
                {
                    templateId = RevitCompat.GetId(template.Id),
                    name = template.Name,
                    viewType = template.ViewType.ToString(),
                    isCompatibleWithTarget = isCompatibleWithTarget,
                    controlledSettingCount = controlledCount,
                    nonControlledSettingCount = nonControlledCount,
                    controlledSettings = controlledSettingsList?.OrderBy(s => ((dynamic)s).name ?? string.Empty).ToArray(),
                    nonControlledSettings = nonControlledSettingsList?.OrderBy(s => ((dynamic)s).name ?? string.Empty).ToArray(),
                    appliedToViewCount = appliedCount,
                    appliedToViews = appliedToViews?.OrderBy(v => ((dynamic)v).name ?? string.Empty).ToArray()
                });
            }

            return CommandResult.Ok(new
            {
                count = templates.Count,
                returned = resultTemplates.Count,
                targetViewId = targetView != null ? (long?)RevitCompat.GetId(targetView.Id) : null,
                targetViewName = targetView?.Name,
                templates = resultTemplates.OrderBy(t => ((dynamic)t).name).ToArray()
            });
        }

        private static Parameter GetParameterById(View template, ElementId pId)
        {
            var pIdVal = RevitCompat.GetId(pId);
            if (pIdVal < 0)
            {
                try
                {
                    return template.get_Parameter((BuiltInParameter)pIdVal);
                }
                catch { }
            }
            else
            {
                foreach (Parameter p in template.Parameters)
                {
                    if (p != null)
                    {
                        try
                        {
                            if (RevitCompat.GetId(p.Id) == pIdVal)
                                return p;
                        }
                        catch { }
                    }
                }
            }
            return null;
        }

        private static object ResolveParameterInfo(View template, ElementId pId)
        {
            var pIdVal = RevitCompat.GetId(pId);
            string paramName = null;
            string builtInParameter = null;

            try
            {
                var param = GetParameterById(template, pId);
                if (param != null)
                {
                    paramName = param.Definition.Name;
                }
            }
            catch { }

            if (pIdVal < 0)
            {
                try
                {
                    builtInParameter = ((BuiltInParameter)pIdVal).ToString();
                }
                catch { }
            }

            return new
            {
                id = pIdVal,
                name = paramName,
                builtInParameter = builtInParameter
            };
        }
    }
}
