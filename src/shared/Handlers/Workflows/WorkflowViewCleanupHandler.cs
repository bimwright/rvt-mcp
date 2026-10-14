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
    public class WorkflowViewCleanupHandler : IRevitCommand
    {
        public string Name => "workflow_view_cleanup";
        public string Description => "Analyze unused views, empty schedules, and naming outliers, with guarded optional deletion of safe candidates.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""include_unused_views"": { ""type"": ""boolean"", ""default"": true },
    ""include_empty_schedules"": { ""type"": ""boolean"", ""default"": true },
    ""include_naming_outliers"": { ""type"": ""boolean"", ""default"": true },
    ""delete_empty_views"": { ""type"": ""boolean"", ""default"": false },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""limit"": { ""type"": ""integer"", ""default"": 200, ""minimum"": 1, ""maximum"": 1000 }
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

            var includeUnusedViews = request.Value<bool?>("include_unused_views") ?? true;
            var includeEmptySchedules = request.Value<bool?>("include_empty_schedules") ?? true;
            var includeNamingOutliers = request.Value<bool?>("include_naming_outliers") ?? true;
            var deleteEmptyViews = request.Value<bool?>("delete_empty_views") ?? false;
            var dryRun = request.Value<bool?>("dry_run") ?? true;
            var limit = request.Value<int?>("limit") ?? 200;

            if (!includeUnusedViews && !includeEmptySchedules && !includeNamingOutliers)
                return CommandResult.Fail("At least one include_* option must be true.");
            if (limit < 1 || limit > 1000)
                return CommandResult.Fail("limit must be between 1 and 1000.");
            if (!dryRun && !deleteEmptyViews)
                return CommandResult.Fail("Non-dry-run cleanup requires delete_empty_views=true.");

            var steps = new JArray();
            var warnings = new List<string>();
            var deletedIds = new List<long>();
            var placedViewIds = CollectPlacedViewIds(doc);
            var candidates = new JObject();
            var proposedDeletes = new JArray();

            if (includeUnusedViews)
            {
                var unusedViews = FindUnusedViews(doc, placedViewIds, limit, out var truncated);
                candidates["unused_views"] = unusedViews;
                if (truncated)
                    warnings.Add("Unused view candidates were truncated at " + limit.ToString(CultureInfo.InvariantCulture) + ".");

                foreach (var item in unusedViews.OfType<JObject>())
                {
                    if (item.Value<bool>("safe_to_delete"))
                        proposedDeletes.Add(item.DeepClone());
                }

                steps.Add(WorkflowSupport.Step(
                    "Find Unused Views",
                    "view collectors",
                    "succeeded",
                    "Find printable views not placed on sheets and not otherwise protected.",
                    new { candidates = unusedViews.Count, truncated }));
            }

            if (includeEmptySchedules)
            {
                var emptySchedules = FindEmptySchedules(doc, placedViewIds, limit, out var truncated);
                candidates["empty_schedules"] = emptySchedules;
                if (truncated)
                    warnings.Add("Empty schedule candidates were truncated at " + limit.ToString(CultureInfo.InvariantCulture) + ".");

                foreach (var item in emptySchedules.OfType<JObject>())
                {
                    if (item.Value<bool>("safe_to_delete"))
                        proposedDeletes.Add(item.DeepClone());
                }

                steps.Add(WorkflowSupport.Step(
                    "Find Empty Schedules",
                    "ViewSchedule.GetTableData",
                    "succeeded",
                    "Find schedules with no body rows that are not placed on sheets.",
                    new { candidates = emptySchedules.Count, truncated }));
            }

            if (includeNamingOutliers)
            {
                var outliers = FindNamingOutliers(doc, limit, out var truncated);
                candidates["naming_outliers"] = outliers;
                if (truncated)
                    warnings.Add("Naming outlier candidates were truncated at " + limit.ToString(CultureInfo.InvariantCulture) + ".");

                steps.Add(WorkflowSupport.Step(
                    "Find Naming Outliers",
                    "suggest_view_name_corrections",
                    "succeeded",
                    "Find view names with whitespace, illegal characters, or duplicate names.",
                    new { candidates = outliers.Count, truncated }));
            }

            if (!deleteEmptyViews || dryRun || proposedDeletes.Count == 0)
            {
                if (deleteEmptyViews && dryRun)
                {
                    steps.Add(WorkflowSupport.Step(
                        "Delete Cleanup Candidates",
                        "doc.Delete",
                        "skipped",
                        "Dry-run: delete_empty_views=true but dry_run=true.",
                        new { proposed_deletes = proposedDeletes.Count }));
                }

                return CommandResult.Ok(BuildResult(
                    dryRun,
                    "succeeded",
                    steps,
                    warnings,
                    candidates,
                    proposedDeletes,
                    deletedIds,
                    WorkflowSupport.Rollback("Transaction", false, dryRun ? "Dry-run; no transaction opened." : "No delete candidates selected.")));
            }

            using (var tx = new Transaction(doc, "RvtMcp: workflow view cleanup"))
            {
                tx.Start();
                try
                {
                    var idsToDelete = new List<ElementId>();
                    foreach (var item in proposedDeletes.OfType<JObject>())
                    {
                        var rawId = item.Value<long>("element_id");
                        if (!RevitCompat.CanRepresentElementId(rawId))
                            continue;
                        idsToDelete.Add(RevitCompat.ToElementId(rawId));
                    }

                    var deleted = doc.Delete(idsToDelete);
                    foreach (var id in deleted)
                        deletedIds.Add(RevitCompat.GetId(id));

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        throw new InvalidOperationException("Transaction status: " + status);

                    steps.Add(WorkflowSupport.Step(
                        "Delete Cleanup Candidates",
                        "doc.Delete",
                        "succeeded",
                        "Deleted safe unplaced views and empty schedules.",
                        new { requested = idsToDelete.Count, deleted = deletedIds.Count }));

                    return CommandResult.Ok(BuildResult(false, "succeeded", steps, warnings, candidates, proposedDeletes, deletedIds, WorkflowSupport.Rollback("Transaction", false, "Cleanup delete transaction committed.")));
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted())
                        tx.RollBack();
                    steps.Add(WorkflowSupport.Step("Delete Cleanup Candidates", "doc.Delete", "failed", "Deleted safe unplaced views and empty schedules.", null, ex.Message));
                    warnings.Add("Delete failed: " + ex.Message);
                    return CommandResult.Ok(BuildResult(false, "failed", steps, warnings, candidates, proposedDeletes, deletedIds, WorkflowSupport.Rollback("Transaction", true, "Cleanup delete transaction failed and was rolled back.")));
                }
            }
        }

        private JObject BuildResult(
            bool dryRun,
            string status,
            JArray steps,
            IEnumerable<string> warnings,
            JObject candidates,
            JArray proposedDeletes,
            IEnumerable<long> deletedIds,
            JObject rollback)
        {
            var result = WorkflowSupport.Envelope(Name, dryRun, status, steps, deletedIds, Array.Empty<long>(), warnings, rollback);
            result["candidates"] = candidates ?? new JObject();
            result["proposed_deletes"] = proposedDeletes ?? new JArray();
            result["deleted_ids"] = WorkflowSupport.ToJArray(deletedIds);
            return result;
        }

        private static HashSet<long> CollectPlacedViewIds(Document doc)
        {
            var ids = new HashSet<long>();
            foreach (var viewport in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
            {
                try { ids.Add(RevitCompat.GetId(viewport.ViewId)); }
                catch { }
            }
            foreach (var scheduleInstance in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
            {
                try { ids.Add(RevitCompat.GetId(scheduleInstance.ScheduleId)); }
                catch { }
            }
            return ids;
        }

        private static JArray FindUnusedViews(Document doc, HashSet<long> placedIds, int limit, out bool truncated)
        {
            truncated = false;
            var array = new JArray();
            foreach (var view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (array.Count >= limit)
                {
                    truncated = true;
                    break;
                }

                if (view.IsTemplate ||
                    view.ViewType == ViewType.DrawingSheet ||
                    view.ViewType == ViewType.Schedule ||
                    view.ViewType == ViewType.ColumnSchedule ||
                    !SafeCanBePrinted(view))
                    continue;

                var id = RevitCompat.GetId(view.Id);
                if (placedIds.Contains(id))
                    continue;

                var protection = DeleteProtectionReason(doc, view, placedIds);
                array.Add(new JObject
                {
                    ["element_id"] = id,
                    ["name"] = view.Name,
                    ["view_type"] = view.ViewType.ToString(),
                    ["safe_to_delete"] = protection == null,
                    ["protection_reason"] = protection == null ? JValue.CreateNull() : new JValue(protection)
                });
            }
            return array;
        }

        private static JArray FindEmptySchedules(Document doc, HashSet<long> placedIds, int limit, out bool truncated)
        {
            truncated = false;
            var array = new JArray();
            foreach (var schedule in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (array.Count >= limit)
                {
                    truncated = true;
                    break;
                }

                if (schedule.IsTemplate || !IsScheduleLikelyEmpty(schedule))
                    continue;

                var id = RevitCompat.GetId(schedule.Id);
                var placed = placedIds.Contains(id);
                array.Add(new JObject
                {
                    ["element_id"] = id,
                    ["name"] = schedule.Name,
                    ["safe_to_delete"] = !placed && !IsActiveView(doc, schedule),
                    ["is_placed"] = placed,
                    ["protection_reason"] = placed ? "Schedule is placed on a sheet." : IsActiveView(doc, schedule) ? "Schedule is the active view." : null
                });
            }
            return array;
        }

        private static JArray FindNamingOutliers(Document doc, int limit, out bool truncated)
        {
            truncated = false;
            var outliers = new JArray();
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
            var duplicateNames = views.GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var view in views.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (outliers.Count >= limit)
                {
                    truncated = true;
                    break;
                }

                var normalized = WorkflowSupport.NormalizeName(view.Name);
                var reasons = new List<string>();
                if (!string.Equals(normalized, view.Name, StringComparison.Ordinal))
                    reasons.Add("normalized_name_differs");
                if (duplicateNames.Contains(view.Name))
                    reasons.Add("duplicate_name");
                if (string.IsNullOrWhiteSpace(view.Name))
                    reasons.Add("blank_name");

                if (reasons.Count == 0)
                    continue;

                outliers.Add(new JObject
                {
                    ["element_id"] = RevitCompat.GetId(view.Id),
                    ["name"] = view.Name,
                    ["view_type"] = view.ViewType.ToString(),
                    ["suggested"] = normalized,
                    ["reasons"] = JArray.FromObject(reasons)
                });
            }

            return outliers;
        }

        private static string DeleteProtectionReason(Document doc, View view, HashSet<long> placedIds)
        {
            if (IsActiveView(doc, view))
                return "View is the active view.";
            try
            {
                if (view.GetDependentViewIds().Count > 0)
                    return "View has dependent views.";
            }
            catch { }
            if (placedIds.Contains(RevitCompat.GetId(view.Id)))
                return "View is placed on a sheet.";
            return null;
        }

        private static bool IsActiveView(Document doc, View view)
        {
            try { return doc.ActiveView != null && doc.ActiveView.Id == view.Id; }
            catch { return false; }
        }

        private static bool SafeCanBePrinted(View view)
        {
            try { return view.CanBePrinted; }
            catch { return false; }
        }

        private static bool IsScheduleLikelyEmpty(ViewSchedule schedule)
        {
            try
            {
                var body = schedule.GetTableData().GetSectionData(SectionType.Body);
                return body == null || body.NumberOfRows <= 1;
            }
            catch
            {
                return false;
            }
        }
    }
}
