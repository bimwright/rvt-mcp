using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class WorkflowSheetSetHandler : IRevitCommand
    {
        public string Name => "workflow_sheet_set";
        public string Description => "Create a coordinated sheet set, place views and schedules, and set sheet parameters with dry-run and rollback reporting.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""sheets""],
  ""properties"": {
    ""sheets"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""required"": [""sheet_number"", ""sheet_name""],
        ""properties"": {
          ""sheet_number"": { ""type"": ""string"" },
          ""sheet_name"": { ""type"": ""string"" },
          ""titleblock_type_id"": { ""type"": ""integer"" },
          ""view_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } },
          ""schedule_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } },
          ""parameters"": { ""type"": ""object"" }
        }
      }
    },
    ""renumber_strategy"": { ""type"": ""string"", ""enum"": [""none"", ""prefix"", ""replace""], ""default"": ""none"" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""continue_on_error"": { ""type"": ""boolean"", ""default"": false }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No active document is available.");

            JObject request;
            try
            {
                request = WorkflowSupport.ParseParams(paramsJson);
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            var sheetsArr = request["sheets"] as JArray;
            if (sheetsArr == null || sheetsArr.Count == 0)
                return CommandResult.Fail("'sheets' must be a non-empty array.");

            var renumberStrategy = (request.Value<string>("renumber_strategy") ?? "none").Trim().ToLowerInvariant();
            if (renumberStrategy != "none" && renumberStrategy != "prefix" && renumberStrategy != "replace")
                return CommandResult.Fail("renumber_strategy must be one of: none, prefix, replace.");

            var dryRun = request.Value<bool?>("dry_run") ?? true;
            var continueOnError = request.Value<bool?>("continue_on_error") ?? false;
            var steps = new JArray();
            var warnings = new List<string>();
            var createdIds = new List<long>();
            var modifiedIds = new List<long>();
            var sheetReports = new JArray();

            var specs = new List<SheetSpec>();
            try
            {
                for (var i = 0; i < sheetsArr.Count; i++)
                    specs.Add(ParseSheetSpec((JObject)sheetsArr[i], i));
            }
            catch (Exception ex)
            {
                return CommandResult.Fail(ex.Message);
            }

            var duplicateRequested = specs
                .GroupBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToArray();
            if (duplicateRequested.Length > 0)
                return CommandResult.Fail("Duplicate requested sheet numbers: " + string.Join(", ", duplicateRequested));

            var existingSheetNumbers = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Select(s => s.SheetNumber),
                StringComparer.OrdinalIgnoreCase);
            var existingCollisions = specs.Where(s => existingSheetNumbers.Contains(s.SheetNumber)).Select(s => s.SheetNumber).ToArray();
            if (existingCollisions.Length > 0)
                return CommandResult.Fail("Sheet numbers already exist: " + string.Join(", ", existingCollisions));

            var validationErrors = ValidateSpecs(doc, specs);
            if (validationErrors.Count > 0 && !continueOnError)
                return CommandResult.Fail("Sheet set validation failed: " + string.Join("; ", validationErrors));
            foreach (var error in validationErrors)
                warnings.Add(error);

            steps.Add(WorkflowSupport.Step(
                "Validate Sheet Set",
                "workflow_sheet_set",
                validationErrors.Count == 0 ? "succeeded" : "partial",
                "Validate sheet numbers, titleblocks, view IDs, schedule IDs, and requested parameters.",
                new { sheet_count = specs.Count, validation_error_count = validationErrors.Count }));

            if (dryRun)
            {
                foreach (var spec in specs)
                    sheetReports.Add(BuildDryRunReport(spec, doc));

                steps.Add(WorkflowSupport.Step(
                    "Create Sheets",
                    "ViewSheet.Create",
                    "skipped",
                    "Dry-run: sheets and placements were validated but not created.",
                    new { planned = specs.Count }));

                var result = WorkflowSupport.Envelope(
                    Name,
                    true,
                    validationErrors.Count == 0 ? "succeeded" : "partial",
                    steps,
                    createdIds,
                    modifiedIds,
                    warnings,
                    WorkflowSupport.Rollback("TransactionGroup", false, "Dry-run; no write operations attempted."));
                result["sheets"] = sheetReports;
                return CommandResult.Ok(result);
            }

            var hadFailure = false;
            var rollback = WorkflowSupport.Rollback("TransactionGroup", false, "Committed successfully.");
            using (Failures.FailureControl.BeginBatch())
            using (var group = new TransactionGroup(doc, "RvtMcp: workflow sheet set"))
            {
                group.Start();

                foreach (var spec in specs)
                {
                    using (var tx = new Transaction(doc, "RvtMcp: create workflow sheet"))
                    {
                        tx.Start();
                        try
                        {
                            var report = CreateSheet(doc, spec, createdIds, modifiedIds);
                            sheetReports.Add(report);
                            steps.Add(WorkflowSupport.Step(
                                "Create Sheet " + spec.SheetNumber,
                                "ViewSheet.Create",
                                "succeeded",
                                "Create sheet, place requested views/schedules, and set sheet parameters.",
                                report));

                            var status = tx.Commit();
                            if (status != TransactionStatus.Committed)
                                throw new InvalidOperationException("Transaction status: " + status);
                        }
                        catch (Exception ex)
                        {
                            hadFailure = true;
                            if (tx.HasStarted())
                                tx.RollBack();

                            var failedReport = new JObject
                            {
                                ["sheet_number"] = spec.SheetNumber,
                                ["sheet_name"] = spec.SheetName,
                                ["created"] = false,
                                ["error"] = ex.Message
                            };
                            sheetReports.Add(failedReport);
                            steps.Add(WorkflowSupport.Step(
                                "Create Sheet " + spec.SheetNumber,
                                "ViewSheet.Create",
                                "failed",
                                "Create sheet, place requested views/schedules, and set sheet parameters.",
                                null,
                                ex.Message));

                            if (!continueOnError || Failures.FailureControl.IsStopped)
                                break;
                        }
                    }
                }

                if ((hadFailure && !continueOnError) || Failures.FailureControl.IsStopped)
                {
                    var rollbackStatus = group.RollBack();
                    if (rollbackStatus != TransactionStatus.RolledBack)
                        return CommandResult.Fail("Sheet workflow rollback was not confirmed (status: " + rollbackStatus + ").");
                    Failures.FailureControl.NotifyBatchRolledBack();
                    if (Failures.FailureControl.IsStopped)
                        return CommandResult.Fail("REVIT_FAILURE_ROLLED_BACK: Failure policy stopped the sheet workflow and its transaction group was rolled back.");
                    rollback = WorkflowSupport.Rollback("TransactionGroup", true, "A sheet step failed and continue_on_error=false.");
                    createdIds.Clear();
                    modifiedIds.Clear();
                }
                else
                {
                    if (group.Assimilate() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the sheet workflow transaction group; model changes were not confirmed.");
                }
            }

            var final = WorkflowSupport.Envelope(
                Name,
                false,
                hadFailure ? (continueOnError ? "partial" : "failed") : "succeeded",
                steps,
                createdIds,
                modifiedIds,
                warnings,
                rollback);
            final["sheets"] = sheetReports;
            return CommandResult.Ok(final);
        }

        private static SheetSpec ParseSheetSpec(JObject item, int index)
        {
            if (item == null)
                throw new ArgumentException("sheets[" + index.ToString(CultureInfo.InvariantCulture) + "] must be an object.");

            var sheetNumber = item.Value<string>("sheet_number");
            var sheetName = item.Value<string>("sheet_name");
            if (string.IsNullOrWhiteSpace(sheetNumber))
                throw new ArgumentException("sheets[" + index.ToString(CultureInfo.InvariantCulture) + "].sheet_number is required.");
            if (string.IsNullOrWhiteSpace(sheetName))
                throw new ArgumentException("sheets[" + index.ToString(CultureInfo.InvariantCulture) + "].sheet_name is required.");

            return new SheetSpec
            {
                SheetNumber = sheetNumber.Trim(),
                SheetName = sheetName.Trim(),
                TitleblockTypeId = item.Value<long?>("titleblock_type_id") ?? item.Value<long?>("title_block_type_id"),
                ViewIds = WorkflowSupport.ReadLongArray(item, "view_ids") ?? new long[0],
                ScheduleIds = WorkflowSupport.ReadLongArray(item, "schedule_ids") ?? new long[0],
                Parameters = item["parameters"] as JObject ?? new JObject()
            };
        }

        private static List<string> ValidateSpecs(Document doc, IEnumerable<SheetSpec> specs)
        {
            var errors = new List<string>();
            foreach (var spec in specs)
            {
                if (spec.TitleblockTypeId.HasValue)
                {
                    var titleblock = doc.GetElement(RevitCompat.ToElementId(spec.TitleblockTypeId.Value)) as FamilySymbol;
                    if (titleblock == null || titleblock.Category == null || RevitCompat.GetId(titleblock.Category.Id) != (long)BuiltInCategory.OST_TitleBlocks)
                        errors.Add("Sheet " + spec.SheetNumber + ": titleblock_type_id is not a titleblock FamilySymbol.");
                }

                foreach (var viewId in spec.ViewIds)
                {
                    var view = doc.GetElement(RevitCompat.ToElementId(viewId)) as View;
                    if (view == null || view.IsTemplate || view.ViewType == ViewType.DrawingSheet)
                        errors.Add("Sheet " + spec.SheetNumber + ": view_id " + viewId.ToString(CultureInfo.InvariantCulture) + " is not a placeable view.");
                }

                foreach (var scheduleId in spec.ScheduleIds)
                {
                    var schedule = doc.GetElement(RevitCompat.ToElementId(scheduleId)) as ViewSchedule;
                    if (schedule == null || schedule.IsTemplate)
                        errors.Add("Sheet " + spec.SheetNumber + ": schedule_id " + scheduleId.ToString(CultureInfo.InvariantCulture) + " is not a placeable schedule.");
                }
            }
            return errors;
        }

        private static JObject BuildDryRunReport(SheetSpec spec, Document doc)
        {
            return new JObject
            {
                ["sheet_number"] = spec.SheetNumber,
                ["sheet_name"] = spec.SheetName,
                ["titleblock_type_id"] = spec.TitleblockTypeId.HasValue ? new JValue(spec.TitleblockTypeId.Value) : JValue.CreateNull(),
                ["planned_viewports"] = spec.ViewIds.Length,
                ["planned_schedules"] = spec.ScheduleIds.Length,
                ["planned_parameter_updates"] = spec.Parameters.Count,
                ["would_create"] = true
            };
        }

        private static JObject CreateSheet(Document doc, SheetSpec spec, List<long> createdIds, List<long> modifiedIds)
        {
            var titleblockId = ElementId.InvalidElementId;
            if (spec.TitleblockTypeId.HasValue)
            {
                var symbol = doc.GetElement(RevitCompat.ToElementId(spec.TitleblockTypeId.Value)) as FamilySymbol;
                if (symbol == null)
                    throw new InvalidOperationException("Titleblock type not found: " + spec.TitleblockTypeId.Value.ToString(CultureInfo.InvariantCulture));
                if (!symbol.IsActive)
                {
                    symbol.Activate();
                    doc.Regenerate();
                }
                titleblockId = symbol.Id;
            }
            else
            {
                var symbol = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsElementType()
                    .Cast<FamilySymbol>()
                    .FirstOrDefault();
                if (symbol != null)
                    titleblockId = symbol.Id;
            }

            var sheet = ViewSheet.Create(doc, titleblockId);
            sheet.SheetNumber = spec.SheetNumber;
            sheet.Name = spec.SheetName;
            var sheetId = RevitCompat.GetId(sheet.Id);
            createdIds.Add(sheetId);

            var viewports = new JArray();
            var schedules = new JArray();
            var parameterUpdates = new JArray();

            for (var i = 0; i < spec.ViewIds.Length; i++)
            {
                var view = doc.GetElement(RevitCompat.ToElementId(spec.ViewIds[i])) as View;
                if (view == null)
                    throw new InvalidOperationException("View not found: " + spec.ViewIds[i].ToString(CultureInfo.InvariantCulture));
                if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                    throw new InvalidOperationException("Cannot place view '" + view.Name + "' on sheet " + spec.SheetNumber + ".");

                var point = PlacementPoint(i);
                var viewport = Viewport.Create(doc, sheet.Id, view.Id, point);
                createdIds.Add(RevitCompat.GetId(viewport.Id));
                viewports.Add(new JObject
                {
                    ["viewport_id"] = RevitCompat.GetId(viewport.Id),
                    ["view_id"] = RevitCompat.GetId(view.Id),
                    ["view_name"] = view.Name,
                    ["point"] = PointDto(point)
                });
            }

            for (var i = 0; i < spec.ScheduleIds.Length; i++)
            {
                var schedule = doc.GetElement(RevitCompat.ToElementId(spec.ScheduleIds[i])) as ViewSchedule;
                if (schedule == null)
                    throw new InvalidOperationException("Schedule not found: " + spec.ScheduleIds[i].ToString(CultureInfo.InvariantCulture));

                var point = PlacementPoint(spec.ViewIds.Length + i);
                var instance = ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, point);
                createdIds.Add(RevitCompat.GetId(instance.Id));
                schedules.Add(new JObject
                {
                    ["schedule_instance_id"] = RevitCompat.GetId(instance.Id),
                    ["schedule_id"] = RevitCompat.GetId(schedule.Id),
                    ["schedule_name"] = schedule.Name,
                    ["point"] = PointDto(point)
                });
            }

            foreach (var property in spec.Parameters.Properties())
            {
                var parameter = sheet.LookupParameter(property.Name);
                var update = new JObject
                {
                    ["parameter"] = property.Name,
                    ["value"] = property.Value.Type == JTokenType.Null ? null : property.Value.ToString()
                };

                if (parameter == null)
                {
                    update["status"] = "failed";
                    update["error"] = "Parameter not found on sheet.";
                    parameterUpdates.Add(update);
                    continue;
                }

                if (WorkflowSupport.TrySetParameterFromString(parameter, property.Value.ToString(), out var error))
                {
                    update["status"] = "succeeded";
                    modifiedIds.Add(sheetId);
                }
                else
                {
                    update["status"] = "failed";
                    update["error"] = error;
                }
                parameterUpdates.Add(update);
            }

            return new JObject
            {
                ["sheet_id"] = sheetId,
                ["sheet_number"] = sheet.SheetNumber,
                ["sheet_name"] = sheet.Name,
                ["created"] = true,
                ["viewports"] = viewports,
                ["schedules"] = schedules,
                ["parameter_updates"] = parameterUpdates
            };
        }

        private static XYZ PlacementPoint(int index)
        {
            var column = index % 3;
            var row = index / 3;
            return new XYZ(0.35 + column * 0.45, 0.75 - row * 0.28, 0);
        }

        private static JObject PointDto(XYZ point)
        {
            return new JObject
            {
                ["unit"] = "mm",
                ["x"] = Math.Round(point.X * WorkflowSupport.FeetToMm, 1),
                ["y"] = Math.Round(point.Y * WorkflowSupport.FeetToMm, 1),
                ["z"] = Math.Round(point.Z * WorkflowSupport.FeetToMm, 1)
            };
        }

        private class SheetSpec
        {
            public string SheetNumber;
            public string SheetName;
            public long? TitleblockTypeId;
            public long[] ViewIds;
            public long[] ScheduleIds;
            public JObject Parameters;
        }
    }
}
