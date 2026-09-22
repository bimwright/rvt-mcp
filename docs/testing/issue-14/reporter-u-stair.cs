using System;
using System.Linq;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;

// Submit this ENTIRE file as the code argument to revit_send_code_to_revit.
// Full-source form: works without the unreleased built-in plugin helper.
// Use an architectural TEST PROJECT. Adds two levels, one U stair and one 3D view.
// Does not save the model. Undo "Issue14: U stair and landing demo" removes the group.
public class McpDynamicScript
{
    public static bool KeepResult = true; // false: validate, then roll back all test elements.
    public static object Run(UIApplication app)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc == null) throw new InvalidOperationException("Open an architectural test project.");
        var doc = uidoc.Document;
        if (doc.IsFamilyDocument || doc.IsReadOnly || doc.IsModifiable)
            throw new InvalidOperationException("Use an editable project outside any edit/transaction mode.");
        if (!new FilteredElementCollector(doc).OfClass(typeof(StairsType)).Any())
            throw new InvalidOperationException("Project needs a component stair type; use an architectural template.");
        string viewName = "Issue14 U Stair " + Guid.NewGuid().ToString("N").Substring(0, 8);
        Level bottom = null, top = null;
        double width = 1200.0 / 304.8, gap = 300.0 / 304.8, tread = 280.0 / 304.8;
        var failures = new Issue14FailuresPreprocessor();
        ElementId stairsId = ElementId.InvalidElementId;
        View3D view = null;
        using (var group = new TransactionGroup(doc, "Issue14: U stair and landing demo"))
        {
            group.Start();
            try
            {
                using (var tx = new Transaction(doc, "Issue14 test levels"))
                {
                    tx.Start();
                    tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions()
                        .SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                    bottom = Level.Create(doc, 0);
                    top = Level.Create(doc, 3200.0 / 304.8);
                    bottom.Name = viewName + " - Bottom";
                    top.Name = viewName + " - Top";
                    if (tx.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Test levels did not commit.");
                }
                using (var scope = new StairsEditScope(doc, "U stair with intermediate landing"))
                {
                    try
                    {
                        stairsId = scope.Start(bottom.Id, top.Id);
                        using (var tx = new Transaction(doc, "Two opposite runs and automatic landing"))
                        {
                            tx.Start();
                            tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions()
                                .SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                            var stairs = (Stairs)doc.GetElement(stairsId);
                            // Use the model\'s default component-stair type; do not edit shared types.
                            stairs.DesiredRisersNumber = 20;
                            stairs.ActualTreadDepth = tread;
                            var p = new XYZ(0, 0, bottom.Elevation);
                            var q = p + XYZ.BasisX * (9 * tread);
                            var run1 = StairsRun.CreateStraightRun(doc, stairsId, Line.CreateBound(p, q), StairsRunJustification.Center);
                            run1.ActualRunWidth = width;
                            doc.Regenerate();
                            var p2 = new XYZ(q.X, p.Y + width + gap, bottom.Elevation + run1.TopElevation);
                            var q2 = p2 - XYZ.BasisX * (9 * tread);
                            var run2 = StairsRun.CreateStraightRun(doc, stairsId, Line.CreateBound(p2, q2), StairsRunJustification.Center);
                            run2.ActualRunWidth = width;
                            doc.Regenerate();
                            if (!StairsLanding.CanCreateAutomaticLanding(doc, run1.Id, run2.Id))
                                throw new InvalidOperationException("Runs cannot form an automatic landing.");
                            var landings = StairsLanding.CreateAutomaticLanding(doc, run1.Id, run2.Id);
                            doc.Regenerate();
                            if (landings.Count != 1 || stairs.GetStairsRuns().Count != 2
                                || stairs.ActualRisersNumber != 20
                                || Math.Abs(run1.TopElevation - run2.BaseElevation) > 1e-6
                                || Math.Abs(bottom.Elevation + run2.TopElevation - top.Elevation) > 1e-6)
                                throw new InvalidOperationException("U-stair topology or elevation check failed.");
                            stairs.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(
                                "Issue #14: U stair, 2 runs + 1 landing, created entirely through send-code.");
                            if (tx.Commit() != TransactionStatus.Committed)
                                throw new InvalidOperationException("Stair transaction rolled back.");
                        }
                        scope.Commit(failures);
                        if (scope.IsActive || failures.HadErrors || doc.GetElement(stairsId) == null)
                            throw new InvalidOperationException("Stair scope did not commit.");
                    }
                    finally { if (scope.IsActive) scope.Cancel(); }
                }
                using (var tx = new Transaction(doc, "U stair presentation"))
                {
                    tx.Start();
                    tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions()
                        .SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                    var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
                        .Cast<ViewFamilyType>().First(v => v.ViewFamily == ViewFamily.ThreeDimensional);
                    view = View3D.CreateIsometric(doc, type.Id);
                    view.Name = viewName;
                    view.DetailLevel = ViewDetailLevel.Fine;
                    view.DisplayStyle = DisplayStyle.HLR;
                    var stairs = (Stairs)doc.GetElement(stairsId);
                    var visible = new List<ElementId> { stairsId };
                    visible.AddRange(stairs.GetStairsRuns());
                    visible.AddRange(stairs.GetStairsLandings());
                    visible.AddRange(stairs.GetAssociatedRailings());
                    view.IsolateElementsTemporary(visible);
                    view.ConvertTemporaryHideIsolateToPermanent();
                    var bounds = stairs.get_BoundingBox(null);
                    var box = new BoundingBoxXYZ {
                        Min = bounds.Min - new XYZ(1, 1, 1),
                        Max = bounds.Max + new XYZ(1, 1, 4)
                    };
                    view.SetSectionBox(box);
                    view.IsSectionBoxActive = true;
                    var centre = (box.Min + box.Max) / 2;
                    view.SetOrientation(new ViewOrientation3D(centre + new XYZ(-24, -30, 28),
                        new XYZ(28, 35, 61.5).Normalize(), new XYZ(24, 30, -28).Normalize()));
                    if (tx.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("View transaction rolled back.");
                }
                if (!KeepResult)
                {
                    var checkedStair = (Stairs)doc.GetElement(stairsId);
                    var checks = new {
                        outcome = "validated_then_rolled_back",
                        runCount = checkedStair.GetStairsRuns().Count,
                        landingCount = checkedStair.GetStairsLandings().Count,
                        risers = checkedStair.ActualRisersNumber,
                        failures.HadWarnings, failures.HadErrors, failures.Messages
                    };
                    group.RollBack();
                    return checks;
                }
                if (group.Assimilate() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Demo group did not commit.");
            }
            catch
            {
                if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
                throw;
            }
        }
        uidoc.ActiveView = view;
        uidoc.Selection.SetElementIds(new List<ElementId>());
        uidoc.GetOpenUIViews().First(v => v.ViewId == view.Id).ZoomToFit();
        var created = (Stairs)doc.GetElement(stairsId);
        return new {
            outcome = failures.HadWarnings ? "committed_with_warnings" : "committed",
            stairsId = stairsId.ToString(), viewId = view.Id.ToString(), view = view.Name,
            fromLevel = bottom.Name, toLevel = top.Name,
            totalHeightMm = (top.Elevation - bottom.Elevation) * 304.8,
            risers = created.ActualRisersNumber, riserHeightMm = created.ActualRiserHeight * 304.8,
            treadDepthMm = created.ActualTreadDepth * 304.8,
            runs = created.GetStairsRuns().Select(id => (StairsRun)doc.GetElement(id)).Select(r => new {
                id = r.Id.ToString(), risers = r.ActualRisersNumber, widthMm = r.ActualRunWidth * 304.8,
                baseMm = r.BaseElevation * 304.8, topMm = r.TopElevation * 304.8 }).ToArray(),
            landingIds = created.GetStairsLandings().Select(id => id.ToString()).ToArray(),
            failures.HadWarnings, failures.HadErrors, failures.Messages
        };


    }
}

public sealed class Issue14FailuresPreprocessor : IFailuresPreprocessor
{
    public List<string> Messages { get; } = new List<string>();
    public bool HadWarnings { get; private set; }
    public bool HadErrors { get; private set; }
    public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
    {
        bool hasError = false;
        foreach (var failure in accessor.GetFailureMessages())
        {
            var severity = failure.GetSeverity();
            Messages.Add(severity + ": " + failure.GetDescriptionText());
            if (severity == FailureSeverity.Warning)
            {
                HadWarnings = true;
                accessor.DeleteWarning(failure);
            }
            else if (severity != FailureSeverity.None) hasError = true;
        }
        if (!hasError) return FailureProcessingResult.Continue;
        HadErrors = true;
        accessor.SetFailureHandlingOptions(
            accessor.GetFailureHandlingOptions().SetClearAfterRollback(true));
        return FailureProcessingResult.ProceedWithRollBack;
    }
}
