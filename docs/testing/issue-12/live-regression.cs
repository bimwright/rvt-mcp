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
        Case("inherit_and_connect_sanitary",()=>{
            var a=Pipe(sanitary.Id,start,joint);var r=Call(create,Args(joint,end));
            Check((string)r["system_type_source"]=="connector"&& (long)r["system_type_id"]==sanitary.Id.Value,"wrong inheritance");
            var b=(Pipe)doc.GetElement(new ElementId((long)r["pipe_id"]));
            Check(Math.Abs(b.Diameter*304.8-101.6)<.01 && Joined(a,b),"diameter/connection mismatch");
            return r;
        });
        Case("inherit_from_open_fitting_port",()=>{
            var a=Pipe(sanitary.Id,start,joint);var b=Pipe(sanitary.Id,joint,joint+new XYZ(0,10,0));FamilyInstance elbow=null;
            Tx(()=>{elbow=doc.Create.NewElbowFitting(At(a,joint),At(b,joint));});
            Tx(()=>doc.Delete(b.Id));
            var port=elbow.MEPModel.ConnectorManager.Connectors.Cast<Connector>().Single(c=>c.ConnectorType==ConnectorType.End&&!c.IsConnected);
            var r=Call(create,Args(port.Origin,port.Origin+port.CoordinateSystem.BasisZ*10));
            Check((long)r["system_type_id"]==sanitary.Id.Value&&(long)r["start_element_id"]==elbow.Id.Value&&port.IsConnected,"fitting inheritance failed");return r;
        });
        Case("explicit_mismatch_refused",()=>{
            var a=Pipe(sanitary.Id,start,joint);var args=Args(joint,end);args["system_type_id"]=other.Id.Value;
            var r=Call(create,args);var b=(Pipe)doc.GetElement(new ElementId((long)r["pipe_id"]));
            Check(!Joined(a,b),"explicit creation auto-connected");var before=Ids();var c=Call(connect,Pair(a,b));
            Check(!(bool)c["connected"]&&(string)c["reason"]=="system_type_mismatch"&&!Joined(a,b)&&before.SequenceEqual(Ids()),"mismatch not rejected without mutation");return c;
        });
        Case("explicit_connector_ids_cannot_bypass_mismatch",()=>{
            var a=Pipe(sanitary.Id,start,joint);var b=Pipe(other.Id,joint,end);var args=Pair(a,b);
            args["connector_index_1"]=At(a,joint).Id;args["connector_index_2"]=At(b,joint).Id;
            var r=Call(connect,args);Check(!(bool)r["connected"]&&!Joined(a,b),"explicit mismatch bypass");return r;
        });
        Case("same_type_different_systems_allowed",()=>{
            var a=Pipe(sanitary.Id,start,joint);var b=Pipe(sanitary.Id,joint,end);
            Check(a.MEPSystem.Id!=b.MEPSystem.Id,"fixture must start as separate systems");
            var r=Call(connect,Pair(a,b));Check((bool)r["connected"]&&Joined(a,b),"same type blocked");return r;
        });
        Case("already_connected_is_noop",()=>{
            var a=Pipe(sanitary.Id,start,joint);var r=Call(create,Args(joint,end));var b=(Pipe)doc.GetElement(new ElementId((long)r["pipe_id"]));var before=Ids();
            var c=Call(connect,Pair(a,b));Check((bool)c["connected"]&&(bool)c["already_connected"]&&before.SequenceEqual(Ids()),"repeat was not no-op");return c;
        });
        Case("no_match_reports_default",()=>{
            var r=Call(create,Args(joint,end));var first=new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).FirstElementId();
            Check((string)r["system_type_source"]=="default"&&!(bool)r["connected_to_start"]&&(long)r["system_type_id"]==first.Value,"default mismatch");return r;
        });
        Case("diameter_conflict_leaves_no_pipe",()=>{
            Pipe(sanitary.Id,start,joint);var args=Args(joint,end);args["diameter"]=50.8;var before=Ids();
            var r=create.Execute(app,args.ToString());Check(!r.Success&&before.SequenceEqual(Ids()),"diameter conflict mutated model");return new{r.Error};
        });
        Case("ambiguous_start_returns_candidates",()=>{
            Pipe(sanitary.Id,start,joint);Pipe(sanitary.Id,joint+new XYZ(0,10,0),joint);var before=Ids();var r=Call(create,Args(joint,end));
            Check(!(bool)r["created"]&&r["candidates"].Count()==2&&before.SequenceEqual(Ids()),"ambiguous connector auto-chosen");return r;
        });
        Case("explicit_start_resolves_ambiguity",()=>{
            var a=Pipe(sanitary.Id,start,joint);var otherPipe=Pipe(sanitary.Id,joint+new XYZ(0,10,0),joint);var args=Args(joint,end);
            args["start_element_id"]=a.Id.Value;args["start_connector_id"]=At(a,joint).Id;var r=Call(create,args);var b=(Pipe)doc.GetElement(new ElementId((long)r["pipe_id"]));
            Check(Joined(a,b)&&!At(otherPipe,joint).IsConnected,"wrong candidate connected");return r;
        });
        Case("inside_1mm_tolerance_inherits",()=>{
            var a=Pipe(sanitary.Id,start,joint);var r=Call(create,Args(joint+new XYZ(.5/304.8,0,0),end));
            Check((string)r["system_type_source"]=="connector","inside tolerance ignored");return r;
        });
        Case("outside_1mm_tolerance_defaults",()=>{
            Pipe(sanitary.Id,start,joint);var r=Call(create,Args(joint+new XYZ(1.1/304.8,0,0),end));
            Check((string)r["system_type_source"]=="default"&&!(bool)r["connected_to_start"],"outside tolerance inherited");return r;
        });
        Case("invalid_explicit_start_no_fallback",()=>{
            var a=Pipe(sanitary.Id,start,joint);var args=Args(joint,end);args["start_element_id"]=a.Id.Value;args["start_connector_id"]=999;var before=Ids();var r=create.Execute(app,args.ToString());
            Check(!r.Success&&before.SequenceEqual(Ids()),"invalid selector fell back");return new{r.Error};
        });
        Case("api_creation_failure_rolls_back",()=>{
            Pipe(sanitary.Id,start,joint);
            var args=Args(joint,joint+new XYZ(.1/304.8,0,0));var before=Ids();var r=create.Execute(app,args.ToString());
            Check(!r.Success&&before.SequenceEqual(Ids()),"Expected creation failure with clean rollback: "+JObject.FromObject(r));return new{r.Error};
        });
        Case("hvac_supply_return_mismatch_refused",()=>{
            var source=new FilteredElementCollector(doc).OfClass(typeof(Duct)).Cast<Duct>().First();
            var types=new FilteredElementCollector(doc).OfClass(typeof(MechanicalSystemType)).Cast<MechanicalSystemType>().ToList();
            Duct a=null,b=null;Tx(()=>{a=Duct.Create(doc,types.First(t=>t.SystemClassification==MEPSystemClassification.SupplyAir).Id,source.GetTypeId(),level.Id,start,joint);b=Duct.Create(doc,types.First(t=>t.SystemClassification==MEPSystemClassification.ReturnAir).Id,source.GetTypeId(),level.Id,joint,end);});
            var r=Call(connect,Pair(a,b));Check(!(bool)r["connected"]&&(string)r["reason"]=="system_type_mismatch"&&!Joined(a,b),"HVAC mismatch not blocked");return r;
        });
        Case("explicit_domain_mismatch_refused",()=>{
            var p=Pipe(sanitary.Id,start,joint);
            var source=new FilteredElementCollector(doc).OfClass(typeof(Duct)).Cast<Duct>().First();Duct d=null;
            var st=new FilteredElementCollector(doc).OfClass(typeof(MechanicalSystemType)).FirstElementId();
            Tx(()=>d=Duct.Create(doc,st,source.GetTypeId(),level.Id,joint,end));
            var args=Pair(p,d);args["connector_index_1"]=At(p,joint).Id;args["connector_index_2"]=At(d,joint).Id;
            var before=Ids();var r=connect.Execute(app,args.ToString());Check(!r.Success&&before.SequenceEqual(Ids()),"explicit domains bypassed validation");return new{r.Error};
        });
        Case("unassigned_equipment_port_allowed",()=>{
            var original=new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .First(f=>f.Category.Id.Value==(long)BuiltInCategory.OST_MechanicalEquipment&&f.MEPModel?.ConnectorManager!=null&&f.MEPModel.ConnectorManager.Connectors.Cast<Connector>().Any(c=>c.Domain==Domain.DomainHvac&&c.ConnectorType==ConnectorType.End));
            FamilyInstance equipment=null;Tx(()=>{equipment=(FamilyInstance)doc.GetElement(ElementTransformUtils.CopyElement(doc,original.Id,new XYZ(1000,1000,0)).First());});
            var port=equipment.MEPModel.ConnectorManager.Connectors.Cast<Connector>().First(c=>c.Domain==Domain.DomainHvac&&c.ConnectorType==ConnectorType.End&&!c.IsConnected&&c.MEPSystem==null);
            var matchingType=new FilteredElementCollector(doc).OfClass(typeof(MechanicalSystemType)).Cast<MechanicalSystemType>()
                .First(t=>t.SystemClassification.ToString()==port.DuctSystemType.ToString());
            var dt=new FilteredElementCollector(doc).OfClass(typeof(DuctType)).Cast<DuctType>().First(t=>t.Shape==port.Shape);
            Duct d=null;Tx(()=>{d=Duct.Create(doc,matchingType.Id,dt.Id,level.Id,port.Origin,port.Origin+port.CoordinateSystem.BasisZ*10);if(port.Shape==ConnectorProfileType.Round)d.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM).Set(port.Radius*2);else {d.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM).Set(port.Width);d.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM).Set(port.Height);}});
            var args=Pair(equipment,d);args["connector_index_1"]=port.Id;args["connector_index_2"]=At(d,port.Origin).Id;
            var r=Call(connect,args);Check((bool)r["connected"]&&port.IsConnected,"unassigned equipment rejected");return r;
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
