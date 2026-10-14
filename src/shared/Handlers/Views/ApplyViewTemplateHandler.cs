using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ApplyViewTemplateHandler : IRevitCommand
    {
        public string Name => "apply_view_template";
        public string Description => "Apply or assign a view template to one or more views.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""templateId""],
  ""properties"": {
    ""templateId"": { ""type"": ""integer"" },
    ""viewIds"": {
      ""type"": ""array"",
      ""items"": { ""type"": ""integer"" },
      ""description"": ""Target views. If omitted, uses the active view.""
    },
    ""mode"": {
      ""type"": ""string"",
      ""enum"": [""assign"", ""applyParameters""],
      ""default"": ""assign"",
      ""description"": ""assign sets ViewTemplateId. applyParameters does a one-time ApplyViewTemplateParameters call.""
    },
    ""replaceExisting"": {
      ""type"": ""boolean"",
      ""default"": false,
      ""description"": ""Required to replace an existing assigned ViewTemplateId in assign mode.""
    }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            var templateIdVal = request.Value<long>("templateId");
            var viewIdsInput = request["viewIds"]?.ToObject<long[]>();
            var mode = request.Value<string>("mode") ?? "assign";
            var replaceExisting = request.Value<bool?>("replaceExisting") ?? false;

            if (!RevitCompat.CanRepresentElementId(templateIdVal))
                return CommandResult.Fail(RevitCompat.ElementIdRangeError(templateIdVal));

            var template = doc.GetElement(RevitCompat.ToElementId(templateIdVal)) as View;
            if (template == null || !template.IsTemplate)
                return CommandResult.Fail($"View template with ID {templateIdVal} not found or is not a template view.");

            if (!string.Equals(mode, "assign", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(mode, "applyParameters", StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.Fail("mode must be 'assign' or 'applyParameters'.");
            }

            var targetViews = new List<View>();
            if (viewIdsInput != null && viewIdsInput.Length > 0)
            {
                foreach (var vidVal in viewIdsInput)
                {
                    if (!RevitCompat.CanRepresentElementId(vidVal))
                        return CommandResult.Fail(RevitCompat.ElementIdRangeError(vidVal));

                    var targetView = doc.GetElement(RevitCompat.ToElementId(vidVal)) as View;
                    if (targetView == null)
                        return CommandResult.Fail($"Target view with ID {vidVal} not found.");

                    targetViews.Add(targetView);
                }
            }
            else
            {
                if (doc.ActiveView == null)
                    return CommandResult.Fail("No active view available and viewIds array is empty.");
                targetViews.Add(doc.ActiveView);
            }

            // 1. Strict Pre-validation of all targets (all-or-nothing check)
            foreach (var target in targetViews)
            {
                if (target.IsTemplate)
                    return CommandResult.Fail($"Target view {RevitCompat.GetId(target.Id)} is a view template; cannot apply templates to templates.");

                if (!target.IsValidViewTemplate(template.Id))
                    return CommandResult.Fail($"View template '{template.Name}' is not compatible with view type of '{target.Name}' ({target.ViewType}).");

                if (string.Equals(mode, "assign", StringComparison.OrdinalIgnoreCase))
                {
                    var currentTemplateId = target.ViewTemplateId;
                    if (currentTemplateId != ElementId.InvalidElementId && !replaceExisting)
                    {
                        var currentTemplate = doc.GetElement(currentTemplateId) as View;
                        return CommandResult.Fail($"Target view '{target.Name}' ({RevitCompat.GetId(target.Id)}) already has template '{currentTemplate?.Name ?? "unknown"}' assigned. Set replaceExisting to true to replace it.");
                    }
                }
            }

            var resultsList = new List<object>();

            // 2. Perform the assignment inside a transaction
            using (var tx = new Transaction(doc, "RvtMcp: apply view template"))
            {
                tx.Start();
                try
                {
                    foreach (var target in targetViews)
                    {
                        long? prevId = null;
                        string prevName = null;
                        var currentTemplateId = target.ViewTemplateId;
                        if (currentTemplateId != ElementId.InvalidElementId)
                        {
                            prevId = RevitCompat.GetId(currentTemplateId);
                            var currentTemplate = doc.GetElement(currentTemplateId) as View;
                            prevName = currentTemplate?.Name;
                        }

                        if (string.Equals(mode, "assign", StringComparison.OrdinalIgnoreCase))
                        {
                            target.ViewTemplateId = template.Id;
                        }
                        else
                        {
                            target.ApplyViewTemplateParameters(template);
                        }

                        resultsList.Add(new
                        {
                            viewId = RevitCompat.GetId(target.Id),
                            viewName = target.Name,
                            viewType = target.ViewType.ToString(),
                            previousTemplateId = prevId,
                            previousTemplateName = prevName,
                            applied = true,
                            error = (string)null
                        });
                    }

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                    {
                        return CommandResult.Fail($"Transaction commit status: {status}");
                    }
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to apply view template: {ex.Message}");
                }
            }

            return CommandResult.Ok(new
            {
                templateId = RevitCompat.GetId(template.Id),
                templateName = template.Name,
                mode,
                requested = targetViews.Count,
                appliedCount = resultsList.Count,
                failedCount = 0,
                results = resultsList
            });
        }
    }
}
