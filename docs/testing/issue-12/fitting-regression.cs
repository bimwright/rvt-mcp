// Production handler against real Revit connectors. All fixtures roll back.
using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;

public class McpDynamicScript
{
    public static object Run(UIApplication app)
    {
        const bool candidate = false;
        var doc = app.ActiveUIDocument.Document;
        if (!doc.Title.StartsWith("Review-MEP-2027")) throw new Exception("Dedicated test model required.");
        IRevitCommand handler = new RvtMcp.Plugin.Handlers.ConnectMepElementsHandler();
        if (candidate)
        {
            var root = @"D:\Projects\bimwright\rvt-mcp\src\shared";
            var names = new[] { "Handlers/ConnectMepElementsHandler.cs", "Handlers/MepConnectionSystemType.cs", "Infrastructure/RevitCompat.cs" };
            var sources = names.Select(n => File.ReadAllText(Path.Combine(root, n))
                .Replace("ConnectMepElementsHandler", "ReviewConnectHandler")
                .Replace("MepConnectionSystemType", "ReviewSystemType")
                .Replace("RevitCompat", "ReviewCompat"));
            var refs = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .GroupBy(a => a.GetName().Name).Select(g => g.OrderByDescending(a => a.GetName().Version).First())
                .Select(a => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(a.Location));
            var options = new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(preprocessorSymbols: new[] { "REVIT2024_OR_GREATER", "REVIT2027_OR_GREATER" });
            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("ReviewConnection_" + Guid.NewGuid().ToString("N"),
                sources.Select(s => Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(s, options)), refs,
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
            using (var output = new MemoryStream())
            {
                var result = compilation.Emit(output);
                if (!result.Success) throw new Exception(string.Join("\n", result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)));
                handler = (IRevitCommand)Activator.CreateInstance(Assembly.Load(output.ToArray()).GetType("RvtMcp.Plugin.Handlers.ReviewConnectHandler"));
            }
        }
        var before = Ids(doc); var modified = doc.IsModified;
        var results = new List<object>();
        int failed = 0;
        var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First();
        foreach (var kind in new[] { "pipe", "duct" })
        foreach (var explicitPorts in new[] { false, true })
        {
            object data = null; string error = null; bool pass = false;
            using (var group = new TransactionGroup(doc, "Review fitting regression"))
            {
                group.Start();
                try
                {
                    MEPCurve first, second; FamilyInstance fitting;
                    var start = new XYZ(1000, 1000, level.Elevation + 10);
                    var joint = start + new XYZ(10, 0, 0);
                    using (var tx = new Transaction(doc, "Real elbow fixture"))
                    {
                        tx.Start();
                        tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetFailuresPreprocessor(new SafeFailuresPreprocessor()).SetClearAfterRollback(true));
                        if (kind == "pipe")
                        {
                            var type = new FilteredElementCollector(doc).OfClass(typeof(PipeType)).FirstElementId();
                            var system = new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).FirstElementId();
                            first = Pipe.Create(doc, system, type, level.Id, start, joint);
                            second = Pipe.Create(doc, system, type, level.Id, joint, joint + new XYZ(0, 10, 0));
                        }
                        else
                        {
                            var type = new FilteredElementCollector(doc).OfClass(typeof(DuctType)).Cast<DuctType>().First(t => t.Shape == ConnectorProfileType.Round).Id;
                            var system = new FilteredElementCollector(doc).OfClass(typeof(MechanicalSystemType)).FirstElementId();
                            first = Duct.Create(doc, system, type, level.Id, start, joint);
                            second = Duct.Create(doc, system, type, level.Id, joint, joint + new XYZ(0, 10, 0));
                        }
                        fitting = doc.Create.NewElbowFitting(At(first, joint), At(second, joint));
                        if (tx.Commit() != TransactionStatus.Committed) throw new Exception("Fixture failed.");
                    }
                    var c1 = FittingPort(first, fitting); var c2 = FittingPort(second, fitting);
                    var fixtureIds = Ids(doc);
                    var geometry = new[] { first, second }.SelectMany(c => c.ConnectorManager.Connectors.Cast<Connector>())
                        .OrderBy(c => c.Owner.Id.Value).ThenBy(c => c.Id)
                        .Select(c => new { owner = c.Owner.Id.Value, port = c.Id, open = !c.IsConnected, x = c.Origin.X, y = c.Origin.Y, z = c.Origin.Z }).ToArray();
                    var args = new JObject { { "element_id_1", first.Id.Value }, { "element_id_2", second.Id.Value } };
                    if (explicitPorts) { args["connector_index_1"] = c1.Id; args["connector_index_2"] = c2.Id; }
                    var result = handler.Execute(app, args.ToString());
                    var response = result.Data == null ? new JObject() : JObject.FromObject(result.Data);
                    var afterGeometry = new[] { first, second }.SelectMany(c => c.ConnectorManager.Connectors.Cast<Connector>())
                        .OrderBy(c => c.Owner.Id.Value).ThenBy(c => c.Id)
                        .Select(c => new { owner = c.Owner.Id.Value, port = c.Id, open = !c.IsConnected, x = c.Origin.X, y = c.Origin.Y, z = c.Origin.Z }).ToArray();
                    pass = result.Success && (bool?)response["connected"] == true && (bool?)response["already_connected"] == true
                        && !c1.IsConnectedTo(c2) && fixtureIds.SequenceEqual(Ids(doc)) && geometry.SequenceEqual(afterGeometry);
                    data = new { result.Success, result.Error, response, fittingId = fitting.Id.Value, direct = c1.IsConnectedTo(c2),
                        firstRefs = c1.AllRefs.Cast<Connector>().Select(c => new { owner = c.Owner.Id.Value, port = c.Id }).ToArray(),
                        secondRefs = c2.AllRefs.Cast<Connector>().Select(c => new { owner = c.Owner.Id.Value, port = c.Id }).ToArray(),
                        geometryUnchanged = geometry.SequenceEqual(afterGeometry), inventoryUnchanged = fixtureIds.SequenceEqual(Ids(doc)) };
                }
                catch (Exception ex) { error = ex.ToString(); }
                finally { group.RollBack(); }
            }
            var restored = before.SequenceEqual(Ids(doc)) && doc.IsModified == modified;
            pass = pass && restored;
            if (!pass) failed++;
            results.Add(new { kind, explicitPorts, pass, error, restored, data });
        }
        return new { mode = candidate ? "candidate source" : "installed plugin", model = doc.Title, revit = app.Application.VersionNumber,
            build = app.Application.VersionBuild, handlerMvid = handler.GetType().Assembly.ManifestModule.ModuleVersionId, total = results.Count, failed, results,
            restored = before.SequenceEqual(Ids(doc)) && doc.IsModified == modified };
    }
    static Connector At(MEPCurve curve, XYZ p) => curve.ConnectorManager.Connectors.Cast<Connector>().OrderBy(c => c.Origin.DistanceTo(p)).First();
    static Connector FittingPort(MEPCurve curve, FamilyInstance fitting) => curve.ConnectorManager.Connectors.Cast<Connector>()
        .Single(c => c.AllRefs.Cast<Connector>().Any(r => r.Owner.Id == fitting.Id));
    static long[] Ids(Document doc) => new FilteredElementCollector(doc)
        .WherePasses(new LogicalOrFilter(new ElementIsElementTypeFilter(), new ElementIsElementTypeFilter(true)))
        .ToElementIds().Select(id => id.Value).OrderBy(id => id).ToArray();
}
