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
    public class WorkflowClashReviewHandler : IRevitCommand
    {
        public string Name => "workflow_clash_review";
        public string Description => "Run clash detection, optionally create a review view, color clash hits, and add review markers with an auditable workflow report.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""category_a"", ""category_b""],
  ""properties"": {
    ""category_a"": { ""type"": ""string"" },
    ""category_b"": { ""type"": ""string"" },
    ""view_id"": { ""type"": ""integer"" },
    ""max_pairs"": { ""type"": ""integer"", ""default"": 200, ""minimum"": 1, ""maximum"": 2000 },
    ""create_review_view"": { ""type"": ""boolean"", ""default"": true },
    ""color_hits"": { ""type"": ""boolean"", ""default"": true },
    ""create_markers"": { ""type"": ""boolean"", ""default"": false },
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

            var categoryA = request.Value<string>("category_a");
            var categoryB = request.Value<string>("category_b");
            var viewId = request.Value<long?>("view_id");
            var maxPairs = request.Value<int?>("max_pairs") ?? 200;
            var createReviewView = request.Value<bool?>("create_review_view") ?? true;
            var colorHits = request.Value<bool?>("color_hits") ?? true;
            var createMarkers = request.Value<bool?>("create_markers") ?? false;
            var dryRun = request.Value<bool?>("dry_run") ?? true;
            var continueOnError = request.Value<bool?>("continue_on_error") ?? false;

            if (string.IsNullOrWhiteSpace(categoryA) || string.IsNullOrWhiteSpace(categoryB))
                return CommandResult.Fail("category_a and category_b are required fields.");
            if (maxPairs < 1 || maxPairs > 2000)
                return CommandResult.Fail("max_pairs must be between 1 and 2000.");
            if (viewId.HasValue && !RevitCompat.CanRepresentElementId(viewId.Value))
                return CommandResult.Fail("view_id " + RevitCompat.ElementIdRangeError(viewId.Value));

            View sourceView = null;
            if (viewId.HasValue)
            {
                sourceView = doc.GetElement(RevitCompat.ToElementId(viewId.Value)) as View;
                if (sourceView == null)
                    return CommandResult.Fail("view_id does not resolve to a View: " + viewId.Value.ToString(CultureInfo.InvariantCulture));
            }

            var steps = new JArray();
            var warnings = new List<string>();
            var createdIds = new List<long>();
            var modifiedIds = new HashSet<long>();
            var rollback = WorkflowSupport.Rollback("None", false, "Read-only clash detection only.");

            var clashParams = new JObject
            {
                ["categories_a"] = new JArray(categoryA),
                ["categories_b"] = new JArray(categoryB),
                ["strategy"] = "bbox_then_solid",
                ["max_pairs"] = maxPairs,
                ["max_results"] = Math.Min(maxPairs, 500)
            };
            if (viewId.HasValue)
                clashParams["view_id"] = viewId.Value;

            CommandResult clashResult;
            try
            {
                clashResult = new ClashDetectionHandler().Execute(app, clashParams.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                steps.Add(WorkflowSupport.Step(
                    "Clash Detection",
                    "clash_detection",
                    "failed",
                    "Detect clashes between '" + categoryA + "' and '" + categoryB + "'.",
                    null,
                    ex.Message));
                return CommandResult.Ok(BuildResult(dryRun, "failed", steps, createdIds, modifiedIds, warnings, WorkflowSupport.Rollback("None", false, "Read-only clash detection failed."), new JArray(), null, new JArray()));
            }

            if (!clashResult.Success)
            {
                steps.Add(WorkflowSupport.Step(
                    "Clash Detection",
                    "clash_detection",
                    "failed",
                    "Detect clashes between '" + categoryA + "' and '" + categoryB + "'.",
                    null,
                    clashResult.Error));
                return CommandResult.Ok(BuildResult(dryRun, "failed", steps, createdIds, modifiedIds, warnings, WorkflowSupport.Rollback("None", false, "Read-only clash detection failed."), new JArray(), null, new JArray()));
            }

            var clashData = JObject.FromObject(clashResult.Data);
            var clashes = clashData["clashes"] as JArray ?? new JArray();
            steps.Add(WorkflowSupport.Step(
                "Clash Detection",
                "clash_detection",
                "succeeded",
                "Detect clashes between '" + categoryA + "' and '" + categoryB + "'.",
                new
                {
                    returned = clashes.Count,
                    scanned_a = clashData.Value<int?>("scanned_a") ?? 0,
                    scanned_b = clashData.Value<int?>("scanned_b") ?? 0,
                    truncated = clashData.Value<bool?>("truncated") ?? false
                }));

            if (clashes.Count == 0)
            {
                warnings.Add("No clashes were found; write steps were skipped.");
                return CommandResult.Ok(BuildResult(
                    dryRun,
                    "succeeded",
                    steps,
                    createdIds,
                    modifiedIds,
                    warnings,
                    WorkflowSupport.Rollback("None", false, "No clashes found."),
                    clashes,
                    null,
                    new JArray()));
            }

            if (dryRun)
            {
                if (createReviewView)
                    steps.Add(WorkflowSupport.Step("Create Review View", "View3D.CreateIsometric", "skipped", "Dry-run: review view would be created.", new { planned = true }));
                if (colorHits)
                    steps.Add(WorkflowSupport.Step("Color Clash Hits", "View.SetElementOverrides", "skipped", "Dry-run: clashing elements would be colored.", new { element_count = ExtractElementIds(clashes).Count }));
                if (createMarkers)
                    steps.Add(WorkflowSupport.Step("Create Clash Markers", "TextNote.Create", "skipped", "Dry-run: markers would be created where the target view can host annotation.", new { planned = true }));

                return CommandResult.Ok(BuildResult(
                    true,
                    "succeeded",
                    steps,
                    createdIds,
                    modifiedIds,
                    warnings,
                    WorkflowSupport.Rollback("TransactionGroup", false, "Dry-run; no write operations attempted."),
                    clashes,
                    null,
                    new JArray()));
            }

            View targetView = null;
            var markerIds = new JArray();
            var hadWriteError = false;

            using (var group = new TransactionGroup(doc, "RvtMcp: workflow clash review"))
            {
                group.Start();
                rollback = WorkflowSupport.Rollback("TransactionGroup", false, "Committed clash review write steps.");

                using (var tx = new Transaction(doc, "RvtMcp: clash review"))
                {
                    tx.Start();
                    try
                    {
                        if (createReviewView)
                        {
                            try
                            {
                                targetView = CreateReview3DView(doc);
                                createdIds.Add(RevitCompat.GetId(targetView.Id));
                                steps.Add(WorkflowSupport.Step(
                                    "Create Review View",
                                    "View3D.CreateIsometric",
                                    "succeeded",
                                    "Create a dedicated 3D clash review view.",
                                    new { view_id = RevitCompat.GetId(targetView.Id), name = targetView.Name }));
                            }
                            catch (Exception ex)
                            {
                                hadWriteError = true;
                                steps.Add(WorkflowSupport.Step("Create Review View", "View3D.CreateIsometric", "failed", "Create a dedicated 3D clash review view.", null, ex.Message));
                                if (!continueOnError)
                                    throw;
                            }
                        }

                        if (targetView == null)
                            targetView = ResolveGraphicsView(doc, sourceView);

                        if (colorHits)
                        {
                            try
                            {
                                var colorResult = ApplyClashOverrides(doc, targetView, ExtractElementIds(clashes), modifiedIds);
                                steps.Add(WorkflowSupport.Step(
                                    "Color Clash Hits",
                                    "View.SetElementOverrides",
                                    "succeeded",
                                    "Apply red/orange overrides to clashing elements in the target view.",
                                    colorResult));
                            }
                            catch (Exception ex)
                            {
                                hadWriteError = true;
                                steps.Add(WorkflowSupport.Step("Color Clash Hits", "View.SetElementOverrides", "failed", "Apply red/orange overrides to clashing elements in the target view.", null, ex.Message));
                                if (!continueOnError)
                                    throw;
                            }
                        }

                        if (createMarkers)
                        {
                            try
                            {
                                var result = CreateMarkers(doc, targetView, clashes, markerIds);
                                foreach (var idToken in markerIds)
                                    createdIds.Add(idToken.Value<long>());
                                steps.Add(WorkflowSupport.Step(
                                    "Create Clash Markers",
                                    "TextNote.Create",
                                    result.Value<int>("created") > 0 ? "succeeded" : "skipped",
                                    "Create text-note clash markers in annotatable target views.",
                                    result));
                            }
                            catch (Exception ex)
                            {
                                hadWriteError = true;
                                steps.Add(WorkflowSupport.Step("Create Clash Markers", "TextNote.Create", "failed", "Create text-note clash markers in annotatable target views.", null, ex.Message));
                                if (!continueOnError)
                                    throw;
                            }
                        }

                        var status = tx.Commit();
                        if (status != TransactionStatus.Committed)
                            throw new InvalidOperationException("Clash review transaction did not commit. Status: " + status);
                    }
                    catch
                    {
                        if (tx.HasStarted())
                            tx.RollBack();
                        group.RollBack();
                        rollback = WorkflowSupport.Rollback("TransactionGroup", true, "A write step failed and continue_on_error=false.");
                        return CommandResult.Ok(BuildResult("failed", steps, createdIds, modifiedIds, warnings, rollback, clashes, targetView, markerIds, dryRun));
                    }
                }

                if (group.Assimilate() != TransactionStatus.Committed)
                    return CommandResult.Fail("Revit did not commit the clash review transaction group; model changes were not confirmed.");
            }

            return CommandResult.Ok(BuildResult(
                hadWriteError ? "partial" : "succeeded",
                steps,
                createdIds,
                modifiedIds,
                warnings,
                rollback,
                clashes,
                targetView,
                markerIds,
                dryRun));
        }

        private JObject BuildResult(
            bool dryRun,
            string status,
            JArray steps,
            IEnumerable<long> createdIds,
            IEnumerable<long> modifiedIds,
            IEnumerable<string> warnings,
            JObject rollback,
            JArray clashes,
            View reviewView,
            JArray markerIds)
        {
            var result = WorkflowSupport.Envelope(Name, dryRun, status, steps, createdIds, modifiedIds, warnings, rollback);
            result["clashes"] = clashes ?? new JArray();
            result["review_view_id"] = reviewView == null ? JValue.CreateNull() : new JValue(RevitCompat.GetId(reviewView.Id));
            result["marker_ids"] = markerIds ?? new JArray();
            return result;
        }

        private JObject BuildResult(
            string status,
            JArray steps,
            IEnumerable<long> createdIds,
            IEnumerable<long> modifiedIds,
            IEnumerable<string> warnings,
            JObject rollback,
            JArray clashes,
            View reviewView,
            JArray markerIds,
            bool dryRun)
        {
            return BuildResult(dryRun, status, steps, createdIds, modifiedIds, warnings, rollback, clashes, reviewView, markerIds);
        }

        private static View3D CreateReview3DView(Document doc)
        {
            var type = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.ThreeDimensional);
            if (type == null)
                throw new InvalidOperationException("No 3D ViewFamilyType is available.");

            var view = View3D.CreateIsometric(doc, type.Id);
            view.Name = WorkflowSupport.UniqueName(
                doc,
                new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<Element>(),
                "RvtMcp Clash Review");
            return view;
        }

        private static View ResolveGraphicsView(Document doc, View preferred)
        {
            if (preferred != null && SafeAllowsOverrides(preferred))
                return preferred;
            if (doc.ActiveView != null && SafeAllowsOverrides(doc.ActiveView))
                return doc.ActiveView;

            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && SafeAllowsOverrides(v));
            if (existing != null)
                return existing;

            throw new InvalidOperationException("No target view supports element graphics overrides. Enable create_review_view or provide a graphics-overridable view_id.");
        }

        private static bool SafeAllowsOverrides(View view)
        {
            try { return view != null && !view.IsTemplate && view.AreGraphicsOverridesAllowed(); }
            catch { return false; }
        }

        private static List<long> ExtractElementIds(JArray clashes)
        {
            var ids = new HashSet<long>();
            foreach (var clash in clashes.OfType<JObject>())
            {
                var a = clash["a"]?["element_id"]?.Value<long?>();
                var b = clash["b"]?["element_id"]?.Value<long?>();
                if (a.HasValue)
                    ids.Add(a.Value);
                if (b.HasValue)
                    ids.Add(b.Value);
            }
            return ids.ToList();
        }

        private static JObject ApplyClashOverrides(Document doc, View view, List<long> elementIds, HashSet<long> modifiedIds)
        {
            if (view == null)
                throw new InvalidOperationException("Target view is null.");
            if (!SafeAllowsOverrides(view))
                throw new InvalidOperationException("View '" + view.Name + "' does not allow graphics overrides.");

            var red = new Color(220, 40, 40);
            var orange = new Color(255, 145, 0);
            var solidFill = FindSolidFill(doc);
            var failed = new JArray();
            var index = 0;

            foreach (var rawId in elementIds)
            {
                try
                {
                    if (!RevitCompat.CanRepresentElementId(rawId))
                    {
                        failed.Add(new JObject { ["element_id"] = rawId, ["error"] = RevitCompat.ElementIdRangeError(rawId) });
                        continue;
                    }

                    var id = RevitCompat.ToElementId(rawId);
                    var element = doc.GetElement(id);
                    if (element == null)
                    {
                        failed.Add(new JObject { ["element_id"] = rawId, ["error"] = "Element not found." });
                        continue;
                    }

                    var settings = view.GetElementOverrides(id) ?? new OverrideGraphicSettings();
                    var color = index % 2 == 0 ? red : orange;
                    settings.SetProjectionLineColor(color);
                    settings.SetSurfaceForegroundPatternColor(color);
                    settings.SetSurfaceTransparency(35);
                    if (solidFill != null)
                    {
                        settings.SetSurfaceForegroundPatternId(solidFill.Id);
                        settings.SetSurfaceForegroundPatternVisible(true);
                    }
                    view.SetElementOverrides(id, settings);
                    modifiedIds.Add(rawId);
                    index++;
                }
                catch (Exception ex)
                {
                    failed.Add(new JObject { ["element_id"] = rawId, ["error"] = ex.Message });
                }
            }

            return new JObject
            {
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["view_name"] = view.Name,
                ["requested"] = elementIds.Count,
                ["modified"] = modifiedIds.Count,
                ["failed"] = failed
            };
        }

        private static FillPatternElement FindSolidFill(Document doc)
        {
            try
            {
                return new FilteredElementCollector(doc)
                    .OfClass(typeof(FillPatternElement))
                    .Cast<FillPatternElement>()
                    .FirstOrDefault(f =>
                    {
                        try { return f.GetFillPattern().IsSolidFill; }
                        catch { return false; }
                    });
            }
            catch
            {
                return null;
            }
        }

        private static JObject CreateMarkers(Document doc, View view, JArray clashes, JArray markerIds)
        {
            if (!CanHostText(view))
            {
                return new JObject
                {
                    ["created"] = 0,
                    ["skipped"] = clashes.Count,
                    ["reason"] = "Target view '" + (view?.Name ?? "<null>") + "' cannot host text notes."
                };
            }

            var textType = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType))
                .Cast<TextNoteType>()
                .FirstOrDefault();
            if (textType == null)
                throw new InvalidOperationException("No TextNoteType is available for marker creation.");

            var created = 0;
            foreach (var clash in clashes.OfType<JObject>())
            {
                var center = GetClashCenter(clash);
                if (center == null)
                    continue;

                var note = TextNote.Create(
                    doc,
                    view.Id,
                    center,
                    "CLASH " + (created + 1).ToString(CultureInfo.InvariantCulture),
                    textType.Id);
                markerIds.Add(RevitCompat.GetId(note.Id));
                created++;
            }

            return new JObject
            {
                ["created"] = created,
                ["skipped"] = clashes.Count - created
            };
        }

        private static bool CanHostText(View view)
        {
            if (view == null || view.IsTemplate)
                return false;

            switch (view.ViewType)
            {
                case ViewType.FloorPlan:
                case ViewType.CeilingPlan:
                case ViewType.EngineeringPlan:
                case ViewType.AreaPlan:
                case ViewType.Section:
                case ViewType.Elevation:
                case ViewType.Detail:
                case ViewType.DraftingView:
                    return true;
                default:
                    return false;
            }
        }

        private static XYZ GetClashCenter(JObject clash)
        {
            try
            {
                var min = clash["bbox_overlap"]?["min"];
                var max = clash["bbox_overlap"]?["max"];
                if (min == null || max == null)
                    return null;

                var x = (min.Value<double>("x") + max.Value<double>("x")) / 2.0 / WorkflowSupport.FeetToMm;
                var y = (min.Value<double>("y") + max.Value<double>("y")) / 2.0 / WorkflowSupport.FeetToMm;
                var z = (min.Value<double>("z") + max.Value<double>("z")) / 2.0 / WorkflowSupport.FeetToMm;
                return new XYZ(x, y, z);
            }
            catch
            {
                return null;
            }
        }
    }
}
