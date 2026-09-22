using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;

public class McpDynamicScript
{
    public static object Run(UIApplication app)
    {
        var candidate = false;
        IRevitCommand handler;
        if (candidate)
        {
            var root = @"D:\Projects\bimwright\rvt-mcp\src\shared";
            var sources = new[]{"Handlers/CreatePointBasedElementHandler.cs","Infrastructure/RevitCompat.cs"}
                .Select(n=>File.ReadAllText(Path.Combine(root,n)).Replace("CreatePointBasedElementHandler","Issue13PlacementHandler").Replace("RevitCompat","Issue13Compat"));
            var refs = AppDomain.CurrentDomain.GetAssemblies().Where(a=>!a.IsDynamic&&!string.IsNullOrEmpty(a.Location))
                .GroupBy(a=>a.GetName().Name).Select(g=>g.OrderByDescending(a=>a.GetName().Version).First())
                .Select(a=>Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(a.Location));
            var options = new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(preprocessorSymbols:new[]{"REVIT2024_OR_GREATER","REVIT2027_OR_GREATER"});
            var compilation=Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("Issue13Candidate_"+Guid.NewGuid().ToString("N"),
                sources.Select(s=>Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(s,options)),refs,
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
            using(var output=new MemoryStream())
            {
                var result=compilation.Emit(output);
                if(!result.Success)return new{compileErrors=result.Diagnostics.Where(d=>d.Severity==Microsoft.CodeAnalysis.DiagnosticSeverity.Error).Select(d=>d.ToString()).ToArray()};
                handler=(IRevitCommand)Activator.CreateInstance(Assembly.Load(output.ToArray()).GetType("RvtMcp.Plugin.Handlers.Issue13PlacementHandler"));
            }
        }
        else handler=new RvtMcp.Plugin.Handlers.CreatePointBasedElementHandler();
        return new Issue13Suite(app,handler).Run(candidate);
    }
}
public class DestinationTypes:IDuplicateTypeNamesHandler
{
    public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)=>DuplicateTypeAction.UseDestinationTypes;
}
public class Issue13Suite
{
    readonly UIApplication app;readonly Document doc;readonly IRevitCommand handler;
    FamilySymbol door,window,furniture,workPlane,viewBased;Level level,upper;Wall wall,upperWall;
    readonly List<object> results=new List<object>();int failed;
    public Issue13Suite(UIApplication a,IRevitCommand h){app=a;doc=a.ActiveUIDocument.Document;handler=h;}
    public object Run(bool candidate)
    {
        var before=Ids();var modified=doc.IsModified;
        using(var group=new TransactionGroup(doc,"Issue13 regression"))
        {
            group.Start();try
            {
                var linked=new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().Select(l=>l.GetLinkDocument()).First(d=>d!=null&&d.Title.Contains("Architectural"));
                var symbols=new FilteredElementCollector(linked).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
                var originals=new[]{symbols.First(s=>s.FamilyName=="Door-Passage-Single-Flush"),symbols.First(s=>s.FamilyName=="Window-Double-Hung"),symbols.First(s=>s.Category.Id.Value==(long)BuiltInCategory.OST_Furniture&&s.Family.FamilyPlacementType==FamilyPlacementType.OneLevelBased)};
                var copies=new List<FamilySymbol>();
                level=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l=>Math.Abs(l.Elevation)).First();
                Tx(()=>{
                    var options=new CopyPasteOptions();options.SetDuplicateTypeNamesHandler(new DestinationTypes());
                    foreach(var s in originals)copies.Add(ElementTransformUtils.CopyElements(linked,new[]{s.Id},doc,Transform.Identity,options).Select(id=>doc.GetElement(id)).OfType<FamilySymbol>().First());
                    wall=Wall.Create(doc,Line.CreateBound(new XYZ(0,2000/304.8,level.Elevation),new XYZ(5000/304.8,2000/304.8,level.Elevation)),level.Id,false);
                    upper=Level.Create(doc,level.Elevation+10);
                    upperWall=Wall.Create(doc,Line.CreateBound(new XYZ(0,8000/304.8,upper.Elevation),new XYZ(5000/304.8,8000/304.8,upper.Elevation)),upper.Id,false);
                });
                door=copies[0];window=copies[1];furniture=copies[2];
                workPlane=new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().First(s=>s.Family.FamilyPlacementType==FamilyPlacementType.WorkPlaneBased);
                viewBased=new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().First(s=>s.Family.FamilyPlacementType==FamilyPlacementType.ViewBased);
                Case("door_without_host_rejected",()=>Rejected(Args(door),"requires host_id"));
                Case("door_correct_host_and_point",()=>Placed(Args(door,wall),wall));
                Case("window_correct_host_and_elevation",()=>{var a=Args(window,wall);a["z"]=level.Elevation*304.8+1200;return Placed(a,wall);});
                Case("furniture_without_host",()=>Placed(Args(furniture),null));
                Case("furniture_ignores_host_with_warning",()=>{var a=Args(furniture);a["host_id"]=999999999;var r=Placed(a,null);Check(r["warning"].Type==JTokenType.String,"missing ignored-host warning");return r;});
                Case("missing_host_element_rejected",()=>{var a=Args(door);a["host_id"]=999999999;return Rejected(a,"not found");});
                Case("wrong_host_kind_rejected",()=>{var a=Args(door);a["host_id"]=level.Id.Value;return Rejected(a,"requires host_id of a wall");});
                Case("work_plane_without_host_rejected",()=>Rejected(Args(workPlane),"WorkPlaneBased"));
                Case("work_plane_with_wall_rejected",()=>Rejected(Args(workPlane,wall),"WorkPlaneBased"));
                Case("view_based_rejected",()=>Rejected(Args(viewBased),"not supported"));
                Case("unknown_level_rejected",()=>{var a=Args(door,wall);a["level"]="Issue13-missing-level";return Rejected(a,"not found");});
                Case("missing_coordinate_rejected",()=>{var a=Args(door,wall);a.Remove("x");return Rejected(a,"finite number");});
                Case("door_upper_level_world_coordinates",()=>{var a=Args(door,upperWall);a["y"]=8000;a["z"]=upper.Elevation*304.8;a["level"]=upper.Name;return Placed(a,upperWall);});
                Case("furniture_upper_level_world_coordinates",()=>{var a=Args(furniture);a["z"]=upper.Elevation*304.8;a["level"]=upper.Name;return Placed(a,null);});
                Case("constrained_furniture_offset_rejected",()=>{var a=Args(furniture);a["z"]=upper.Elevation*304.8+500;a["level"]=upper.Name;return Rejected(a,"1 mm");});
                Case("off_wall_point_rolls_back",()=>{var a=Args(door,wall);a["y"]=2100;return Rejected(a,"1 mm");});
            }
            finally{group.RollBack();}
        }
        return new{mode=candidate?"candidate source":"installed plugin",model=doc.Title,revit=app.Application.VersionNumber,total=results.Count,failed,restored=before.SequenceEqual(Ids())&&modified==doc.IsModified,modified=doc.IsModified,results};
    }
    void Case(string name,Func<object> test)
    {
        var before=Ids();object data=null;string error=null;
        using(var group=new TransactionGroup(doc,name)){group.Start();try{data=test();}catch(Exception e){error=e.ToString();}finally{group.RollBack();}}
        bool restored=before.SequenceEqual(Ids());if(error!=null||!restored)failed++;
        results.Add(new{name,pass=error==null&&restored,restored,error,data});
    }
    JObject Args(FamilySymbol s,Element host=null){var a=new JObject{{"typeId",s.Id.Value},{"x",2500},{"y",2000},{"z",level.Elevation*304.8},{"level",level.Name}};if(host!=null)a["host_id"]=host.Id.Value;return a;}
    object Rejected(JObject args,string expected)
    {
        var before=Ids();var r=handler.Execute(app,args.ToString());Check(!r.Success&&r.Error.Contains(expected)&&before.SequenceEqual(Ids()),"Expected rejection without mutation: "+JObject.FromObject(r));return new{r.Error};
    }
    JObject Placed(JObject args,Element host)
    {
        var r=handler.Execute(app,args.ToString());Check(r.Success,r.Error);var data=JObject.FromObject(r.Data);
        var instance=(FamilyInstance)doc.GetElement(new ElementId((long)data["elementId"]));var p=((LocationPoint)instance.Location).Point;
        Check(host==null?instance.Host==null:instance.Host?.Id==host.Id,"wrong actual host");
        Check(p.DistanceTo(new XYZ((double)args["x"]/304.8,(double)args["y"]/304.8,(double)args["z"]/304.8))<=1/304.8,"wrong actual point");return data;
    }
    long[] Ids()=>new FilteredElementCollector(doc).WherePasses(new LogicalOrFilter(new ElementIsElementTypeFilter(),new ElementIsElementTypeFilter(true))).ToElementIds().Select(i=>i.Value).OrderBy(i=>i).ToArray();
    void Tx(Action action){using(var t=new Transaction(doc,"Issue13 fixture")){t.Start();action();if(t.Commit()!=TransactionStatus.Committed)throw new Exception("Fixture transaction failed");}}
    void Check(bool ok,string message){if(!ok)throw new Exception(message);}
}
