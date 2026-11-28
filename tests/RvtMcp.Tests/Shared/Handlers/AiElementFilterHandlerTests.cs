using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests;

/// <summary>
/// ai_element_filter reads only `limit` elements when there is no filter, and keeps totalFound
/// exact either way (#46). Category names resolve to top-level categories before subcategories,
/// an empty parameterValue matches empty or unset parameters, an unknown parameter name is an
/// error, and viewSpecific separates model from view-owned elements (#77).
/// </summary>
public class AiElementFilterHandlerTests
{
    private static readonly Lazy<Func<string, JObject>> Probe = new(() => HandlerProbe.Compile(Seam,
        new[]
        {
            "src/shared/Handlers/Elements/AiElementFilterHandler.cs",
            "src/shared/Infrastructure/CategoryLookup.cs",
            "src/shared/Infrastructure/RevitCompat.cs"
        }));

    private static JObject Run(object request) => Probe.Value(JObject.FromObject(request).ToString());

    private static long[] Ids(JObject result) =>
        result["data"]!["elements"]!.Select(e => e.Value<long>("elementId")).ToArray();

    [Fact]
    public void Without_a_filter_counts_natively_and_reads_only_limit_elements()
    {
        var result = Run(new { category = "Walls", limit = 5 });
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(50, result["data"]!.Value<int>("totalFound"));
        Assert.Equal(5, result["data"]!.Value<int>("returned"));
        Assert.True(result.Value<int>("reads") <= 5, "elements read: " + result.Value<int>("reads"));
    }

    [Fact]
    public void With_a_filter_counts_every_match_and_returns_the_first_limit()
    {
        var result = Run(new { category = "Walls", parameterName = "Unconnected Height", parameterValue = "2000", @operator = "greaterthan", limit = 3 });
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(30, result["data"]!.Value<int>("totalFound"));
        Assert.Equal(new long[] { 21, 22, 23 }, Ids(result));
    }

    [Fact]
    public void Lines_resolves_to_the_top_level_category_not_the_line_style_with_the_same_name()
    {
        // #77: the "Lines" line style sorts before the Lines category in BuiltInCategory.
        var result = Run(new { category = "lines" });
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(7, result["data"]!.Value<int>("totalFound"));
    }

    [Fact]
    public void Empty_parameter_value_matches_elements_whose_parameter_is_empty_or_unset()
    {
        var result = Run(new { category = "Walls", parameterName = "Mark", parameterValue = "", @operator = "equals", limit = 100 });
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(20, result["data"]!.Value<int>("totalFound"));
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i).ToArray(), Ids(result));
    }

    [Fact]
    public void Empty_parameter_value_with_an_operator_other_than_equals_is_rejected()
    {
        var result = Run(new { category = "Walls", parameterName = "Mark", parameterValue = "", @operator = "contains" });
        Assert.False(result.Value<bool>("success"));
        Assert.Contains("parameterValue is required for operator 'contains'", result.Value<string>("error"));
    }

    [Fact]
    public void Parameter_name_that_no_element_has_is_an_error_not_the_whole_category()
    {
        var result = Run(new { category = "Walls", parameterName = "Mrak", parameterValue = "W-30" });
        Assert.False(result.Value<bool>("success"));
        Assert.Contains("has an instance parameter named 'Mrak'", result.Value<string>("error"));
    }

    [Fact]
    public void Parameter_name_check_does_not_fail_a_category_without_elements()
    {
        var result = Run(new { category = "Doors", parameterName = "Mark", parameterValue = "" });
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(0, result["data"]!.Value<int>("totalFound"));
    }

    [Fact]
    public void Parameter_value_without_a_parameter_name_is_rejected()
    {
        var result = Run(new { category = "Walls", parameterValue = "W-30" });
        Assert.False(result.Value<bool>("success"));
        Assert.Contains("parameterName is required", result.Value<string>("error"));
    }

    [Fact]
    public void View_specific_false_keeps_only_model_elements()
    {
        var result = Run(new { category = "Lines", viewSpecific = false });
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(4, result["data"]!.Value<int>("totalFound"));
        Assert.All(result["data"]!["elements"]!, e =>
        {
            Assert.False(e.Value<bool>("viewSpecific"));
            Assert.Null(e["ownerViewId"]);
        });
    }

    [Fact]
    public void View_specific_true_keeps_view_owned_elements_with_their_owner_view()
    {
        var result = Run(new { category = "Lines", viewSpecific = true, limit = 2 });
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(3, result["data"]!.Value<int>("totalFound"));
        Assert.Equal(2, result["data"]!.Value<int>("returned"));
        Assert.All(result["data"]!["elements"]!, e =>
        {
            Assert.True(e.Value<bool>("viewSpecific"));
            Assert.Equal(900, e.Value<long>("ownerViewId"));
        });
    }

    [Fact]
    public void View_specific_combines_with_a_parameter_filter()
    {
        var result = Run(new { category = "Lines", parameterName = "Mark", parameterValue = "", viewSpecific = true });
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(new long[] { 105, 107 }, Ids(result));
    }

    // Doubles count the elements a collector yields, so the test sees how many were read.
    // Settings.Categories holds top-level categories only; Category.GetCategory also finds the
    // "Lines" line style, which sorts before the Lines category in the enum.
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
 public enum BuiltInCategory { INVALID=-1,OST_Walls=-2000011,OST_Doors=-2000023,OST_Lines=-2000051,OST_GenericLines=-2000078 }
 public enum StorageType { None,Integer,Double,String,ElementId }
 public class ElementId { public int IntegerValue; public ElementId(int id){IntegerValue=id;} public static ElementId InvalidElementId=new ElementId(-1); }
 public class ForgeTypeId {}
 public static class SpecTypeId { public static ForgeTypeId Length=new ForgeTypeId(),PipeSize=new ForgeTypeId(),DuctSize=new ForgeTypeId(),BarDiameter=new ForgeTypeId(),
  WireDiameter=new ForgeTypeId(),SectionDimension=new ForgeTypeId(),Area=new ForgeTypeId(),Volume=new ForgeTypeId(),Angle=new ForgeTypeId(); }
 public class Definition { public ForgeTypeId Spec; public ForgeTypeId GetDataType()=>Spec; }
 public class Parameter { public double Value; public string Text; public bool HasValue=true; public StorageType StorageType=StorageType.Double; public Definition Definition=new Definition{Spec=SpecTypeId.Length};
  public double AsDouble()=>Value; public string AsString()=>Text; public int AsInteger()=>0; public ElementId AsElementId()=>ElementId.InvalidElementId; }
 public class Category { public string Name; public BuiltInCategory Bic; public ElementId Id=>new ElementId((int)Bic);
  public static Category GetCategory(Document doc,BuiltInCategory bic)=>doc.AllCategories.FirstOrDefault(c=>c.Bic==bic); }
 public class Settings { public List<Category> Categories=new List<Category>(); }
 public class Element { public ElementId Id; public string Name=""; public Category Category; public double HeightFt; public Parameter Mark; public bool ViewSpecific; public ElementId OwnerViewId=ElementId.InvalidElementId;
  public Parameter LookupParameter(string name)=>name=="Unconnected Height"?new Parameter{Value=HeightFt}:name=="Mark"?Mark:null;
  public ElementId GetTypeId()=>ElementId.InvalidElementId; }
 public class Document { public Settings Settings=new Settings(); public List<Category> AllCategories=new List<Category>(); public List<Element> Items=new List<Element>(); public int Reads;
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
 static Parameter Text(string value,bool has=true)=>new Parameter{StorageType=StorageType.String,Text=value,HasValue=has};
 public static string Run(string requestJson){
  var doc=new Document();
  var walls=new Category{Name="Walls",Bic=BuiltInCategory.OST_Walls};
  var doors=new Category{Name="Doors",Bic=BuiltInCategory.OST_Doors};
  var lines=new Category{Name="Lines",Bic=BuiltInCategory.OST_Lines};
  var lineStyle=new Category{Name="Lines",Bic=BuiltInCategory.OST_GenericLines};
  doc.Settings.Categories.AddRange(new[]{walls,doors,lines});
  doc.AllCategories.AddRange(new[]{walls,doors,lines,lineStyle});
  // Walls 1-10: Mark "", 11-20: Mark unset, 21-50: Mark "W-<id>".
  for(int i=1;i<=50;i++) doc.Items.Add(new Element{Id=new ElementId(i),Category=walls,HeightFt=(i*100-50)/304.8,
   Mark=i<=10?Text(""):i<=20?Text(null,false):Text("W-"+i)});
  // Lines 101-104 model lines; 105-107 detail lines owned by view 900. 105 and 107 have no Mark.
  for(int i=101;i<=107;i++){ var detail=i>=105;
   doc.Items.Add(new Element{Id=new ElementId(i),Category=lines,ViewSpecific=detail,OwnerViewId=detail?new ElementId(900):ElementId.InvalidElementId,
    Mark=Text(i==105||i==107?"":"L-"+i)}); }
  var app=new UIApplication{ActiveUIDocument=new UIDocument{Document=doc}};
  var r=new RvtMcp.Plugin.Handlers.AiElementFilterHandler().Execute(app,requestJson);
  var result=JObject.FromObject(new{success=r.Success,error=r.Error,data=r.Data});
  result["reads"]=doc.Reads;
  return result.ToString();
 }
}
""";
}
