using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests;

/// <summary>
/// list_family_types_in_family takes the system:&lt;typeId&gt; id list_loaded_families publishes and
/// lists the id of every type in that system family, so a wall type without instances can be
/// passed to create_line_based_element (#44).
/// </summary>
public class ListFamilyTypesInFamilyHandlerTests
{
    private static readonly Lazy<Func<string, JObject>> Probe = new(() => HandlerProbe.Compile(Seam, new[]
    {
        "src/shared/Handlers/Families/ListFamilyTypesInFamilyHandler.cs",
        "src/shared/Infrastructure/Responses/ResponsePaging.cs",
        "src/shared/Infrastructure/RevitCompat.cs",
    }));

    [Fact]
    public void System_id_lists_every_type_of_that_system_family()
    {
        var result = Probe.Value("system:11");
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        var data = result["data"]!;
        Assert.Equal("system", data.Value<string>("kind"));
        Assert.Equal("Basic Wall", data.Value<string>("family_name"));
        Assert.Equal(2, data.Value<int>("total_type_count"));
        // 12 has no instance; 13 is another wall family and 20 a floor type.
        Assert.Equal(new[] { "11", "12" }, data["types"]!.Select(t => t.Value<string>("id")).ToArray());
        Assert.Equal(200.0, data["types"]![0]!["parameters"]!["Width"]!.Value<double>("value"), 3);
    }

    [Theory]
    [InlineData("system:30", "is not a system family type")] // a loadable family's FamilySymbol
    [InlineData("system:77", "is not a system family type")] // no such element
    [InlineData("system:abc", "must be system:<typeId>")]
    public void Refuses_an_id_that_is_not_a_system_type(string familyId, string error)
    {
        var result = Probe.Value(familyId);
        Assert.False(result.Value<bool>("success"));
        Assert.Contains(error, result.Value<string>("error"));
    }

    [Fact]
    public void Numeric_family_id_still_lists_the_family_symbols()
    {
        var result = Probe.Value("40");
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal("loadable", result["data"]!.Value<string>("kind"));
        Assert.Equal(new[] { "30" }, result["data"]!["types"]!.Select(t => t.Value<string>("id")).ToArray());
    }

    // Doubles model element types with a family name and category, and type parameters.
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
 public enum BuiltInParameter { INVALID=-1,WALL_ATTR_WIDTH_PARAM=1 }
 public enum StorageType { None,Integer,Double,String,ElementId }
 public class ElementId { public int IntegerValue; public ElementId(int id){IntegerValue=id;} public static ElementId InvalidElementId=new ElementId(-1);
  public override bool Equals(object o)=>o is ElementId e&&e.IntegerValue==IntegerValue; public override int GetHashCode()=>IntegerValue;
  public static bool operator==(ElementId a,ElementId b)=>Equals(a,b); public static bool operator!=(ElementId a,ElementId b)=>!Equals(a,b); }
 public class ForgeTypeId {}
 public static class SpecTypeId { public static ForgeTypeId Length=new ForgeTypeId(),Area=new ForgeTypeId(),Volume=new ForgeTypeId(),Angle=new ForgeTypeId(); }
 public class Definition { public string Name; public ForgeTypeId Spec; public ForgeTypeId GetDataType()=>Spec; }
 public class InternalDefinition:Definition { public BuiltInParameter BuiltInParameter; }
 public class Parameter { public Definition Definition; public StorageType StorageType; public double D;
  public double AsDouble()=>D; public string AsString()=>null; public int AsInteger()=>0; public ElementId AsElementId()=>null; public string AsValueString()=>null; }
 public class ParameterSet:IEnumerable { public List<Parameter> Items=new List<Parameter>(); public IEnumerator GetEnumerator()=>Items.GetEnumerator(); }
 public class Category { public ElementId Id; public string Name; }
 public class Element { public ElementId Id; public string Name=""; public Category Category; public ParameterSet Parameters=new ParameterSet(); }
 public class ElementType:Element { public string FamilyName; }
 public class FamilySymbol:ElementType { public bool IsActive; }
 public class WallType:ElementType {}
 public class FloorType:ElementType {}
 public class Family:Element { public bool IsInPlace,IsEditable=true; public Category FamilyCategory; public List<ElementId> Symbols=new List<ElementId>();
  public ICollection<ElementId> GetFamilySymbolIds()=>Symbols; }
 public class Document { public Dictionary<int,Element> Items=new Dictionary<int,Element>();
  public T Add<T>(T e) where T:Element {Items[e.Id.IntegerValue]=e;return e;}
  public Element GetElement(ElementId id)=>Items.TryGetValue(id.IntegerValue,out var e)?e:null; }
 public class FilteredElementCollector:IEnumerable<Element> {
  private Document Doc; private Type Type; public FilteredElementCollector(Document doc){Doc=doc;}
  public FilteredElementCollector OfClass(Type t){Type=t;return this;}
  public IEnumerator<Element> GetEnumerator()=>Doc.Items.Values.Where(e=>Type.IsInstanceOfType(e)).ToList().GetEnumerator();
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
 public static string Run(string familyId){
  var doc=new Document();
  var walls=new Category{Id=new ElementId(-2000011),Name="Walls"}; var floors=new Category{Id=new ElementId(-2000032),Name="Floors"}; var doors=new Category{Id=new ElementId(-2000023),Name="Doors"};
  Parameter Width(double mm)=>new Parameter{Definition=new InternalDefinition{Name="Width",Spec=SpecTypeId.Length,BuiltInParameter=BuiltInParameter.WALL_ATTR_WIDTH_PARAM},StorageType=StorageType.Double,D=mm/304.8};
  var t11=doc.Add(new WallType{Id=new ElementId(11),Name="Generic 200",FamilyName="Basic Wall",Category=walls}); t11.Parameters.Items.Add(Width(200));
  doc.Add(new WallType{Id=new ElementId(12),Name="Generic 300",FamilyName="Basic Wall",Category=walls});
  doc.Add(new WallType{Id=new ElementId(13),Name="Storefront",FamilyName="Curtain Wall",Category=walls});
  doc.Add(new FloorType{Id=new ElementId(20),Name="Generic 150",FamilyName="Floor",Category=floors});
  doc.Add(new FamilySymbol{Id=new ElementId(30),Name="900 x 2100",FamilyName="Single Door",Category=doors});
  var fam=doc.Add(new Family{Id=new ElementId(40),Name="Single Door",FamilyCategory=doors}); fam.Symbols.Add(new ElementId(30));
  var app=new UIApplication{ActiveUIDocument=new UIDocument{Document=doc}};
  var r=new RvtMcp.Plugin.Handlers.ListFamilyTypesInFamilyHandler().Execute(app,new JObject{["family_id"]=familyId}.ToString());
  return JObject.FromObject(new{success=r.Success,error=r.Error,data=r.Data}).ToString();
 }
}
""";
}
