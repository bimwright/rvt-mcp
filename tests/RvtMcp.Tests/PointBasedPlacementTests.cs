using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests;

public class PointBasedPlacementTests
{
    private static readonly Lazy<MethodInfo> Probe = new(Compile);

    [Theory]
    [InlineData("missing_host")]
    [InlineData("missing_host_element")]
    [InlineData("wrong_host_kind")]
    [InlineData("metadata_error")]
    [InlineData("missing_x")]
    [InlineData("nonfinite")]
    [InlineData("unknown_level")]
    [InlineData("WorkPlaneBased")]
    [InlineData("TwoLevelsBased")]
    [InlineData("ViewBased")]
    [InlineData("CurveBased")]
    [InlineData("CurveBasedDetail")]
    [InlineData("CurveDrivenStructural")]
    [InlineData("Adaptive")]
    [InlineData("Invalid")]
    public void InvalidPlacementDoesNotStartTransaction(string scenario)
    {
        var result = Run(scenario);
        Assert.False(result.Value<bool>("success"));
        Assert.Equal(0, result.Value<int>("started"));
        Assert.Equal(0, result.Value<int>("calls"));
        Assert.False(string.IsNullOrWhiteSpace(result.Value<string>("error")));
    }

    [Theory]
    [InlineData("hosted")]
    [InlineData("unhosted")]
    [InlineData("ignored_host")]
    public void UsesCorrectOverloadAndReturnsActualPlacement(string scenario)
    {
        var result = Run(scenario);
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(scenario == "hosted", result.Value<bool>("hostOverload"));
        Assert.Equal(1, result.Value<int>("committed"));
        Assert.Equal(2500, (double)result["data"]!["location_mm"]!["x"]!, 6);
        Assert.Equal(2000, (double)result["data"]!["location_mm"]!["y"]!, 6);
        Assert.Equal(900, (double)result["data"]!["location_mm"]!["z"]!, 6);
        Assert.Equal(scenario == "hosted" ? 20L : (long?)null, (long?)result["data"]!["host_id"]);
        Assert.Equal(scenario == "ignored_host", result["data"]!["warning"]!.Type != JTokenType.Null);
    }

    [Theory]
    [InlineData("null_host")]
    [InlineData("other_host")]
    [InlineData("wrong_location")]
    [InlineData("missing_location")]
    [InlineData("null_instance")]
    [InlineData("api_throw")]
    [InlineData("commit_rejected")]
    [InlineData("unhosted_level_snaps_z")]
    public void ApiFailureOrIncorrectPlacementCannotReportSuccess(string scenario)
    {
        var result = Run(scenario);
        Assert.False(result.Value<bool>("success"));
        Assert.Equal(0, result.Value<int>("committed"));
        Assert.Equal(0, result.Value<int>("instances"));
        Assert.Equal(1, result.Value<int>("rolledBack"));
    }

    private static JObject Run(string scenario) => JObject.Parse((string)Probe.Value.Invoke(null, new object[] { scenario })!);

    private static MethodInfo Compile() => CompileAt();
    private static MethodInfo CompileAt([CallerFilePath] string file = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", ".."));
        var source = File.ReadAllText(Path.Combine(root, "src/shared/Handlers/CreatePointBasedElementHandler.cs"));
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(JObject).Assembly.Location).Distinct().Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("PlacementProbe_" + Guid.NewGuid().ToString("N"),
            new[] { source, Doubles }.Select(s => CSharpSyntaxTree.ParseText(s)), refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return Assembly.Load(output.ToArray()).GetType("PlacementProbe")!.GetMethod("Run")!;
    }

    // API doubles inject failures at the production handler's actual creation/commit seam.
    // Live Revit fixtures separately verify real overload semantics and host/coordinate behavior.
    private const string Doubles = """
using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Autodesk.Revit.DB;
namespace Autodesk.Revit.DB {
 public enum FamilyPlacementType {OneLevelBased,OneLevelBasedHosted,TwoLevelsBased,ViewBased,WorkPlaneBased,CurveBased,CurveBasedDetail,CurveDrivenStructural,Adaptive,Invalid}
 public enum BuiltInCategory {OST_Doors=1,OST_Windows=2}
 public enum TransactionStatus {Started,Committed,RolledBack}
 public class ElementId {public long Value;public ElementId(long v){Value=v;} public static bool operator ==(ElementId a,ElementId b)=>ReferenceEquals(a,b)||(!(a is null)&&!(b is null)&&a.Value==b.Value);public static bool operator !=(ElementId a,ElementId b)=>!(a==b);public override bool Equals(object o)=>o is ElementId id&&this==id;public override int GetHashCode()=>Value.GetHashCode();}
 public class Element {public ElementId Id;public string Name="test";public Category Category;}
 public class Category {public ElementId Id=new ElementId(1);public string Name="Doors";}
 public class Wall:Element {}
 public class Level:Element {public double Elevation;}
 public class Family {public FamilyPlacementType Placement;public bool Throw;public FamilyPlacementType FamilyPlacementType {get{if(Throw)throw new Exception("metadata unavailable");return Placement;}}}
 public class FamilySymbol:Element {public Family Family;public string FamilyName="fixture";public bool IsActive;public void Activate(){IsActive=true;}}
 public class XYZ {public double X,Y,Z;public XYZ(double x,double y,double z){X=x;Y=y;Z=z;}public double DistanceTo(XYZ p)=>Math.Sqrt(Math.Pow(X-p.X,2)+Math.Pow(Y-p.Y,2)+Math.Pow(Z-p.Z,2));}
 public class LocationPoint {public XYZ Point;}
 public class FamilyInstance:Element {public Element Host;public object Location;}
 public class Document {
  public string Scenario;public Dictionary<long,Element> Elements=new Dictionary<long,Element>();public int Started,Committed,RolledBack,Calls,Instances;public bool HostOverload;
  public Factory Create=>new Factory(this);public Element GetElement(ElementId id)=>Elements.TryGetValue(id.Value,out var e)?e:null;public void Regenerate(){}
 }
 public class Factory {
  Document d;public Factory(Document doc){d=doc;}
  public FamilyInstance NewFamilyInstance(XYZ p,FamilySymbol s,Level l,Autodesk.Revit.DB.Structure.StructuralType st)=>Make(p,s,null);
  public FamilyInstance NewFamilyInstance(XYZ p,FamilySymbol s,Element h,Level l,Autodesk.Revit.DB.Structure.StructuralType st){d.HostOverload=true;return Make(p,s,h);}
  FamilyInstance Make(XYZ p,FamilySymbol s,Element h){d.Calls++;d.Instances++;if(d.Scenario=="api_throw")throw new Exception("API threw after mutation");if(d.Scenario=="null_instance")return null;
   var result=new FamilyInstance{Id=new ElementId(30),Category=s.Category,Host=d.Scenario=="null_host"?null:d.Scenario=="other_host"?new Wall{Id=new ElementId(21)}:h,Location=d.Scenario=="missing_location"?null:new LocationPoint{Point=d.Scenario=="wrong_location"?new XYZ(0,6.35/304.8,0):d.Scenario=="unhosted_level_snaps_z"?new XYZ(p.X,p.Y,100):p}};d.Elements[30]=result;return result;
  }
 }
 public class FilteredElementCollector:IEnumerable<Element>{Document d;Type type;public FilteredElementCollector(Document doc){d=doc;}public FilteredElementCollector OfClass(Type t){type=t;return this;}public IEnumerator<Element> GetEnumerator()=>d.Elements.Values.Where(e=>type.IsInstanceOfType(e)).GetEnumerator();IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();}
 public class Transaction:IDisposable {Document d;bool active;public Transaction(Document doc,string name){d=doc;}public void Start(){d.Started++;active=true;}public bool HasStarted()=>active;public void RollBack(){d.RolledBack++;d.Instances=0;active=false;}public TransactionStatus Commit(){if(d.Scenario=="commit_rejected"){RollBack();return TransactionStatus.RolledBack;}d.Committed++;active=false;return TransactionStatus.Committed;}public void Dispose(){if(active)RollBack();}}
}
namespace Autodesk.Revit.DB.Structure {public enum StructuralType {NonStructural}}
namespace Autodesk.Revit.UI {public class UIDocument {public Document Document;} public class UIApplication {public UIDocument ActiveUIDocument;}}
namespace RvtMcp.Plugin {
 public interface IRevitCommand {}
 public class CommandResult {public bool Success;public object Data;public string Error;public static CommandResult Ok(object data)=>new CommandResult{Success=true,Data=data};public static CommandResult Fail(string error)=>new CommandResult{Error=error};}
 public static class RevitCompat {public static ElementId ToElementId(long id)=>new ElementId(id);public static long GetId(ElementId id)=>id.Value;}
}
public class PlacementProbe {
 public static string Run(string scenario){
  var d=new Document{Scenario=scenario};var f=new Family{Placement=FamilyPlacementType.OneLevelBasedHosted};var s=new FamilySymbol{Id=new ElementId(10),Family=f,Category=new Category()};
  d.Elements[10]=s;d.Elements[20]=new Wall{Id=new ElementId(20)};d.Elements[1]=new Level{Id=new ElementId(1),Name="L1",Elevation=100};
  var args=new JObject{{"typeId",10},{"x",2500},{"y",2000},{"z",900},{"level","L1"},{"host_id",20}};
  if(scenario=="missing_host")args.Remove("host_id");
  if(scenario=="missing_host_element")args["host_id"]=999;
  if(scenario=="wrong_host_kind")args["host_id"]=1;
  if(scenario=="metadata_error")f.Throw=true;
  if(scenario=="unknown_level")args["level"]="unknown";
  if(scenario=="missing_x")args.Remove("x");
  if(scenario=="nonfinite")args["x"]=double.NaN;
  if(scenario=="unhosted"||scenario=="ignored_host"||scenario=="unhosted_level_snaps_z"){f.Placement=FamilyPlacementType.OneLevelBased;if(scenario!="ignored_host")args.Remove("host_id");else args["host_id"]=999;}
  if(Enum.TryParse<FamilyPlacementType>(scenario,out var placement))f.Placement=placement;
  var app=new Autodesk.Revit.UI.UIApplication{ActiveUIDocument=new Autodesk.Revit.UI.UIDocument{Document=d}};
  var r=new RvtMcp.Plugin.Handlers.CreatePointBasedElementHandler().Execute(app,args.ToString());
  return JsonConvert.SerializeObject(new{success=r.Success,error=r.Error,data=r.Data,started=d.Started,committed=d.Committed,rolledBack=d.RolledBack,calls=d.Calls,instances=d.Instances,hostOverload=d.HostOverload});
 }
}
""";
}
