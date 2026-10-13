using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ListSheetsHandler : IRevitCommand
    {
        public string Name => "list_sheets";
        public string Description => "List sheets matching filters, with viewport and schedule counts, title blocks, and revisions";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""number_filter"": { ""type"": ""string"" },
    ""name_pattern"": { ""type"": ""string"" },
    ""include_revisions"": { ""type"": ""boolean"", ""default"": true },
    ""include_viewports"": { ""type"": ""boolean"", ""default"": false },
    ""include_placeholders"": { ""type"": ""boolean"", ""default"": true },
    ""limit"": { ""type"": ""integer"", ""default"": 1000, ""minimum"": 1, ""maximum"": 10000 }
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
            catch (Exception ex)
            {
                return CommandResult.Fail($"Parameters must be a JSON object: {ex.Message}");
            }

            var numberFilter = request.Value<string>("number_filter") ?? request.Value<string>("numberFilter") ?? "";
            var namePattern = request.Value<string>("name_pattern") ?? request.Value<string>("namePattern") ?? "";
            var includeRevisions = request.Value<bool?>("include_revisions") ?? request.Value<bool?>("includeRevisions") ?? true;
            var includeViewports = request.Value<bool?>("include_viewports") ?? request.Value<bool?>("includeViewports") ?? false;
            var includePlaceholders = request.Value<bool?>("include_placeholders") ?? request.Value<bool?>("includePlaceholders") ?? true;
            var limit = request.Value<int?>("limit") ?? 1000;

            limit = Math.Max(1, Math.Min(limit, 10000));

            var allSheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .ToList();

            var filtered = new List<ViewSheet>();
            foreach (var sheet in allSheets)
            {
                if (!includePlaceholders && sheet.IsPlaceholder)
                    continue;

                if (!string.IsNullOrEmpty(numberFilter) && !sheet.SheetNumber.Contains(numberFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrEmpty(namePattern) && !sheet.Name.Contains(namePattern, StringComparison.OrdinalIgnoreCase))
                    continue;

                filtered.Add(sheet);
            }

            var total = filtered.Count;
            var sheetsToReturn = filtered.Take(limit).ToList();
            var limitHit = total > limit;

            var sheetDtos = new List<object>();
            int skipped = 0;

            foreach (var sheet in sheetsToReturn)
            {
                try
                {
                    int viewportCount = 0;
                    if (!sheet.IsPlaceholder)
                    {
                        viewportCount = sheet.GetAllViewports().Count;
                    }

                    int scheduleCount = 0;
                    if (!sheet.IsPlaceholder)
                    {
                        scheduleCount = new FilteredElementCollector(doc, sheet.Id)
                            .OfClass(typeof(ScheduleSheetInstance))
                            .Cast<ScheduleSheetInstance>()
                            .Count(instance => !instance.IsTitleblockRevisionSchedule);
                    }

                    object titleBlockDto = null;
                    if (!sheet.IsPlaceholder)
                    {
                        var titleBlockInstance = new FilteredElementCollector(doc, sheet.Id)
                            .OfCategory(BuiltInCategory.OST_TitleBlocks)
                            .WhereElementIsNotElementType()
                            .Cast<FamilyInstance>()
                            .FirstOrDefault();

                        if (titleBlockInstance != null)
                        {
                            var typeId = titleBlockInstance.GetTypeId();
                            var typeElem = doc.GetElement(typeId) as FamilySymbol;
                            titleBlockDto = new
                            {
                                instance_id = RevitCompat.GetId(titleBlockInstance.Id),
                                type_id = RevitCompat.GetId(typeId),
                                family_name = typeElem?.FamilyName ?? "",
                                type_name = typeElem?.Name ?? ""
                            };
                        }
                    }

                    // Resolve revisions
                    var revisionIdsList = new List<long>();
                    var revisionsList = new List<object>();
                    if (includeRevisions && !sheet.IsPlaceholder)
                    {
                        ICollection<ElementId> revisionIds = null;
                        try
                        {
                            revisionIds = sheet.GetAllRevisionIds();
                        }
                        catch
                        {
                            try
                            {
                                revisionIds = sheet.GetAdditionalRevisionIds();
                            }
                            catch
                            {
                                revisionIds = new List<ElementId>();
                            }
                        }

                        if (revisionIds != null)
                        {
                            foreach (var revId in revisionIds)
                            {
                                var rev = doc.GetElement(revId) as Revision;
                                if (rev == null) continue;

                                revisionIdsList.Add(RevitCompat.GetId(rev.Id));

                                string revNumber = "";
                                try
                                {
                                    revNumber = rev.RevisionNumber;
                                }
                                catch { }

                                revisionsList.Add(new
                                {
                                    id = RevitCompat.GetId(rev.Id),
                                    sequence_number = rev.SequenceNumber,
                                    revision_number = revNumber,
                                    description = rev.Description ?? ""
                                });
                            }
                        }
                    }

                    sheetDtos.Add(new
                    {
                        id = RevitCompat.GetId(sheet.Id),
                        sheet_number = sheet.SheetNumber,
                        sheet_name = sheet.Name,
                        is_placeholder = sheet.IsPlaceholder,
                        viewport_count = viewportCount,
                        schedule_count = scheduleCount,
                        title_block = titleBlockDto,
                        revision_ids = revisionIdsList,
                        revisions = revisionsList
                    });
                }
                catch
                {
                    skipped++;
                }
            }

            return CommandResult.Ok(new
            {
                total = total,
                returned = sheetDtos.Count,
                limit_hit = limitHit,
                skipped = skipped,
                sheets = sheetDtos
            });
        }
    }
}
