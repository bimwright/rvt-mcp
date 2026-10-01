using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class DuplicateViewTemplateHandler : IRevitCommand
    {
        public string Name => "duplicate_view_template";
        public string Description => "Duplicate an existing view template.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""templateId"", ""newName""],
  ""properties"": {
    ""templateId"": { ""type"": ""integer"" },
    ""newName"": { ""type"": ""string"" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            var templateIdVal = request.Value<long>("templateId");
            var newName = request.Value<string>("newName");

            if (string.IsNullOrWhiteSpace(newName))
                return CommandResult.Fail("newName is required and cannot be empty.");

            if (!RevitCompat.CanRepresentElementId(templateIdVal))
                return CommandResult.Fail(RevitCompat.ElementIdRangeError(templateIdVal));

            var template = doc.GetElement(RevitCompat.ToElementId(templateIdVal)) as View;
            if (template == null || !template.IsTemplate)
                return CommandResult.Fail($"View template with ID {templateIdVal} not found or is not a template view.");

            // Check name uniqueness
            var existingTemplates = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => v.IsTemplate)
                .ToList();

            string trimmedNewName = newName.Trim();
            if (existingTemplates.Any(t => string.Equals(t.Name, trimmedNewName, StringComparison.OrdinalIgnoreCase)))
                return CommandResult.Fail($"A view template with the name '{trimmedNewName}' already exists.");

            View duplicatedTemplate = null;
            int controlledCount = 0;
            int nonControlledCount = 0;

            using (var tx = new Transaction(doc, "RvtMcp: duplicate view template"))
            {
                tx.Start();
                try
                {
                    // View.Duplicate rejects template views by API design; the supported
                    // copy is CreateViewTemplate(), which snapshots this view's settings
                    // into a new template element.
                    duplicatedTemplate = template.CreateViewTemplate();
                    if (duplicatedTemplate == null)
                    {
                        tx.RollBack();
                        return CommandResult.Fail("Failed to duplicate the view template.");
                    }

                    duplicatedTemplate.Name = trimmedNewName;

                    controlledCount = duplicatedTemplate.GetTemplateParameterIds().Count - duplicatedTemplate.GetNonControlledTemplateParameterIds().Count;
                    nonControlledCount = duplicatedTemplate.GetNonControlledTemplateParameterIds().Count;

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                    {
                        return CommandResult.Fail($"Transaction commit status: {status}");
                    }
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to duplicate view template: {ex.Message}");
                }
            }

            return CommandResult.Ok(new
            {
                duplicated = true,
                sourceTemplateId = RevitCompat.GetId(template.Id),
                sourceTemplateName = template.Name,
                templateId = RevitCompat.GetId(duplicatedTemplate.Id),
                templateName = duplicatedTemplate.Name,
                viewType = duplicatedTemplate.ViewType.ToString(),
                controlledSettingCount = controlledCount,
                nonControlledSettingCount = nonControlledCount
            });
        }
    }
}
