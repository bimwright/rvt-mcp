using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class DuplicateSheetHandler : IRevitCommand
    {
        public string Name => "duplicate_sheet";
        public string Description => "Duplicate an existing sheet with viewport and schedule layout";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""new_sheet_number""],
  ""properties"": {
    ""source_sheet_id"": { ""type"": ""integer"" },
    ""source_sheet_number"": { ""type"": ""string"" },
    ""new_sheet_number"": { ""type"": ""string"" },
    ""new_sheet_name"": { ""type"": ""string"" },
    ""duplicate_view_option"": { ""type"": ""string"", ""enum"": [""duplicate"", ""with_detailing"", ""as_dependent""] },
    ""include_schedules"": { ""type"": ""boolean"", ""default"": true },
    ""include_revisions"": { ""type"": ""boolean"", ""default"": true },
    ""reuse_views_when_allowed"": { ""type"": ""boolean"", ""default"": true }
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

            var sourceSheetId = request.Value<long?>("source_sheet_id") ?? request.Value<long?>("sourceSheetId");
            var sourceSheetNumber = request.Value<string>("source_sheet_number") ?? request.Value<string>("sourceSheetNumber") ?? "";
            var newSheetNumber = request.Value<string>("new_sheet_number") ?? request.Value<string>("newSheetNumber");
            var newSheetName = request.Value<string>("new_sheet_name") ?? request.Value<string>("newSheetName") ?? "";
            var duplicateViewOption = request.Value<string>("duplicate_view_option") ?? request.Value<string>("duplicateViewOption") ?? "with_detailing";
            var includeSchedules = request.Value<bool?>("include_schedules") ?? request.Value<bool?>("includeSchedules") ?? true;
            var includeRevisions = request.Value<bool?>("include_revisions") ?? request.Value<bool?>("includeRevisions") ?? true;
            var reuseViewsWhenAllowed = request.Value<bool?>("reuse_views_when_allowed") ?? request.Value<bool?>("reuseViewsWhenAllowed") ?? true;

            if (string.IsNullOrWhiteSpace(newSheetNumber))
                return CommandResult.Fail("new_sheet_number is required and cannot be empty.");
            if (!duplicateViewOption.Equals("duplicate", StringComparison.OrdinalIgnoreCase) &&
                !duplicateViewOption.Equals("with_detailing", StringComparison.OrdinalIgnoreCase) &&
                !duplicateViewOption.Equals("as_dependent", StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.Fail("duplicate_view_option must be one of: duplicate, with_detailing, as_dependent.");
            }

            // Resolve source sheet
            ViewSheet sourceSheet = null;
            if (sourceSheetId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(sourceSheetId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(sourceSheetId.Value));

                sourceSheet = doc.GetElement(RevitCompat.ToElementId(sourceSheetId.Value)) as ViewSheet;
            }
            else if (!string.IsNullOrEmpty(sourceSheetNumber))
            {
                sourceSheet = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .FirstOrDefault(s => s.SheetNumber.Equals(sourceSheetNumber, StringComparison.OrdinalIgnoreCase));
            }

            if (sourceSheet == null)
                return CommandResult.Fail("Source sheet not resolved. Provide valid source_sheet_id or source_sheet_number.");

            // GUARD: Reject sheet duplication if source sheet is a placeholder
            if (sourceSheet.IsPlaceholder)
                return CommandResult.Fail("Cannot duplicate a placeholder sheet. Use create_placeholder_sheet instead.");

            // Preflight check for sheet number collision
            var sheetNumberExists = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Any(s => s.SheetNumber.Equals(newSheetNumber, StringComparison.OrdinalIgnoreCase));

            if (sheetNumberExists)
                return CommandResult.Fail($"Sheet with number '{newSheetNumber}' already exists.");

            var warnings = new List<string>();
            var viewportsList = new List<object>();
            var schedulesList = new List<object>();
            var revisionIds = new List<long>();
            ViewSheet newSheet = null;
            FamilyInstance titleBlockInstance = null;

            // Atomic rollback via TransactionGroup
            using (var txGroup = new TransactionGroup(doc, "RvtMcp: duplicate sheet"))
            {
                txGroup.Start();
                using (var tx = new Transaction(doc, "Duplicate Sheet Internal"))
                {
                    tx.Start();
                    try
                    {
                        // Resolve the source title block instance and its type id
                        titleBlockInstance = new FilteredElementCollector(doc, sourceSheet.Id)
                            .OfCategory(BuiltInCategory.OST_TitleBlocks)
                            .WhereElementIsNotElementType()
                            .Cast<FamilyInstance>()
                            .FirstOrDefault();

                        var titleBlockTypeId = titleBlockInstance != null ? titleBlockInstance.GetTypeId() : ElementId.InvalidElementId;

                        // Create sheet
                        newSheet = ViewSheet.Create(doc, titleBlockTypeId);
                        newSheet.SheetNumber = newSheetNumber;
                        newSheet.Name = !string.IsNullOrEmpty(newSheetName) ? newSheetName : sourceSheet.Name;

                        // Copy title block instance parameters only if they are writable and instance-scoped
                        if (titleBlockInstance != null)
                        {
                            var newTitleBlockInstance = new FilteredElementCollector(doc, newSheet.Id)
                                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                                .WhereElementIsNotElementType()
                                .Cast<FamilyInstance>()
                                .FirstOrDefault();

                            if (newTitleBlockInstance != null)
                            {
                                foreach (Parameter param in titleBlockInstance.Parameters)
                                {
                                    if (param.IsReadOnly || !param.HasValue || param.Definition == null) continue;
                                    // Sheet Number/Sheet Name on the titleblock mirror the sheet itself and were
                                    // already set on newSheet — copying the source values collides at commit
                                    // ("Sheet Number is already in use") and aborts the whole duplication.
                                    var builtIn = (param.Definition as InternalDefinition)?.BuiltInParameter;
                                    if (builtIn == BuiltInParameter.SHEET_NUMBER || builtIn == BuiltInParameter.SHEET_NAME) continue;
                                    var newParam = newTitleBlockInstance.get_Parameter(param.Definition);
                                    if (newParam != null && !newParam.IsReadOnly)
                                    {
                                        try
                                        {
                                            CopyParameterValue(param, newParam);
                                        }
                                        catch (Exception ex)
                                        {
                                            warnings.Add($"Could not copy titleblock parameter '{param.Definition.Name}': {ex.Message}");
                                        }
                                    }
                                }
                            }
                        }

                        // Duplicate Viewports
                        var sourceViewportIds = sourceSheet.GetAllViewports();
                        foreach (var vpId in sourceViewportIds)
                        {
                            var vp = doc.GetElement(vpId) as Viewport;
                            if (vp == null) continue;

                            var sourceViewId = vp.ViewId;
                            var sourceView = doc.GetElement(sourceViewId) as View;
                            if (sourceView == null) continue;

                            var center = vp.GetBoxCenter();
                            ElementId targetViewId = sourceViewId;
                            string modeUsed = "reused_view";

                            bool canReuse = reuseViewsWhenAllowed && Viewport.CanAddViewToSheet(doc, newSheet.Id, sourceViewId);
                            if (!canReuse)
                            {
                                ViewDuplicateOption dupOption = ViewDuplicateOption.WithDetailing;
                                if (duplicateViewOption.Equals("duplicate", StringComparison.OrdinalIgnoreCase))
                                    dupOption = ViewDuplicateOption.Duplicate;
                                else if (duplicateViewOption.Equals("as_dependent", StringComparison.OrdinalIgnoreCase))
                                    dupOption = ViewDuplicateOption.AsDependent;

                                try
                                {
                                    targetViewId = sourceView.Duplicate(dupOption);
                                    modeUsed = "duplicated_view";
                                }
                                catch (Exception ex)
                                {
                                    throw new InvalidOperationException($"Failed to duplicate view '{sourceView.Name}': {ex.Message}", ex);
                                }
                            }

                            Viewport newVp;
                            try
                            {
                                newVp = Viewport.Create(doc, newSheet.Id, targetViewId, center);
                            }
                            catch (Exception ex)
                            {
                                throw new InvalidOperationException($"Failed to place view on sheet at center ({center.X}, {center.Y}): {ex.Message}", ex);
                            }

                            viewportsList.Add(new
                            {
                                source_viewport_id = RevitCompat.GetId(vp.Id),
                                source_view_id = RevitCompat.GetId(sourceViewId),
                                new_view_id = RevitCompat.GetId(targetViewId),
                                new_viewport_id = RevitCompat.GetId(newVp.Id),
                                center = new { x_mm = center.X * 304.8, y_mm = center.Y * 304.8 },
                                mode = modeUsed
                            });
                        }

                        // Duplicate Schedules
                        if (includeSchedules)
                        {
                            var scheduleInstances = new FilteredElementCollector(doc, sourceSheet.Id)
                                .OfClass(typeof(ScheduleSheetInstance))
                                .Cast<ScheduleSheetInstance>()
                                .ToList();

                            foreach (var schedInst in scheduleInstances)
                            {
                                if (schedInst.IsTitleblockRevisionSchedule) continue;
                                var schedId = schedInst.ScheduleId;
                                var originalPoint = schedInst.Point;

                                try
                                {
                                    var newSchedInst = ScheduleSheetInstance.Create(doc, newSheet.Id, schedId, originalPoint);
                                    schedulesList.Add(new
                                    {
                                        source_schedule_instance_id = RevitCompat.GetId(schedInst.Id),
                                        schedule_id = RevitCompat.GetId(schedId),
                                        new_schedule_instance_id = RevitCompat.GetId(newSchedInst.Id),
                                        point = new { x_mm = originalPoint.X * 304.8, y_mm = originalPoint.Y * 304.8 }
                                    });
                                }
                                catch (Exception ex)
                                {
                                    warnings.Add($"Could not duplicate schedule '{doc.GetElement(schedId)?.Name}': {ex.Message}");
                                }
                            }
                        }

                        // Copy additional revisions only
                        if (includeRevisions)
                        {
                            var additionalRevisionIds = sourceSheet.GetAdditionalRevisionIds();
                            if (additionalRevisionIds != null && additionalRevisionIds.Count > 0)
                            {
                                newSheet.SetAdditionalRevisionIds(additionalRevisionIds);
                                foreach (var revId in additionalRevisionIds)
                                {
                                    revisionIds.Add(RevitCompat.GetId(revId));
                                }
                            }
                        }

                        var commitStatus = tx.Commit();
                        if (commitStatus != TransactionStatus.Committed)
                        {
                            // Do not dereference elements whose creation was rolled back.
                            // Report a failure so batch_execute also rolls back its group.
                            txGroup.RollBack();
                            return CommandResult.Fail($"Transaction did not commit (status: {commitStatus}).");
                        }
                        txGroup.Assimilate();
                    }
                    catch (Exception ex)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        txGroup.RollBack();
                        return CommandResult.Fail($"Failed to duplicate sheet: {ex.Message}");
                    }
                }
            }

            return CommandResult.Ok(new
            {
                duplicated = true,
                source_sheet_id = RevitCompat.GetId(sourceSheet.Id),
                new_sheet_id = RevitCompat.GetId(newSheet.Id),
                new_sheet_number = newSheet.SheetNumber,
                new_sheet_name = newSheet.Name,
                title_block_type_id = titleBlockInstance != null ? RevitCompat.GetId(titleBlockInstance.GetTypeId()) : (long?)null,
                viewports = viewportsList,
                schedules = schedulesList,
                revision_ids = revisionIds,
                warnings = warnings,
                error = (string)null
            });
        }

        private static void CopyParameterValue(Parameter source, Parameter target)
        {
            switch (source.StorageType)
            {
                case StorageType.String:
                    target.Set(source.AsString() ?? "");
                    break;
                case StorageType.Integer:
                    target.Set(source.AsInteger());
                    break;
                case StorageType.Double:
                    target.Set(source.AsDouble());
                    break;
                case StorageType.ElementId:
                    target.Set(source.AsElementId());
                    break;
            }
        }
    }
}
