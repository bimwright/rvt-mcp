using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class DeleteViewTemplateHandler : IRevitCommand
    {
        public string Name => "delete_view_template";
        public string Description => "Delete a view template from the document.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""templateId""],
  ""properties"": {
    ""templateId"": { ""type"": ""integer"" },
    ""dryRun"": { ""type"": ""boolean"", ""default"": true },
    ""clearFromViews"": {
      ""type"": ""boolean"",
      ""default"": false,
      ""description"": ""If true, clears ViewTemplateId from dependent views before deleting.""
    },
    ""max_used_by_views"": { ""type"": ""integer"", ""default"": 100, ""minimum"": 1, ""maximum"": 1000 }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            var templateIdVal = request.Value<long>("templateId");
            var dryRun = request.Value<bool?>("dryRun") ?? true;
            var clearFromViews = request.Value<bool?>("clearFromViews") ?? false;
            var maxUsedByViews = request.Value<int?>("max_used_by_views") ?? 100;
            if (maxUsedByViews < 1 || maxUsedByViews > 1000)
                return CommandResult.Fail("max_used_by_views must be between 1 and the hard maximum of 1000.");

            if (!RevitCompat.CanRepresentElementId(templateIdVal))
                return CommandResult.Fail(RevitCompat.ElementIdRangeError(templateIdVal));

            var templateId = RevitCompat.ToElementId(templateIdVal);
            var template = doc.GetElement(templateId) as View;
            if (template == null || !template.IsTemplate)
                return CommandResult.Fail($"View template with ID {templateIdVal} not found or is not a template view.");

            // Find all views currently using this template
            var dependentViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate && RevitCompat.GetId(v.ViewTemplateId) == templateIdVal)
                .ToList();

            var usedByViewsDto = dependentViews.Select(v => new
            {
                viewId = RevitCompat.GetId(v.Id),
                name = v.Name,
                viewType = v.ViewType.ToString()
            }).OrderBy(v => v.name).ToArray();

            bool wouldDelete = true;
            bool deleted = false;
            var deletedIds = new List<long>();

            if (dependentViews.Count > 0 && !clearFromViews)
            {
                if (dryRun)
                {
                    wouldDelete = false;
                }
                else
                {
                    return CommandResult.Fail($"Cannot delete template '{template.Name}' because it is in use by {dependentViews.Count} views. Set clearFromViews to true to detach it from them first.");
                }
            }

            if (!dryRun && wouldDelete)
            {
                using (var tx = new Transaction(doc, "RvtMcp: delete view template"))
                {
                    tx.Start();
                    try
                    {
                        if (dependentViews.Count > 0 && clearFromViews)
                        {
                            foreach (var view in dependentViews)
                            {
                                view.ViewTemplateId = ElementId.InvalidElementId;
                            }
                        }

                        var deletedCol = doc.Delete(templateId);
                        if (deletedCol != null)
                        {
                            foreach (var id in deletedCol)
                            {
                                deletedIds.Add(RevitCompat.GetId(id));
                            }
                        }

                        deleted = true;

                        var status = tx.Commit();
                        if (status != TransactionStatus.Committed)
                        {
                            return CommandResult.Fail($"Transaction commit failed: {status}");
                        }
                    }
                    catch (Exception ex)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        return CommandResult.Fail($"Failed to delete view template: {ex.Message}");
                    }
                }
            }

            return CommandResult.Ok(new
            {
                dryRun,
                wouldDelete,
                deleted,
                templateId = templateIdVal,
                templateName = template.Name,
                success = true,
                usedByViewCount = usedByViewsDto.Length,
                usedByViews = usedByViewsDto.Take(maxUsedByViews).ToArray(),
                usedByViewsTruncated = usedByViewsDto.Length > maxUsedByViews,
                clearFromViews,
                deletedElementCount = deletedIds.Count,
                deletedElementIds = deletedIds.Take(maxUsedByViews).ToArray(),
                deletedElementIdsTruncated = deletedIds.Count > maxUsedByViews
            });
        }
    }
}
