using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests;

/// <summary>
/// ai_element_filter reads only `limit` elements when there is no parameter filter, and keeps
/// totalFound exact either way (#46).
/// </summary>
public class AiElementFilterHandlerTests
{
    private static readonly Lazy<Func<string, JObject>> Probe = new(() => HandlerProbe.Compile(Seam,
        new[] { "src/shared/Handlers/Elements/AiElementFilterHandler.cs", "src/shared/Infrastructure/RevitCompat.cs" }));

    [Fact]
    public void Without_a_filter_counts_natively_and_reads_only_limit_elements()
    {
        var result = Probe.Value("unfiltered");
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(50, result["data"]!.Value<int>("totalFound"));
        Assert.Equal(5, result["data"]!.Value<int>("returned"));
        Assert.True(result.Value<int>("reads") <= 5, "elements read: " + result.Value<int>("reads"));
    }

    [Fact]
    public void With_a_filter_counts_every_match_and_returns_the_first_limit()
    {
        var result = Probe.Value("filtered");
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(30, result["data"]!.Value<int>("totalFound"));
        Assert.Equal(new long[] { 21, 22, 23 }, result["data"]!["elements"]!.Select(e => e.Value<long>("elementId")).ToArray());
    }

    // Doubles count the elements a collector yields, so the test sees how many were read.
    private const string Seam = """
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace Autodesk.Revit.DB {
 public enum BuiltInCategory { INVALID=-1,OST_Walls=-2000011,OST_Doors=-2000023 }
 public enum StorageType { None,Integer,Double,String,ElementId }
 public class ElementId { public int IntegerValue; public ElementId(int id){IntegerValue=id;} public static ElementId InvalidElementId=new ElementId(-1); }
 public class ForgeTypeId {}
 public static class SpecTypeId { public static ForgeTypeId Length=new ForgeTypeId(),PipeSize=new ForgeTypeId(),DuctSize=new ForgeTypeId(),BarDiameter=new ForgeTypeId(),
  WireDiameter=new ForgeTypeId(),SectionDimension=new ForgeTypeId(),Area=new ForgeTypeId(),Volume=new ForgeTypeId(),Angle=new ForgeTypeId(); }
 public class Definition { public ForgeTypeId Spec; public ForgeTypeId GetDataType()=>Spec; }
 public class Parameter { public double Value; public StorageType StorageType=StorageType.Double; public Definition Definition=new Definition{Spec=SpecTypeId.Length};
  public double AsDouble()=>Value; public string AsString()=>null; public int AsInteger()=>0; public ElementId AsElementId()=>ElementId.InvalidElementId; }
 public class Category { public string Name; public BuiltInCategory Bic;
  public static Category GetCategory(Document doc,BuiltInCategory bic)=>doc.Categories.FirstOrDefault(c=>c.Bic==bic); }
 public class Element { public ElementId Id; public string Name=""; public Category Category; public double HeightFt;
  public Parameter LookupParameter(string name)=>name=="Unconnected Height"?new Parameter{Value=HeightFt}:null;
  public ElementId GetTypeId()=>ElementId.InvalidElementId; }
 public class Document { public List<Category> Categories=new List<Category>(); public List<Element> Items=new List<Element>(); public int Reads;
  public Element GetElement(ElementId id)=>Items.FirstOrDefault(e=>e.Id.IntegerValue==id.IntegerValue); }
 public class FilteredElementCollector:IEnumerable<Element> {
  private Document Doc; private BuiltInCategory? Bic;
  public FilteredElementCollector(Document doc){Doc=doc;}
  public FilteredElementCollector OfCategory(BuiltInCategory bic){Bic=bic;return this;}
  public FilteredElementCollector WhereElementIsNotElementType()=>this;
  public int GetElementCount()=>Doc.Items.Count(e=>e.Category.Bic==Bic);
  public IEnumerator<Element> GetEnumerator(){ foreach(var e in Doc.Items){ if(e.Category.Bic!=Bic)continue; Doc.Reads++; yield return e; } }
  IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
 }
}
namespace Autodesk.Revit.UI.Selection { public class Selection { public void SetElementIds(ICollection<ElementId> ids){} } }
namespace Autodesk.Revit.UI { public class UIDocument { public Document Document; public Autodesk.Revit.UI.Selection.Selection Selection=new Autodesk.Revit.UI.Selection.Selection(); } public class UIApplication { public UIDocument ActiveUIDocument; } }
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
  var walls=new Category{Name="Walls",Bic=BuiltInCategory.OST_Walls}; doc.Categories.Add(walls); doc.Categories.Add(new Category{Name="Doors",Bic=BuiltInCategory.OST_Doors});
  for(int i=1;i<=50;i++) doc.Items.Add(new Element{Id=new ElementId(i),Category=walls,HeightFt=(i*100-50)/304.8});
  var request=scenario=="filtered"
   ? new JObject{["category"]="Walls",["parameterName"]="Unconnected Height",["parameterValue"]="2000",["operator"]="greaterthan",["limit"]=3}
   : new JObject{["category"]="Walls",["limit"]=5};
  var app=new UIApplication{ActiveUIDocument=new UIDocument{Document=doc}};
  var r=new RvtMcp.Plugin.Handlers.AiElementFilterHandler().Execute(app,request.ToString());
  var result=JObject.FromObject(new{success=r.Success,error=r.Error,data=r.Data});
  result["reads"]=doc.Reads;
  return result.ToString();
 }
}
""";
}
