using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests;

/// <summary>
/// create_dimensions fails (success=false) when fewer than two references resolve (#43), reads a
/// wall's location line from its geometry, and infers a dimension line across parallel references
/// at one elevation.
/// </summary>
public class CreateDimensionsHandlerTests
{
    private static readonly Lazy<Func<string, JObject>> Probe = new(() => HandlerProbe.Compile(Seam,
        new[] { "src/shared/Handlers/Annotations/CreateDimensionsHandler.cs", "src/shared/Infrastructure/RevitCompat.cs" }));

    [Theory]
    [InlineData("missing_mode", "Use wall_centerline, grid_curve or explicit_reference_stable")]
    [InlineData("one_resolved", "Element not found.")]
    [InlineData("curved_wall", "supports straight walls only")]
    [InlineData("no_axis", "did not expose a Reference on this wall's location line")] // faces only: never dimension a face
    public void Fewer_than_two_references_fails_with_created_false(string scenario, string referenceError)
    {
        var result = Probe.Value(scenario);
        Assert.False(result.Value<bool>("success"));
        Assert.Contains("Fewer than two valid references", result.Value<string>("error"));
        Assert.False(result["data"]!.Value<bool>("created"));
        Assert.Contains(result["data"]!["failed_references"]!, f => f.Value<string>("error")!.Contains(referenceError));
        Assert.Equal(0, result.Value<int>("transactions"));
    }

    [Theory]
    [InlineData("walls", 6096.0)] // different lengths, elevations and thicknesses: location lines, not faces
    [InlineData("short_axis", 6096.0)] // the on-axis reference is short; a face midpoint is nearer but off the axis
    [InlineData("grids", 3000.0)]
    public void Infers_a_line_across_parallel_references_at_one_elevation(string scenario, double spacing)
    {
        var result = Probe.Value(scenario);
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        var line = result["data"]!["line"]!;
        Assert.Equal(spacing, Math.Abs(line["end"]!.Value<double>("x") - line["start"]!.Value<double>("x")), 1);
        Assert.Equal(line["start"]!.Value<double>("y"), line["end"]!.Value<double>("y"));
        Assert.Equal(line["start"]!.Value<double>("z"), line["end"]!.Value<double>("z"));
        Assert.Equal(0, result["data"]!.Value<int>("failedCount"));
    }

    // Seen live: NewDimension refused an explicit line ("The direction of dimension is invalid")
    // and the answer lost failed_references.
    [Fact]
    public void A_refused_dimension_keeps_failed_references()
    {
        var result = Probe.Value("refused_line");
        Assert.False(result.Value<bool>("success"));
        Assert.Contains("The direction of dimension is invalid", result.Value<string>("error"));
        Assert.False(result["data"]!.Value<bool>("created"));
        Assert.Equal(999, result["data"]!["failed_references"]![0]!.Value<long>("element_id"));
        Assert.Equal(1, result["data"]!.Value<int>("failedCount"));
    }

    [Fact]
    public void Bounding_box_fallback_keeps_the_line_at_one_elevation()
    {
        var result = Probe.Value("bbox");
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        var line = result["data"]!["line"]!;
        Assert.Equal(line["start"]!.Value<double>("z"), line["end"]!.Value<double>("z"));
    }

    [Fact]
    public void Bounding_box_fallback_dimensions_a_vertical_spacing_in_a_section()
    {
        var result = Probe.Value("section_vertical");
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        var line = result["data"]!["line"]!;
        Assert.Equal(3000.0, Math.Abs(line["end"]!.Value<double>("z") - line["start"]!.Value<double>("z")), 1);
        Assert.Equal(line["start"]!.Value<double>("x"), line["end"]!.Value<double>("x"));
        Assert.Equal(line["start"]!.Value<double>("y"), line["end"]!.Value<double>("y"));
    }

    // Vector math is real so the inferred line is computed, not stubbed. Encoded Revit facts seen
    // live: LocationCurve.Curve.Reference is null; a wall's geometry (computed references) holds a
    // referenced line on the location line; in a plan view NewDimension refuses a line that is not
    // level or not perpendicular to the referenced lines ("The direction of dimension is invalid").
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
 public enum ViewType { FloorPlan,Schedule,ColumnSchedule,ProjectBrowser,SystemBrowser,Legend }
 public enum TransactionStatus { Uninitialized,Started,Committed,RolledBack }
 public class ElementId { public int IntegerValue; public ElementId(int id){IntegerValue=id;} public static ElementId InvalidElementId=new ElementId(-1); }
 public class XYZ { public double X,Y,Z; public XYZ(double x,double y,double z){X=x;Y=y;Z=z;}
  public static XYZ Zero=new XYZ(0,0,0), BasisZ=new XYZ(0,0,1);
  public static XYZ operator+(XYZ a,XYZ b)=>new XYZ(a.X+b.X,a.Y+b.Y,a.Z+b.Z); public static XYZ operator-(XYZ a,XYZ b)=>new XYZ(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
  public static XYZ operator*(XYZ a,double s)=>new XYZ(a.X*s,a.Y*s,a.Z*s);
  public double DotProduct(XYZ o)=>X*o.X+Y*o.Y+Z*o.Z; public XYZ CrossProduct(XYZ o)=>new XYZ(Y*o.Z-Z*o.Y,Z*o.X-X*o.Z,X*o.Y-Y*o.X);
  public double GetLength()=>Math.Sqrt(DotProduct(this)); public XYZ Normalize()=>this*(1/GetLength()); public double DistanceTo(XYZ o)=>(this-o).GetLength(); }
 public class GeometryObject {}
 public abstract class Curve:GeometryObject { public Reference Reference; }
 public class Line:Curve { public XYZ A,B; public XYZ Direction=>(B-A).Normalize(); public XYZ GetEndPoint(int i)=>i==0?A:B;
  public static Line CreateBound(XYZ a,XYZ b){ if(a.DistanceTo(b)<1e-9)throw new ArgumentException("too short"); return new Line{A=a,B=b}; } }
 public class Arc:Curve {}
 public class GeometryElement:IEnumerable<GeometryObject> { public List<GeometryObject> Items=new List<GeometryObject>();
  public IEnumerator<GeometryObject> GetEnumerator()=>Items.GetEnumerator(); IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator(); }
 public class Options { public bool ComputeReferences,IncludeNonVisibleObjects; public View View; }
 public class Location {}
 public class LocationCurve:Location { public Curve Curve; }
 public class BoundingBoxXYZ { public XYZ Min,Max; }
 public class Element { public ElementId Id; public string Name=""; public Location Location; public BoundingBoxXYZ Box; public ElementId TypeId=new ElementId(500);
  public BoundingBoxXYZ get_BoundingBox(View v)=>Box; public void ChangeTypeId(ElementId id){TypeId=id;} public ElementId GetTypeId()=>TypeId; }
 public class ElementType:Element {}
 public class DimensionType:ElementType {}
 public class Wall:Element { public List<GeometryObject> Geometry=new List<GeometryObject>();
  public GeometryElement get_Geometry(Options o)=>o.ComputeReferences&&o.IncludeNonVisibleObjects?new GeometryElement{Items=Geometry}:new GeometryElement(); }
 public class Grid:Element { public Curve Curve; }
 public class View:Element { public ViewType ViewType=ViewType.FloorPlan; public bool IsTemplate; public XYZ ViewDirection=new XYZ(0,0,1); }
 public class Dimension:Element {}
 public class Reference { public Element Element; public Line Line; public Reference(Element e){Element=e;}
  public static Reference ParseFromStableRepresentation(Document doc,string s)=>new Reference(doc.GetElement(new ElementId(int.Parse(s)))); }
 public class ReferenceArray { public List<Reference> Items=new List<Reference>(); public void Append(Reference r){Items.Add(r);} public int Size=>Items.Count; }
 public class ItemFactory { public Document Doc;
  public Dimension NewDimension(View view,Line line,ReferenceArray refs){
   if(Math.Abs(view.ViewDirection.Z)>0.9&&Math.Abs(line.A.Z-line.B.Z)>1e-9)throw new ArgumentException("The direction of dimension is invalid.");
   foreach(var r in refs.Items) if(r.Line!=null&&Math.Abs(r.Line.Direction.DotProduct(line.Direction))>1e-6)throw new ArgumentException("The direction of dimension is invalid.");
   return Doc.Add(new Dimension()); } }
 public class Document { public Dictionary<int,Element> Items=new Dictionary<int,Element>(); public View ActiveView; public ItemFactory Create; public int Transactions,NextId=1000;
  public Document(){Create=new ItemFactory{Doc=this};}
  public T Add<T>(T e) where T:Element {if(e.Id==null)e.Id=new ElementId(NextId++);Items[e.Id.IntegerValue]=e;return e;}
  public Element GetElement(ElementId id)=>Items.TryGetValue(id.IntegerValue,out var e)?e:null; }
 public class Transaction:IDisposable {
  private Document Doc; private TransactionStatus Status;
  public Transaction(Document doc,string name){Doc=doc;}
  public TransactionStatus Start(){Doc.Transactions++;return Status=TransactionStatus.Started;}
  public bool HasStarted()=>Status==TransactionStatus.Started;
  public TransactionStatus RollBack()=>Status=TransactionStatus.RolledBack;
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
 static XYZ P(double x,double y,double z)=>new XYZ(x/304.8,y/304.8,z/304.8);
 // A straight wall along Y at x: its location curve has no Reference; its geometry holds two face
 // lines (± half the thickness) and the referenced location line, plus an end line across the wall.
 // axis: the on-axis line's length in mm from y0 (null = full length, 0 = none).
 static Wall StraightWall(Document doc,int id,double x,double y0,double y1,double z,double half,double? axis=null){
  var w=doc.Add(new Wall{Id=new ElementId(id),Location=new LocationCurve{Curve=new Line{A=P(x,y0,0),B=P(x,y1,0)}},Box=new BoundingBoxXYZ{Min=P(x-half,y0,z),Max=P(x+half,y1,z+3000)}});
  foreach(var dx in new[]{-half,0.0,half}){
   if(dx==0&&axis==0)continue;
   var l=new Line{A=P(x+dx,y0,z),B=P(x+dx,dx==0&&axis.HasValue?y0+axis.Value:y1,z)}; l.Reference=new Reference(w){Line=l}; w.Geometry.Add(l); }
  var end=new Line{A=P(x-half,y0,z),B=P(x+half,y0,z)}; end.Reference=new Reference(w){Line=end}; w.Geometry.Add(end);
  return w; }
 public static string Run(string scenario){
  var doc=new Document();
  doc.ActiveView=doc.Add(new View{Id=new ElementId(100),Name="Level 1",ViewDirection=scenario=="section_vertical"?new XYZ(0,-1,0):new XYZ(0,0,1)});
  StraightWall(doc,1,0,0,3048,0,100,scenario=="short_axis"?100:scenario=="no_axis"?0:(double?)null);
  StraightWall(doc,2,6096,600,1800,3000,150);
  doc.Add(new Wall{Id=new ElementId(3),Location=new LocationCurve{Curve=new Arc()}});
  foreach(var g in new[]{(11,0.0),(12,3000.0)}){ var c=new Line{A=P(g.Item2,-5000,0),B=P(g.Item2,5000,0)}; var grid=doc.Add(new Grid{Id=new ElementId(g.Item1),Curve=c}); }
  doc.Add(new Element{Id=new ElementId(31),Box=new BoundingBoxXYZ{Min=P(1000,1000,0),Max=P(1200,1200,200)}});
  doc.Add(new Element{Id=new ElementId(32),Box=new BoundingBoxXYZ{Min=P(1000,1000,3000),Max=P(1200,1200,3200)}});
  doc.Add(new Element{Id=new ElementId(21),Box=new BoundingBoxXYZ{Min=P(3000,1000,5000),Max=P(3200,2000,9000)}});
  JArray refs;
  switch(scenario){
   case "section_vertical": refs=new JArray(new JObject{["mode"]="explicit_reference_stable",["element_id"]=31,["stable_reference"]="31"},new JObject{["mode"]="explicit_reference_stable",["element_id"]=32,["stable_reference"]="32"}); break;
   case "no_axis": refs=new JArray(new JObject{["mode"]="wall_centerline",["element_id"]=1},new JObject{["mode"]="wall_centerline",["element_id"]=2}); break;
   case "refused_line": refs=new JArray(new JObject{["mode"]="wall_centerline",["element_id"]=1},new JObject{["mode"]="wall_centerline",["element_id"]=2},new JObject{["mode"]="wall_centerline",["element_id"]=999}); break;
   case "missing_mode": refs=new JArray(new JObject{["element_id"]=1},new JObject{["element_id"]=2}); break;
   case "one_resolved": refs=new JArray(new JObject{["mode"]="wall_centerline",["element_id"]=1},new JObject{["mode"]="wall_centerline",["element_id"]=999}); break;
   case "curved_wall": refs=new JArray(new JObject{["mode"]="wall_centerline",["element_id"]=1},new JObject{["mode"]="wall_centerline",["element_id"]=3}); break;
   case "grids": refs=new JArray(new JObject{["mode"]="grid_curve",["element_id"]=11},new JObject{["mode"]="grid_curve",["element_id"]=12}); break;
   case "bbox": refs=new JArray(new JObject{["mode"]="explicit_reference_stable",["element_id"]=1,["stable_reference"]="1"},new JObject{["mode"]="explicit_reference_stable",["element_id"]=21,["stable_reference"]="21"}); break;
   default: refs=new JArray(new JObject{["mode"]="wall_centerline",["element_id"]=1},new JObject{["mode"]="wall_centerline",["element_id"]=2}); break;
  }
  var app=new UIApplication{ActiveUIDocument=new UIDocument{Document=doc}};
  var request=new JObject{["references"]=refs,["view_id"]=100};
  // An explicit line along the walls: NewDimension refuses it.
  if(scenario=="refused_line")request["line"]=new JObject{["start"]=new JObject{["x"]=0,["y"]=0,["z"]=0},["end"]=new JObject{["x"]=0,["y"]=3000,["z"]=0}};
  var r=new RvtMcp.Plugin.Handlers.CreateDimensionsHandler().Execute(app,request.ToString());
  var result=JObject.FromObject(new{success=r.Success,error=r.Error,data=r.Data});
  result["transactions"]=doc.Transactions;
  return result.ToString();
 }
}
""";
}
