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

public class MepConnectionHandlerTests
{
    private static readonly Lazy<MethodInfo> Probe = new(Compile);

    [Theory]
    [InlineData("direct")]
    [InlineData("insert_pipe_fitting")]
    [InlineData("insert_duct_fitting")]
    public void AcceptsSuccessfulApiConnectionIncludingInsertedFitting(string scenario)
    {
        var result = Run(scenario);
        Assert.True(result.Value<bool>("connected"), result.ToString());
        Assert.Equal(1, result.Value<int>("commits"));
        Assert.Equal(0, result.Value<int>("rollbacks"));
    }

    [Theory]
    [InlineData("existing_direct")]
    [InlineData("existing_fitting")]
    [InlineData("existing_fitting_explicit")]
    public void RepeatedCallThroughSameFittingIsANoOp(string scenario)
    {
        var result = Run(scenario);
        Assert.True(result.Value<bool>("connected"), result.ToString());
        Assert.True(result.Value<bool>("already"));
        Assert.Equal(0, result.Value<int>("starts"));
        Assert.Equal(0, result.Value<int>("connectCalls"));
    }

    [Theory]
    [InlineData("no_connection")]
    [InlineData("unrelated_fittings")]
    [InlineData("equipment_bridge")]
    [InlineData("logical_refs")]
    [InlineData("same_fitting_port")]
    [InlineData("wrong_domain")]
    [InlineData("refs_without_physical_connection")]
    [InlineData("throw_after_insertion")]
    public void UnverifiedOrFailedConnectionRollsBack(string scenario)
    {
        var result = Run(scenario);
        Assert.False(result.Value<bool>("connected"), result.ToString());
        Assert.Equal(0, result.Value<int>("commits"));
        Assert.Equal(1, result.Value<int>("rollbacks"));
        Assert.Equal(0, result.Value<int>("remainingFittings"));
    }

    [Fact]
    public void DifferentSystemTypesStillFailBeforeMutation()
    {
        var result = Run("mismatch");
        Assert.False(result.Value<bool>("connected"));
        Assert.Equal("system_type_mismatch", result.Value<string>("reason"));
        Assert.Equal(0, result.Value<int>("starts"));
    }

    private static JObject Run(string scenario) => JObject.Parse((string)Probe.Value.Invoke(null, new object[] { scenario })!);
    private static MethodInfo Compile() => CompileAt();
    private static MethodInfo CompileAt([CallerFilePath] string file = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", ".."));
        var sources = new[] { "ConnectMepElementsHandler.cs", "MepConnectionSystemType.cs" }
            .Select(name => File.ReadAllText(Path.Combine(root, "src/shared/Handlers", name))).Append(Doubles);
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(JObject).Assembly.Location).Distinct().Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("ConnectionHandlerProbe_" + Guid.NewGuid().ToString("N"),
            sources.Select(s => CSharpSyntaxTree.ParseText(s)), refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return Assembly.Load(output.ToArray()).GetType("ConnectionHandlerProbe")!.GetMethod("Run")!;
    }

    // Run the entire production handler. ConnectTo models the documented API branch
    // that inserts a fitting, not an assumption that every connected pair is direct.
    private const string Doubles = """
using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Autodesk.Revit.DB;
namespace Autodesk.Revit.DB {
 public enum Domain {DomainPiping,DomainHvac}
 public enum ConnectorType {End,Curve,Logical}
 public enum BuiltInCategory {OST_PipeFitting=10,OST_DuctFitting=11,OST_MechanicalEquipment=12}
 public enum BuiltInParameter {RBS_PIPING_SYSTEM_TYPE_PARAM,RBS_DUCT_SYSTEM_TYPE_PARAM}
 public enum TransactionStatus {Committed,RolledBack}
 public class ElementId {public long Value;public ElementId(long v){Value=v;}public static ElementId InvalidElementId=new ElementId(-1);public static bool operator ==(ElementId a,ElementId b)=>ReferenceEquals(a,b)||(!(a is null)&&!(b is null)&&a.Value==b.Value);public static bool operator !=(ElementId a,ElementId b)=>!(a==b);public override bool Equals(object o)=>o is ElementId id&&this==id;public override int GetHashCode()=>Value.GetHashCode();}
 public class Category {public ElementId Id;}
 public class Element {public ElementId Id;public string Name="fixture";public Document Document;public Category Category;public Parameter get_Parameter(BuiltInParameter p)=>null;}
 public class MEPSystemType:Element {}
 public class Parameter {public bool HasValue;public ElementId AsElementId()=>null;}
 public class MEPSystem {public ElementId TypeId;public ElementId GetTypeId()=>TypeId;}
 public class MEPCurve:Element {public ConnectorManager ConnectorManager=new ConnectorManager();}
 public class FamilyInstance:Element {public MEPModel MEPModel=new MEPModel();}
 public class MEPModel {public ConnectorManager ConnectorManager=new ConnectorManager();}
 public class ConnectorManager {public ConnectorSet Connectors=new ConnectorSet();}
 public class ConnectorSet:List<Connector> {}
 public class XYZ {public double X,Y,Z;public double DistanceTo(XYZ other)=>0;}
 public class Document {public string Scenario;public Dictionary<long,Element> Elements=new Dictionary<long,Element>();public int Starts,Commits,Rollbacks,ConnectCalls,Fittings;public Element GetElement(ElementId id)=>Elements.TryGetValue(id.Value,out var e)?e:null;public void Regenerate(){} }
 public class Connector {
  public int Id;public Element Owner;public Domain Domain;public ConnectorType ConnectorType=ConnectorType.End;public XYZ Origin=new XYZ();public MEPSystem MEPSystem;public ConnectorSet AllRefs=new ConnectorSet();public List<Connector> Physical=new List<Connector>();
  public bool IsConnected=>Physical.Count>0;public bool IsConnectedTo(Connector other)=>Physical.Contains(other);
  public void Join(Connector other){AllRefs.Add(other);other.AllRefs.Add(this);Physical.Add(other);other.Physical.Add(this);}
  public void ConnectTo(Connector other){Owner.Document.ConnectCalls++;MakeConnection(other);}
  public void MakeConnection(Connector other){
   var d=Owner.Document;var s=d.Scenario;
   if(s=="direct"||s=="existing_direct"){Join(other);return;}
   if(s=="no_connection")return;
   var fitting=new FamilyInstance{Id=new ElementId(100),Document=d,Category=new Category{Id=new ElementId((long)(s=="equipment_bridge"?BuiltInCategory.OST_MechanicalEquipment:s=="insert_duct_fitting"?BuiltInCategory.OST_DuctFitting:BuiltInCategory.OST_PipeFitting))}};
   var port1=new Connector{Id=1,Owner=fitting,Domain=Domain};var port2=new Connector{Id=2,Owner=fitting,Domain=Domain};
   fitting.MEPModel.ConnectorManager.Connectors.AddRange(new[]{port1,port2});d.Fittings=1;
   if(s=="unrelated_fittings")port2.Owner=new FamilyInstance{Id=new ElementId(101),Document=d,Category=fitting.Category};
   if(s=="logical_refs")port1.ConnectorType=port2.ConnectorType=ConnectorType.Logical;
   if(s=="wrong_domain")port1.Domain=port2.Domain=Domain.DomainHvac;
   if(s=="same_fitting_port")port2=port1;
   if(s=="refs_without_physical_connection"){AllRefs.Add(port1);other.AllRefs.Add(port2);}else{Join(port1);other.Join(port2);}
   if(s=="throw_after_insertion")throw new Exception("API failed after adding fitting");
  }
 }
 public class Transaction:IDisposable {
  Document d;bool active;public Transaction(Document doc,string name){d=doc;}public void Start(){active=true;d.Starts++;}public bool HasStarted()=>active;public TransactionStatus Commit(){active=false;d.Commits++;return TransactionStatus.Committed;}
  public void RollBack(){active=false;d.Rollbacks++;d.Fittings=0;foreach(var c in d.Elements.Values.OfType<MEPCurve>().SelectMany(e=>e.ConnectorManager.Connectors)){c.Physical.Clear();c.AllRefs.Clear();}}
  public void Dispose(){if(active)RollBack();}
 }
}
namespace Autodesk.Revit.UI {public class UIDocument{public Document Document;}public class UIApplication{public UIDocument ActiveUIDocument;}}
namespace RvtMcp.Plugin {
 public interface IRevitCommand {}
 public class CommandResult {public bool Success;public object Data;public string Error;public static CommandResult Ok(object data)=>new CommandResult{Success=true,Data=data};public static CommandResult Fail(string error)=>new CommandResult{Error=error};}
 public static class RevitCompat {public static ElementId ToElementId(long id)=>new ElementId(id);public static long GetId(ElementId id)=>id.Value;}
}
public class ConnectionHandlerProbe {
 public static string Run(string scenario){
  var d=new Document{Scenario=scenario};var type=new MEPSystemType{Id=new ElementId(10)};d.Elements[10]=type;d.Elements[20]=new MEPSystemType{Id=new ElementId(20)};
  var a=new MEPCurve{Id=new ElementId(1),Document=d};var b=new MEPCurve{Id=new ElementId(2),Document=d};d.Elements[1]=a;d.Elements[2]=b;
  var domain=scenario=="insert_duct_fitting"?Domain.DomainHvac:Domain.DomainPiping;
  var c1=new Connector{Id=1,Owner=a,Domain=domain,MEPSystem=new MEPSystem{TypeId=type.Id}};
  var c2=new Connector{Id=1,Owner=b,Domain=domain,MEPSystem=new MEPSystem{TypeId=new ElementId(scenario=="mismatch"?20:10)}};
  a.ConnectorManager.Connectors.Add(c1);b.ConnectorManager.Connectors.Add(c2);
  if(scenario.StartsWith("existing_"))c1.MakeConnection(c2);
  var args=new JObject{{"element_id_1",1},{"element_id_2",2}};
  if(scenario=="existing_fitting_explicit"){args["connector_index_1"]=1;args["connector_index_2"]=1;}
  var app=new Autodesk.Revit.UI.UIApplication{ActiveUIDocument=new Autodesk.Revit.UI.UIDocument{Document=d}};
  var r=new RvtMcp.Plugin.Handlers.ConnectMepElementsHandler().Execute(app,args.ToString());var data=r.Data==null?new JObject():JObject.FromObject(r.Data);
  return JsonConvert.SerializeObject(new{connected=(bool?)data["connected"]??false,already=(bool?)data["already_connected"]??false,reason=(string)data["reason"],error=r.Error??(string)data["error"],starts=d.Starts,commits=d.Commits,rollbacks=d.Rollbacks,connectCalls=d.ConnectCalls,remainingFittings=d.Fittings});
 }
}
""";
}
