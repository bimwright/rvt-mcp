using Autodesk.Revit.DB.Architecture;
// Fixture: the saved Snowdon-based Issue14 demo, Revit 2027.
if (app.Application.VersionNumber != "2027" || doc.Title != "Issue14-stairs-demo-2027")
    throw new InvalidOperationException("Open the saved Revit 2027 Issue14 demo first.");
const string viewName = "Issue14 - U Stair - ISO";
if (new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Any(v => v.Name == viewName))
    throw new InvalidOperationException("U-stair demo already exists.");
var bottom = (Level)doc.GetElement(new ElementId(593177L)); // L2
var top = (Level)doc.GetElement(new ElementId(593147L)); // L3
double width = 1200.0 / 304.8, gap = 300.0 / 304.8, tread = 280.0 / 304.8;
var failures = new RvtMcp.Plugin.SafeFailuresPreprocessor();
ElementId stairsId = ElementId.InvalidElementId;
View3D view = null;
using (var group = new TransactionGroup(doc, "Issue14: U stair and landing demo"))
{
    group.Start();
    try
    {
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
                    stairs.ChangeTypeId(new ElementId(168814L));
                    stairs.DesiredRisersNumber = 20;
                    stairs.ActualTreadDepth = tread;
                    var p = new XYZ(1030, 1000, bottom.Elevation);
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
            view.DisplayStyle = DisplayStyle.Shading;
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
    stairsId = stairsId.Value, viewId = view.Id.Value, view = view.Name,
    fromLevel = bottom.Name, toLevel = top.Name,
    totalHeightMm = (top.Elevation - bottom.Elevation) * 304.8,
    risers = created.ActualRisersNumber, riserHeightMm = created.ActualRiserHeight * 304.8,
    treadDepthMm = created.ActualTreadDepth * 304.8,
    runs = created.GetStairsRuns().Select(id => (StairsRun)doc.GetElement(id)).Select(r => new {
        id = r.Id.Value, risers = r.ActualRisersNumber, widthMm = r.ActualRunWidth * 304.8,
        baseMm = r.BaseElevation * 304.8, topMm = r.TopElevation * 304.8 }).ToArray(),
    landingIds = created.GetStairsLandings().Select(id => id.Value).ToArray(),
    failures.HadWarnings, failures.HadErrors, failures.Messages
};
