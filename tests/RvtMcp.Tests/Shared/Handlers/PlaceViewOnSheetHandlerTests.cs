using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests;

/// <summary>
/// place_view_on_sheet puts the viewport at the requested sheet point, else at the title block
/// centre, else at the old fixed point, and returns where it went (#47).
/// </summary>
public class PlaceViewOnSheetHandlerTests
{
    private static readonly Lazy<Func<string, JObject>> Probe = new(() => HandlerProbe.Compile(Seam, new[]
    {
        "src/shared/Handlers/Sheets/PlaceViewOnSheetHandler.cs",
        "src/shared/Handlers/Sheets/SheetPointInput.cs",
        "src/shared/Infrastructure/RevitCompat.cs",
    }));

    [Theory]
    [InlineData("requested_snake", "requested", 200.0, 400.0)]
    [InlineData("requested_camel", "requested", 200.0, 400.0)]
    [InlineData("title_block", "title_block_center", 422.0, 296.0)]
    [InlineData("no_title_block", "default_point", 304.8, 228.6)]
    public void Places_the_viewport_and_returns_its_centre_in_mm(string scenario, string placement, double u, double v)
    {
        var result = Probe.Value(scenario);
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(placement, result["data"]!.Value<string>("placement"));
        Assert.Equal(u, result["data"]!["center"]!.Value<double>("u"), 1);
        Assert.Equal(v, result["data"]!["center"]!.Value<double>("v"), 1);
        Assert.Equal(1, result.Value<int>("viewports"));
    }

    [Fact]
    public void One_coordinate_alone_is_refused_before_the_transaction()
    {
        var result = Probe.Value("lone_x");
        Assert.False(result.Value<bool>("success"));
        Assert.Contains("both xMm and yMm", result.Value<string>("error"));
        Assert.Equal(0, result.Value<int>("transactions"));
    }

    // Doubles model sheet creation with a title block whose bounding box is known, and
    // viewport creation at a given point. They do not model Revit's sheet geometry.
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
 public enum BuiltInCategory { INVALID=-1,OST_TitleBlocks=-2000280 }
 public enum TransactionStatus { Uninitialized,Started,Committed,RolledBack }
 public class ElementId { public int IntegerValue; public ElementId(int id){IntegerValue=id;} public static ElementId InvalidElementId=new ElementId(-1);
  public override bool Equals(object o)=>o is ElementId e&&e.IntegerValue==IntegerValue; public override int GetHashCode()=>IntegerValue;
  public static bool operator==(ElementId a,ElementId b)=>Equals(a,b); public static bool operator!=(ElementId a,ElementId b)=>!Equals(a,b); }
 public class XYZ { public double X,Y,Z; public XYZ(double x,double y,double z){X=x;Y=y;Z=z;} }
 public class BoundingBoxXYZ { public XYZ Min,Max; }
 public class Category { public ElementId Id; }
 public class Element { public ElementId Id; public virtual string Name {get;set;}="" ; public Category Category; public int Owner=-1; public BoundingBoxXYZ Box;
  public BoundingBoxXYZ get_BoundingBox(View v)=>Box; }
 public class ElementType:Element {}
 public class FamilySymbol:ElementType {}
 public class View:Element {}
 public class ViewSheet:View { public string SheetNumber;
  public static ViewSheet Create(Document doc,ElementId titleBlockType){
   var sheet=doc.Add(new ViewSheet());
   if(titleBlockType!=ElementId.InvalidElementId)
    doc.Add(new Element{Category=new Category{Id=new ElementId((int)BuiltInCategory.OST_TitleBlocks)},Owner=sheet.Id.IntegerValue,
     Box=new BoundingBoxXYZ{Min=new XYZ(2/304.8,-1/304.8,0),Max=new XYZ(842/304.8,593/304.8,0)}});
   return sheet; } }
 public class Viewport:Element { public XYZ Center;
  public static bool CanAddViewToSheet(Document doc,ElementId sheet,ElementId view)=>true;
  public static Viewport Create(Document doc,ElementId sheet,ElementId view,XYZ point)=>doc.Add(new Viewport{Center=point,Owner=sheet.IntegerValue});
  public XYZ GetBoxCenter()=>Center; }
 public class Document {
  public Dictionary<int,Element> Items=new Dictionary<int,Element>(); public int Transactions,NextId=1000;
  public T Add<T>(T e) where T:Element {if(e.Id==null)e.Id=new ElementId(NextId++);Items[e.Id.IntegerValue]=e;return e;}
  public Element GetElement(ElementId id)=>Items.TryGetValue(id.IntegerValue,out var e)?e:null;
  public void Regenerate(){}
 }
 public class FilteredElementCollector:IEnumerable<Element> {
  private Document Doc; private ElementId View; private List<Func<Element,bool>> Rules=new List<Func<Element,bool>>();
  public FilteredElementCollector(Document doc){Doc=doc;}
  public FilteredElementCollector(Document doc,ElementId view){Doc=doc;View=view;}
  public FilteredElementCollector OfCategory(BuiltInCategory bic){Rules.Add(e=>e.Category!=null&&e.Category.Id.IntegerValue==(int)bic);return this;}
  public FilteredElementCollector WhereElementIsElementType(){Rules.Add(e=>e is ElementType);return this;}
  public FilteredElementCollector WhereElementIsNotElementType(){Rules.Add(e=>!(e is ElementType));return this;}
  public Element FirstElement()=>this.FirstOrDefault();
  public IEnumerator<Element> GetEnumerator()=>Doc.Items.Values.Where(e=>View==null||e.Owner==View.IntegerValue).Where(e=>Rules.All(r=>r(e))).ToList().GetEnumerator();
  IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
 }
 public class Transaction:IDisposable {
  private Document Doc; private TransactionStatus Status; private Dictionary<int,Element> Snapshot;
  public Transaction(Document doc,string name){Doc=doc;}
  public TransactionStatus Start(){Doc.Transactions++;Snapshot=new Dictionary<int,Element>(Doc.Items);return Status=TransactionStatus.Started;}
  public bool HasStarted()=>Status==TransactionStatus.Started;
  public TransactionStatus RollBack(){Doc.Items=Snapshot;return Status=TransactionStatus.RolledBack;}
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
  doc.Add(new View{Id=new ElementId(10),Name="North"});
  if(scenario!="no_title_block")
   doc.Add(new FamilySymbol{Id=new ElementId(20),Category=new Category{Id=new ElementId((int)BuiltInCategory.OST_TitleBlocks)}});
  var request=new JObject{["viewId"]=10,["sheetNumber"]="T-1"};
  if(scenario=="requested_snake"){request["x_mm"]=200;request["y_mm"]=400;}
  if(scenario=="requested_camel"){request["xMm"]=200;request["yMm"]=400;}
  if(scenario=="lone_x")request["x_mm"]=200;
  var app=new UIApplication{ActiveUIDocument=new UIDocument{Document=doc}};
  var r=new RvtMcp.Plugin.Handlers.PlaceViewOnSheetHandler().Execute(app,request.ToString());
  var result=JObject.FromObject(new{success=r.Success,error=r.Error,data=r.Data});
  result["transactions"]=doc.Transactions; result["viewports"]=doc.Items.Values.Count(e=>e is Viewport);
  return result.ToString();
 }
}
""";
}
