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
        var candidate = false; // Final acceptance uses the restarted, installed plugin.
        IRevitCommand create, connect;
        if (candidate)
        {
            var root = @"D:\Projects\bimwright\rvt-mcp\src\shared";
            var names = new[] {"Handlers/CreatePipeHandler.cs", "Handlers/ConnectMepElementsHandler.cs", "Handlers/MepConnectionSystemType.cs", "Infrastructure/RevitCompat.cs"};
            var sources = names.Select(n => File.ReadAllText(Path.Combine(root,n))
                .Replace("CreatePipeHandler", "Issue12CreatePipeHandler")
                .Replace("ConnectMepElementsHandler", "Issue12ConnectMepElementsHandler")
                .Replace("MepConnectionSystemType", "Issue12ConnectionSystemType")
                .Replace("RevitCompat", "Issue12RevitCompat"));
            var references = AppDomain.CurrentDomain.GetAssemblies().Where(a=>!a.IsDynamic&&!string.IsNullOrEmpty(a.Location))
                .GroupBy(a=>a.GetName().Name).Select(g=>g.OrderByDescending(a=>a.GetName().Version).First())
                .Select(a=>Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(a.Location));
            var options = new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(preprocessorSymbols:new[]{"REVIT2024_OR_GREATER","REVIT2027_OR_GREATER"});
            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("Issue12Candidate_"+Guid.NewGuid().ToString("N"),
                sources.Select(s=>Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(s,options)), references,
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
            using(var output=new MemoryStream())
            {
                var compiled=compilation.Emit(output);
                if(!compiled.Success) return new {compileErrors=compiled.Diagnostics.Where(d=>d.Severity==Microsoft.CodeAnalysis.DiagnosticSeverity.Error).Select(d=>d.ToString()).ToArray()};
                var assembly=Assembly.Load(output.ToArray());
                create=(IRevitCommand)Activator.CreateInstance(assembly.GetType("RvtMcp.Plugin.Handlers.Issue12CreatePipeHandler"));
                connect=(IRevitCommand)Activator.CreateInstance(assembly.GetType("RvtMcp.Plugin.Handlers.Issue12ConnectMepElementsHandler"));
            }
        }
        else
        {
            create=new RvtMcp.Plugin.Handlers.CreatePipeHandler();
            connect=new RvtMcp.Plugin.Handlers.ConnectMepElementsHandler();
        }
        return new Issue12LiveSuite(app,create,connect).Run(candidate);
    }
}

public class Issue12LiveSuite
{
    readonly UIApplication app;
    readonly Document doc;
    readonly IRevitCommand create,connect;
    readonly PipeType pipeType;
    readonly PipingSystemType sanitary,other;
    readonly Level level;
    readonly XYZ start,joint,end;
    readonly List<object> results=new List<object>();
    int failed;
    public Issue12LiveSuite(UIApplication a,IRevitCommand cr,IRevitCommand co)
    {
        app=a;doc=a.ActiveUIDocument.Document;create=cr;connect=co;
        pipeType=new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>().First();
        var types=new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>().ToList();
        sanitary=types.First(t=>t.SystemClassification==MEPSystemClassification.Sanitary);
        other=types.First(t=>t.Id!=sanitary.Id);
        level=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().First();
        start=new XYZ(1000,1000,level.Elevation+10);joint=start+new XYZ(10,0,0);end=joint+new XYZ(10,0,0);
    }
    public object Run(bool candidate)
    {

        foreach(var angle in new[]{false,true}) foreach(var raw in new[]{true,false})
        Case((angle?"elbow":"reducer")+(raw?"_raw_api":"_installed_handler"),()=>{
            var p=Pipe(sanitary.Id,start,joint);var q=Pipe(sanitary.Id,joint,angle?joint+new XYZ(0,10,0):end);
            if(!angle)Tx(()=>q.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(2.0/12.0));
            var before=Ids(); object response=null;
            if(raw)Tx(()=>{At(p,joint).ConnectTo(At(q,joint));doc.Regenerate();});
            else response=Call(connect,Pair(p,q));
            return new{response,direct=Joined(p,q),pConnected=At(p,joint).IsConnected,qConnected=At(q,joint).IsConnected,added=Ids().Except(before).Select(id=>new{id,category=doc.GetElement(new ElementId(id)).Category?.Name}).ToArray()};
        });
        var plugin=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="RvtMcp.Plugin");
        return new{mode=candidate?"candidate source":"installed plugin",model=doc.Title,revit=app.Application.VersionNumber,pluginMvid=plugin.ManifestModule.ModuleVersionId.ToString(),total=results.Count,failed,modified=doc.IsModified,results};
    }
    void Case(string name,Func<object> test)
    {
        var before=Ids();var modified=doc.IsModified;object data=null;string error=null;bool restored=false;
        using(var group=new TransactionGroup(doc,"Issue12 "+name)){group.Start();try{data=test();}catch(Exception e){error=e.ToString();}finally{group.RollBack();}}
        restored=before.SequenceEqual(Ids())&&modified==doc.IsModified;if(error!=null||!restored)failed++;
        results.Add(new{name,pass=error==null&&restored,error,restored,data});
    }
    long[] Ids()=>new FilteredElementCollector(doc).WherePasses(new LogicalOrFilter(new ElementIsElementTypeFilter(),new ElementIsElementTypeFilter(true))).ToElementIds().Select(x=>x.Value).OrderBy(x=>x).ToArray();
    void Tx(Action a){using(var t=new Transaction(doc,"Issue12 fixture")){t.Start();a();if(t.Commit()!=TransactionStatus.Committed)throw new Exception("Fixture transaction failed");}}
    Pipe Pipe(ElementId type,XYZ from,XYZ to){Pipe p=null;Tx(()=>{p=Autodesk.Revit.DB.Plumbing.Pipe.Create(doc,type,pipeType.Id,level.Id,from,to);p.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(4.0/12.0);});return p;}
    JObject Args(XYZ from,XYZ to)=>new JObject{{"start_x",from.X*304.8},{"start_y",from.Y*304.8},{"start_z",from.Z*304.8},{"end_x",to.X*304.8},{"end_y",to.Y*304.8},{"end_z",to.Z*304.8},{"pipe_type_id",pipeType.Id.Value},{"level_id",level.Id.Value}};
    JObject Pair(Element a,Element b)=>new JObject{{"element_id_1",a.Id.Value},{"element_id_2",b.Id.Value}};
    Connector At(MEPCurve p,XYZ point)=>p.ConnectorManager.Connectors.Cast<Connector>().OrderBy(c=>c.Origin.DistanceTo(point)).First();
    bool Joined(MEPCurve a,MEPCurve b)=>a.ConnectorManager.Connectors.Cast<Connector>().Any(c=>b.ConnectorManager.Connectors.Cast<Connector>().Any(d=>c.IsConnectedTo(d)));
    JObject Call(IRevitCommand handler,JObject args){var r=handler.Execute(app,args.ToString());if(!r.Success)throw new Exception(r.Error);return JObject.FromObject(r.Data);}
    void Check(bool pass,string message){if(!pass)throw new Exception(message);}
}
