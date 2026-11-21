using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests;

/// <summary>
/// create_line_based_element creates walls only, and a typeId that is not a wall type is
/// refused before anything is created instead of falling back to the default type (#44).
/// </summary>
public class CreateLineBasedElementHandlerTests
{
    private static readonly Lazy<Func<string, JObject>> Probe = new(() => HandlerProbe.Compile(Seam,
        new[] { "src/shared/Handlers/Elements/CreateLineBasedElementHandler.cs", "src/shared/Infrastructure/RevitCompat.cs" }));

    [Theory]
    [InlineData("floor_type", "is a FloorType ('Generic floor'), not a wall type")]
    [InlineData("missing_type", "does not exist in the document")]
    [InlineData("pipe", "not supported. Supported: wall")]
    public void Refuses_before_creating_anything(string scenario, string error)
    {
        var result = Probe.Value(scenario);
        Assert.False(result.Value<bool>("success"));
        Assert.Contains(error, result.Value<string>("error"));
        Assert.Equal(0, result.Value<int>("transactions"));
        Assert.Equal(0, result.Value<int>("walls"));
    }

    [Fact]
    public void Applies_the_requested_wall_type()
    {
        var result = Probe.Value("wall_type");
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(1, result.Value<int>("walls"));
        Assert.Equal("Brick wall", result.Value<string>("wallType"));
    }

    [Fact]
    public void Without_typeId_keeps_the_default_wall_type()
    {
        var result = Probe.Value("no_type");
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal("Default wall", result.Value<string>("wallType"));
    }

    // Doubles model element lookup, wall creation and the transaction count only.
    private const string Seam = """
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace Autodesk.Revit.DB {
 public enum BuiltInParameter { WALL_USER_HEIGHT_PARAM }
 public class ElementId { public int IntegerValue; public ElementId(int id){IntegerValue=id;} public static ElementId InvalidElementId=new ElementId(-1); }
 public class Category { public string Name; }
 public class Element { public ElementId Id; public virtual string Name {get;set;} public Category Category; }
 public class Level:Element {}
 public class ElementType:Element {}
 public class WallType:ElementType {}
 public class FloorType:ElementType {}
 public class Parameter { public bool Set(double value)=>true; }
 public class Wall:Element {
  public WallType WallType;
  public override string Name { get => WallType.Name; set {} }
  public Parameter get_Parameter(BuiltInParameter p)=>new Parameter();
  public static Wall Create(Document doc,Curve line,ElementId level,bool structural){
   var w=new Wall{Id=new ElementId(900+doc.Walls.Count),WallType=doc.DefaultWallType,Category=new Category{Name="Walls"}};doc.Walls.Add(w);return w;}
 }
 public class XYZ { public double X,Y,Z; public XYZ(double x,double y,double z){X=x;Y=y;Z=z;} }
 public class Curve {}
 public class Line:Curve { public static Line CreateBound(XYZ a,XYZ b)=>new Line(); }
 public class Document {
  public Dictionary<int,Element> Items=new Dictionary<int,Element>(); public List<Wall> Walls=new List<Wall>(); public int Transactions; public WallType DefaultWallType;
  public Element GetElement(ElementId id)=>Items.TryGetValue(id.IntegerValue,out var e)?e:null;
 }
 public class FilteredElementCollector:IEnumerable<Element> {
  private Document Doc;private Type Type;public FilteredElementCollector(Document doc){Doc=doc;}
  public FilteredElementCollector OfClass(Type type){Type=type;return this;}
  public IEnumerator<Element> GetEnumerator()=>Doc.Items.Values.Where(x=>Type.IsInstanceOfType(x)).GetEnumerator();
  IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
 }
 public enum TransactionStatus { Uninitialized,Started,Committed,RolledBack }
 public class Transaction:IDisposable {
  private Document Doc;private TransactionStatus Status;
  public Transaction(Document doc,string name){Doc=doc;}
  public TransactionStatus Start(){Doc.Transactions++;return Status=TransactionStatus.Started;}
  public bool HasStarted()=>Status==TransactionStatus.Started;
  public TransactionStatus RollBack(){Doc.Walls.Clear();return Status=TransactionStatus.RolledBack;}
  public TransactionStatus Commit()=>Status=TransactionStatus.Committed;
  public void Dispose(){if(HasStarted())RollBack();}
 }
}
namespace Autodesk.Revit.UI { public class UIDocument { public Document Document; } public class UIApplication { public UIDocument ActiveUIDocument; } }
namespace RvtMcp.Plugin {
 public interface IRevitCommand {}
 public class CommandResult { public bool Success;public string Error;public object Data;
  public static CommandResult Fail(string error)=>new CommandResult{Error=error};
  public static CommandResult Ok(object data)=>new CommandResult{Success=true,Data=data};
 }
}
public static class Probe {
 public static string Run(string scenario){
  var doc=new Document();
  doc.DefaultWallType=new WallType{Id=new ElementId(10),Name="Default wall"};
  doc.Items[1]=new Level{Id=new ElementId(1),Name="Level 1"};
  doc.Items[10]=doc.DefaultWallType;
  doc.Items[11]=new WallType{Id=new ElementId(11),Name="Brick wall"};
  doc.Items[20]=new FloorType{Id=new ElementId(20),Name="Generic floor"};
  var request=new JObject{["elementType"]=scenario=="pipe"?"pipe":"wall",["startX"]=0,["startY"]=0,["endX"]=5000,["endY"]=0,["level"]="Level 1"};
  if(scenario=="floor_type")request["typeId"]=20;
  if(scenario=="missing_type")request["typeId"]=77;
  if(scenario=="wall_type"||scenario=="pipe")request["typeId"]=11;
  var app=new UIApplication{ActiveUIDocument=new UIDocument{Document=doc}};
  var result=new RvtMcp.Plugin.Handlers.CreateLineBasedElementHandler().Execute(app,request.ToString());
  return JsonConvert.SerializeObject(new{success=result.Success,error=result.Error,transactions=doc.Transactions,walls=doc.Walls.Count,wallType=doc.Walls.FirstOrDefault()?.WallType?.Name});
 }
}
""";
}
