using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests;

/// <summary>
/// list_areas reads each area's scheme from Area.AreaScheme because Area is a model element with
/// no owner view, falling back to OwnerViewId only when Area.AreaScheme is null (#55).
/// </summary>
public class ListAreasHandlerTests
{
    private static readonly Lazy<Func<string, JObject>> Probe = new(() => HandlerProbe.Compile(Seam,
        new[] { "src/shared/Handlers/Rooms/ListAreasHandler.cs", "src/shared/Infrastructure/RevitCompat.cs" }));

    [Fact]
    public void Unfiltered_call_reports_area_scheme_when_owner_view_is_invalid()
    {
        var result = Probe.Value("all");
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        var data = result["data"]!;
        Assert.Equal(3, data.Value<int>("total"));
        Assert.Equal(3, data.Value<int>("returned"));

        var areas = data["areas"]!.ToList();
        Assert.Equal("Gross Building", areas[0]["area_scheme"]!.Value<string>("name"));
        Assert.Equal(9490, areas[0]["area_scheme"]!.Value<long>("element_id"));
        Assert.Equal("Rentable", areas[1]["area_scheme"]!.Value<string>("name"));
        Assert.Equal(9494, areas[1]["area_scheme"]!.Value<long>("element_id"));
        Assert.Equal("Permit", areas[2]["area_scheme"]!.Value<string>("name"));
        Assert.Equal(9498, areas[2]["area_scheme"]!.Value<long>("element_id"));
    }

    [Theory]
    [InlineData("gross_exact", new long[] { 101 })]
    [InlineData("gross_case_insensitive", new long[] { 101 })]
    [InlineData("rentable", new long[] { 102 })]
    [InlineData("permit_fallback", new long[] { 103 })]
    [InlineData("missing_scheme", new long[0])]
    public void Filters_by_area_scheme_name_case_insensitively(string scenario, long[] expectedIds)
    {
        var result = Probe.Value(scenario);
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        var data = result["data"]!;
        Assert.Equal(expectedIds.Length, data.Value<int>("total"));
        Assert.Equal(expectedIds.Length, data.Value<int>("returned"));
        var ids = data["areas"]!.Select(a => a.Value<long>("element_id")).ToArray();
        Assert.Equal(expectedIds, ids);
    }

    [Fact]
    public void Combines_area_scheme_and_level_filters()
    {
        var result = Probe.Value("rentable_level_mismatch");
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(0, result["data"]!.Value<int>("total"));
        Assert.Empty(result["data"]!["areas"]!);
    }

    // Doubles model Areas as model elements (OwnerViewId == InvalidElementId) with Area.AreaScheme,
    // plus one legacy fallback case where Area.AreaScheme is null and OwnerViewId points to a ViewPlan.
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
 public enum BuiltInCategory { INVALID=-1,OST_Areas=-2003200 }
 public class ElementId { public int IntegerValue; public ElementId(int id){IntegerValue=id;} public static ElementId InvalidElementId=new ElementId(-1);
  public override bool Equals(object o)=>o is ElementId e&&e.IntegerValue==IntegerValue; public override int GetHashCode()=>IntegerValue;
  public static bool operator==(ElementId a,ElementId b)=>Equals(a,b); public static bool operator!=(ElementId a,ElementId b)=>!Equals(a,b); }
 public class XYZ { public double X,Y,Z; public XYZ(double x,double y,double z){X=x;Y=y;Z=z;} }
 public class Location {}
 public class LocationPoint:Location { public XYZ Point; }
 public class Element { public ElementId Id; public string UniqueId=""; public string Name=""; public ElementId OwnerViewId=ElementId.InvalidElementId; }
 public class Level:Element {}
 public class AreaScheme:Element {}
 public class ViewPlan:Element { public AreaScheme AreaScheme; public Level GenLevel; }
 public class SpatialElement:Element { public string Number=""; public double Area; public Location Location; public Level Level; }
 public class Area:SpatialElement { public AreaScheme AreaScheme; }
 public class Document {
  public List<Element> Items=new List<Element>();
  public Element GetElement(ElementId id)=>Items.FirstOrDefault(e=>e.Id==id);
 }
 public class FilteredElementCollector:IEnumerable<Element> {
  private Document Doc; private BuiltInCategory? Bic;
  public FilteredElementCollector(Document doc){Doc=doc;}
  public FilteredElementCollector OfCategory(BuiltInCategory bic){Bic=bic;return this;}
  public FilteredElementCollector WhereElementIsNotElementType()=>this;
  public IEnumerator<Element> GetEnumerator(){
   foreach(var e in Doc.Items){ if(Bic==BuiltInCategory.OST_Areas && e is Area) yield return e; }
  }
  IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
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
  var level1=new Level{Id=new ElementId(311),Name="Level 1"};
  var level2=new Level{Id=new ElementId(312),Name="Level 2"};
  var gross=new AreaScheme{Id=new ElementId(9490),Name="Gross Building"};
  var rentable=new AreaScheme{Id=new ElementId(9494),Name="Rentable"};
  var permit=new AreaScheme{Id=new ElementId(9498),Name="Permit"};
  var fallbackPlan=new ViewPlan{Id=new ElementId(500),Name="Permit Plan",AreaScheme=permit,GenLevel=level1};
  doc.Items.Add(fallbackPlan);
  doc.Items.Add(new Area{Id=new ElementId(101),UniqueId="u1",Name="Gross L1",Number="G-1",Area=100.0,Location=new LocationPoint{Point=new XYZ(1,2,0)},Level=level1,AreaScheme=gross,OwnerViewId=ElementId.InvalidElementId});
  doc.Items.Add(new Area{Id=new ElementId(102),UniqueId="u2",Name="Rent L2",Number="R-2",Area=50.0,Location=new LocationPoint{Point=new XYZ(3,4,10)},Level=level2,AreaScheme=rentable,OwnerViewId=ElementId.InvalidElementId});
  doc.Items.Add(new Area{Id=new ElementId(103),UniqueId="u3",Name="Fallback",Number="P-1",Area=25.0,Location=new LocationPoint{Point=new XYZ(5,6,0)},Level=null,AreaScheme=null,OwnerViewId=fallbackPlan.Id});

  JObject req=scenario switch {
   "gross_exact" => new JObject{["area_scheme_name"]="Gross Building"},
   "gross_case_insensitive" => new JObject{["area_scheme_name"]="gross building"},
   "rentable" => new JObject{["area_scheme_name"]="Rentable"},
   "permit_fallback" => new JObject{["area_scheme_name"]="Permit"},
   "missing_scheme" => new JObject{["area_scheme_name"]="DoesNotExist"},
   "rentable_level_mismatch" => new JObject{["area_scheme_name"]="Rentable",["level_name"]="Level 1"},
   _ => new JObject()
  };

  var app=new UIApplication{ActiveUIDocument=new UIDocument{Document=doc}};
  var r=new RvtMcp.Plugin.Handlers.ListAreasHandler().Execute(app,req.ToString());
  return JObject.FromObject(new{success=r.Success,error=r.Error,data=r.Data}).ToString();
 }
}
""";
}
