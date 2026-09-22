using Autodesk.Revit.DB.Architecture;
if (app.Application.VersionNumber != "2027" || doc.Title != "Snowdon Towers Sample Architectural")
    throw new InvalidOperationException("This demo is pinned to the owner's Revit 2027 sample.");
if (new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Any(v => v.Name == "Issue14 - Send-code Stair Demo"))
    throw new InvalidOperationException("Demo view already exists; inspect it instead of creating a duplicate.");

var bottom = (Level)doc.GetElement(new ElementId(593142L));
var top = (Level)doc.GetElement(new ElementId(593177L));
var failures = new RvtMcp.Plugin.SafeFailuresPreprocessor();
ElementId stairsId = ElementId.InvalidElementId;
ElementId runId = ElementId.InvalidElementId;
View3D demoView = null;
using (var group = new TransactionGroup(doc, "Issue14: create send-code stair demo"))
{
    group.Start();
    try
    {
        using (var scope = new StairsEditScope(doc, "Issue14 stair"))
        {
            try
            {
                stairsId = scope.Start(bottom.Id, top.Id);
                using (var tx = new Transaction(doc, "Create straight stair run"))
                {
                    tx.Start();
                    tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions()
                        .SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                    var stairs = (Stairs)doc.GetElement(stairsId);
                    if (stairs.GetTypeId().Value != 168814L) stairs.ChangeTypeId(new ElementId(168814L));
                    stairs.DesiredRisersNumber = (int)Math.Ceiling((top.Elevation - bottom.Elevation) * 304.8 / 175.0);
                    stairs.ActualTreadDepth = 280.0 / 304.8;
                    var start = new XYZ(1000, 1000, bottom.Elevation);
                    var end = start + XYZ.BasisX * ((stairs.DesiredRisersNumber - 1) * stairs.ActualTreadDepth);
                    var run = StairsRun.CreateStraightRun(doc, stairsId, Line.CreateBound(start, end), StairsRunJustification.Center);
                    run.ActualRunWidth = 1200.0 / 304.8;
                    runId = run.Id;
                    stairs.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set("Issue #14: created entirely through revit_send_code_to_revit; demo only.");
                    if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Stair transaction rolled back.");
                }
                scope.Commit(failures);
                if (failures.HadErrors || doc.GetElement(stairsId) == null) throw new InvalidOperationException("Stair scope did not commit.");
            }
            finally
            {
                if (scope.IsActive) scope.Cancel();
            }
        }
        using (var tx = new Transaction(doc, "Create isolated stair demo view"))
        {
            tx.Start();
            tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions()
                .SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
            var viewType = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>().First(v => v.ViewFamily == ViewFamily.ThreeDimensional);
            demoView = View3D.CreateIsometric(doc, viewType.Id);
            demoView.Name = "Issue14 - Send-code Stair Demo";
            demoView.DetailLevel = ViewDetailLevel.Fine;
            demoView.DisplayStyle = DisplayStyle.Shading;
            var stairs = (Stairs)doc.GetElement(stairsId);
            var visible = new List<ElementId> { stairsId };
            visible.AddRange(stairs.GetStairsRuns());
            visible.AddRange(stairs.GetStairsLandings());
            visible.AddRange(stairs.GetAssociatedRailings());
            demoView.IsolateElementsTemporary(visible);
            demoView.ConvertTemporaryHideIsolateToPermanent();
            var bounds = stairs.get_BoundingBox(null);
            var box = new BoundingBoxXYZ {
                Min = bounds.Min - new XYZ(3, 3, 2),
                Max = bounds.Max + new XYZ(3, 3, 5)
            };
            demoView.SetSectionBox(box);
            demoView.IsSectionBoxActive = true;
            var centre = (box.Min + box.Max) / 2.0;
            demoView.SetOrientation(new ViewOrientation3D(centre + new XYZ(20, -20, 20),
                new XYZ(-1, 1, 2).Normalize(), new XYZ(-1, 1, -1).Normalize()));
            if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Demo view transaction rolled back.");
        }
        if (group.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException("Demo group did not commit.");
    }
    catch
    {
        if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
        throw;
    }
}
uidoc.ActiveView = demoView;
uidoc.Selection.SetElementIds(new List<ElementId>());
var uiView = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == demoView.Id);
uiView?.ZoomToFit();
var created = (Stairs)doc.GetElement(stairsId);
return new {
    outcome = failures.HadWarnings ? "committed_with_warnings" : "committed",
    stairsId = stairsId.Value, runId = runId.Value, viewId = demoView.Id.Value, view = demoView.Name,
    risers = created.ActualRisersNumber, riserHeightMm = created.ActualRiserHeight * 304.8,
    treadDepthMm = created.ActualTreadDepth * 304.8, widthMm = 1200,
    totalHeightMm = (top.Elevation - bottom.Elevation) * 304.8,
    failures.HadWarnings, failures.HadErrors, failures.Messages
};
