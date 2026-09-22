Thanks for the detailed report. We fixed the source-wrapper detection and added an opt-in failure helper in source commit f35a9575362a9eafbac6e381363ee6751c2e1406. Those additions are not yet in a published release.

The [verification record](https://github.com/bimwright/rvt-mcp/blob/master/docs/testing/issue-14/README.md) includes real straight and U-shaped stairs created through send-code, exported images, and Revit 2022/2027 results. We have not reproduced your Revit 2025 crash: our 100 mm tread test produced a Warning, while our Error rollback test used an injected Error. A separate earlier local Revit 2027 crash also remains unattributed.

These images show the retained Revit 2027 demonstrations described in that record, not a run on the reporter's Revit 2025 environment.

**Straight stair created through send-code:**

![Straight stair created through send-code in Revit 2027](https://raw.githubusercontent.com/bimwright/rvt-mcp/0426fb633e2390e2e7ea9d8a58a81a9ef7ac0cbc/docs/testing/issue-14/stair-demo-2027.png)

**U stair with two runs and an intermediate landing:**

![U stair with two runs and an intermediate landing in Revit 2027](https://raw.githubusercontent.com/bimwright/rvt-mcp/0426fb633e2390e2e7ea9d8a58a81a9ef7ac0cbc/docs/testing/issue-14/u-stair-iso-2027.png)

The U-stair image reflects the subsequent instance-only railing adjustment documented in the verification record; the original railing warning remains recorded.

You can try the complete code below without the new built-in helper: it uses the existing full-source `McpDynamicScript.Run(UIApplication)` entrypoint and includes its own `IFailuresPreprocessor`. It needs no Reflection.Emit, sample model IDs, or recompilation of the plugin. This is the documented full-source workaround for v0.6.1; the exact portable payload has been checked on our updated Revit 2027 plugin in rollback mode, not on Revit 2025/v0.6.1.

1. Open an **architectural test project** with a component stair type, outside any edit mode.
2. Ask your MCP client: **Call `revit_send_code_to_revit` once with the entire C# block below as the `code` argument. Keep the full class and imports. Return the tool's complete result, including warning messages.**
3. The default keeps two new test levels, a U stair with 10+10 risers and one intermediate landing, and an isolated iso view. It uses a 3,200 mm height, 280 mm tread, and 1,200 mm run width at the project origin. It does not save the model. Undo the named demo group to remove it. Each invocation creates a fresh demo.
4. If your stair/railing defaults raise warnings, `committed_with_warnings` and `Messages` must be reviewed. Warnings are recorded before removal from Revit failure processing; this is not a compliance check. Our sample's Cable Railing warned about rail continuity.
5. Please share your Revit build and full tool result. If your original crash persists, please include its failing script and a minimal model.

A ready-made [JSON tool argument](https://github.com/bimwright/rvt-mcp/blob/master/docs/testing/issue-14/reporter-u-stair.tool-arguments.json) is also available.

We considered your proposal for a native `create_stairs` tool and are deferring it in favor of a [conversation and send-code workflow](https://github.com/bimwright/rvt-mcp/blob/master/docs/stairs-workflow.md). It combines tested examples with targeted questions about levels, layout, dimensions, and railing intent. For a project-specific stair, the agent should resolve those choices with you and adapt the code; it should not silently substitute railing types or change geometry to eliminate warnings. The standalone script below remains a test demo with its explicitly stated defaults, not a project design template. This documentation decision does not establish a fix for the reported crash.

<details>
<summary>Complete send-code payload — copy the whole block</summary>

```csharp
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
```
</details>
