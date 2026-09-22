// Revit 2027 diagnostic. Every case rolls back; run only in the dedicated test copy.
using System;
using System.Linq;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using RvtMcp.Plugin;

public class McpDynamicScript
{
    public static object Run(UIApplication app)
    {
        var doc = app.ActiveUIDocument.Document;
        if (!doc.Title.StartsWith("Review-MEP-2027")) throw new Exception("Dedicated test model required.");
        var before = Ids(doc);
        var modified = doc.IsModified;
        var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First();
        var pipeType = new FilteredElementCollector(doc).OfClass(typeof(PipeType)).FirstElementId();
        var pipeSystem = new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).FirstElementId();
        var ductTypes = new FilteredElementCollector(doc).OfClass(typeof(DuctType)).Cast<DuctType>().ToArray();
        var ductSystem = new FilteredElementCollector(doc).OfClass(typeof(MechanicalSystemType)).FirstElementId();
        var results = new List<object>();
        foreach (var kind in new[] { "pipe", "round_duct", "rectangular_duct" })
        foreach (var gapMm in new[] { 0.0, 1.0, 100.0 })
        foreach (var elbow in new[] { false, true })
        {
            string error = null; object data = null;
            using (var group = new TransactionGroup(doc, "Review fitting probe"))
            {
                group.Start();
                try
                {
                    var a = new XYZ(1000, 1000, level.Elevation + 10);
                    var joint = a + new XYZ(10, 0, 0);
                    var b = joint + new XYZ(gapMm / 304.8, 0, 0);
                    var end = b + (elbow ? new XYZ(0, 10, 0) : new XYZ(10, 0, 0));
                    MEPCurve first, second;
                    using (var tx = new Transaction(doc, "Create probe curves"))
                    {
                        tx.Start();
                        if (kind == "pipe")
                        {
                            first = Pipe.Create(doc, pipeSystem, pipeType, level.Id, a, joint);
                            second = Pipe.Create(doc, pipeSystem, pipeType, level.Id, b, end);
                        }
                        else
                        {
                            var shape = kind == "round_duct" ? ConnectorProfileType.Round : ConnectorProfileType.Rectangular;
                            var type = ductTypes.First(t => t.Shape == shape);
                            first = Duct.Create(doc, ductSystem, type.Id, level.Id, a, joint);
                            second = Duct.Create(doc, ductSystem, type.Id, level.Id, b, end);
                        }
                        if (tx.Commit() != TransactionStatus.Committed) throw new Exception("Fixture commit failed.");
                    }
                    var c1 = first.ConnectorManager.Connectors.Cast<Connector>().OrderBy(c => c.Origin.DistanceTo(joint)).First();
                    var c2 = second.ConnectorManager.Connectors.Cast<Connector>().OrderBy(c => c.Origin.DistanceTo(b)).First();
                    var originalIds = Ids(doc);
                    var failures = new SafeFailuresPreprocessor();
                    using (var tx = new Transaction(doc, "Raw ConnectTo"))
                    {
                        tx.Start();
                        tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
                        c1.ConnectTo(c2);
                        doc.Regenerate();
                        var added = Ids(doc).Except(originalIds).Select(id => new { id, category = doc.GetElement(new ElementId(id)).Category?.Name }).ToArray();
                        data = new { direct = c1.IsConnectedTo(c2), firstConnected = c1.IsConnected, secondConnected = c2.IsConnected, added, status = tx.Commit().ToString(), failures.Messages };
                    }
                }
                catch (Exception ex) { error = ex.Message; }
                finally { group.RollBack(); }
            }
            results.Add(new { kind, gapMm, elbow, error, data, restored = before.SequenceEqual(Ids(doc)) && modified == doc.IsModified });
        }
        return new { model = doc.Title, revit = app.Application.VersionNumber, results, restored = before.SequenceEqual(Ids(doc)) && modified == doc.IsModified };
    }
    private static long[] Ids(Document doc) => new FilteredElementCollector(doc)
        .WherePasses(new LogicalOrFilter(new ElementIsElementTypeFilter(), new ElementIsElementTypeFilter(true)))
        .ToElementIds().Select(id => id.Value).OrderBy(id => id).ToArray();
}
