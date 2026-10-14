using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateViewTemplateFromViewHandler : IRevitCommand
    {
        public string Name => "create_view_template_from_view";
        public string Description => "Create a new view template from an existing view.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""templateName""],
  ""properties"": {
    ""templateName"": { ""type"": ""string"" },
    ""sourceViewId"": {
      ""type"": ""integer"",
      ""description"": ""Optional source view id. If omitted, uses the active view.""
    },
    ""controlledSettingIds"": {
      ""type"": ""array"",
      ""items"": { ""type"": ""integer"" },
      ""description"": ""Optional exact set of template setting parameter ids to control.""
    },
    ""nonControlledSettingIds"": {
      ""type"": ""array"",
      ""items"": { ""type"": ""integer"" },
      ""description"": ""Optional exact set of template setting parameter ids to leave uncontrolled.""
    },
    ""failIfNameExists"": { ""type"": ""boolean"", ""default"": true }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            var templateName = request.Value<string>("templateName");
            var sourceViewId = request.Value<long?>("sourceViewId");
            var controlledSettingIds = request["controlledSettingIds"]?.ToObject<long[]>();
            var nonControlledSettingIds = request["nonControlledSettingIds"]?.ToObject<long[]>();
            var failIfNameExists = request.Value<bool?>("failIfNameExists") ?? true;

            if (string.IsNullOrWhiteSpace(templateName))
                return CommandResult.Fail("templateName is required and cannot be empty.");

            if (controlledSettingIds != null && nonControlledSettingIds != null)
                return CommandResult.Fail("Cannot specify both controlledSettingIds and nonControlledSettingIds.");

            View sourceView = null;
            if (sourceViewId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(sourceViewId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(sourceViewId.Value));

                sourceView = doc.GetElement(RevitCompat.ToElementId(sourceViewId.Value)) as View;
                if (sourceView == null)
                    return CommandResult.Fail($"Source view {sourceViewId.Value} not found.");
            }
            else
            {
                sourceView = doc.ActiveView;
            }

            if (sourceView == null)
                return CommandResult.Fail("No source view is available.");

            if (sourceView.IsTemplate)
                return CommandResult.Fail("Source view is already a view template.");

            // Check if name already exists
            var existingTemplates = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => v.IsTemplate)
                .ToList();

            string finalName = templateName.Trim();
            bool nameExists = existingTemplates.Any(t => string.Equals(t.Name, finalName, StringComparison.OrdinalIgnoreCase));
            if (nameExists)
            {
                if (failIfNameExists)
                {
                    return CommandResult.Fail($"A view template with the name '{finalName}' already exists.");
                }

                int suffix = 2;
                while (existingTemplates.Any(t => string.Equals(t.Name, $"{finalName} ({suffix})", StringComparison.OrdinalIgnoreCase)))
                {
                    suffix++;
                }
                finalName = $"{finalName} ({suffix})";
            }

            View newTemplate = null;
            int controlledCount = 0;
            int nonControlledCount = 0;

            using (var tx = new Transaction(doc, "RvtMcp: create view template"))
            {
                tx.Start();
                try
                {
                    newTemplate = sourceView.CreateViewTemplate();
                    newTemplate.Name = finalName;

                    var allParamIds = newTemplate.GetTemplateParameterIds();

                    if (nonControlledSettingIds != null)
                    {
                        var nonControlledElementIds = new List<ElementId>();
                        foreach (var idVal in nonControlledSettingIds)
                        {
                            var elementId = RevitCompat.ToElementId(idVal);
                            if (!allParamIds.Any(pid => RevitCompat.GetId(pid) == idVal))
                            {
                                tx.RollBack();
                                return CommandResult.Fail($"Setting parameter ID {idVal} is not valid for this template type.");
                            }
                            nonControlledElementIds.Add(elementId);
                        }
                        newTemplate.SetNonControlledTemplateParameterIds(nonControlledElementIds);
                    }
                    else if (controlledSettingIds != null)
                    {
                        // Compute non-controlled = all - controlled
                        var nonControlledElementIds = new List<ElementId>();
                        foreach (var pid in allParamIds)
                        {
                            var pidVal = RevitCompat.GetId(pid);
                            if (!controlledSettingIds.Contains(pidVal))
                            {
                                nonControlledElementIds.Add(pid);
                            }
                        }

                        // Validate that every requested controlled ID exists in allParamIds
                        foreach (var idVal in controlledSettingIds)
                        {
                            if (!allParamIds.Any(pid => RevitCompat.GetId(pid) == idVal))
                            {
                                tx.RollBack();
                                return CommandResult.Fail($"Controlled setting parameter ID {idVal} is not valid for this template type.");
                            }
                        }
                        newTemplate.SetNonControlledTemplateParameterIds(nonControlledElementIds);
                    }

                    controlledCount = newTemplate.GetTemplateParameterIds().Count - newTemplate.GetNonControlledTemplateParameterIds().Count;
                    nonControlledCount = newTemplate.GetNonControlledTemplateParameterIds().Count;

                    var commitStatus = tx.Commit();
                    if (commitStatus != TransactionStatus.Committed)
                    {
                        return CommandResult.Fail($"Transaction commit failed: {commitStatus}");
                    }
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to create view template: {ex.Message}");
                }
            }

            return CommandResult.Ok(new
            {
                created = true,
                templateId = RevitCompat.GetId(newTemplate.Id),
                templateName = newTemplate.Name,
                sourceViewId = RevitCompat.GetId(sourceView.Id),
                sourceViewName = sourceView.Name,
                viewType = newTemplate.ViewType.ToString(),
                controlledSettingCount = controlledCount,
                nonControlledSettingCount = nonControlledCount,
                warnings = Array.Empty<string>()
            });
        }
    }
}
