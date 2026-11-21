using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests;

/// <summary>
/// The tag tools share one scan of existing tags (TagScan): tag_all_walls is idempotent and
/// counts only wall tags (#40), room and area tags count as tags (#41), a sheet is refused
/// (#42), and a scan that cannot finish fails instead of reporting "no tags".
/// </summary>
public class TagHandlerTests
{
    private static readonly Lazy<Func<string, JObject>> Probe = new(() => HandlerProbe.Compile(Seam, new[]
    {
        "src/shared/Handlers/Annotations/TagScan.cs",
        "src/shared/Handlers/Annotations/TagViewPolicy.cs",
        "src/shared/Handlers/Annotations/TagAllWallsHandler.cs",
        "src/shared/Handlers/Annotations/FindUntaggedElementsHandler.cs",
        "src/shared/Handlers/Annotations/TagAllByCategoryHandler.cs",
        "src/shared/Handlers/Annotations/TagElementsHandler.cs",
        "src/shared/Handlers/Rooms/TagAllRoomsHandler.cs",
        "src/shared/Handlers/Rooms/TagAllAreasHandler.cs",
        "src/shared/Handlers/Structural/TagStructuralFramingHandler.cs",
        "src/shared/Infrastructure/RevitCompat.cs",
    }));

    private static JObject Run(string scenario) => Probe.Value(scenario);

    [Fact]
    public void Tag_all_walls_second_call_creates_no_tags()
    {
        var result = Run("walls:repeat");
        Assert.Equal(2, result["first"]!["data"]!.Value<int>("tagged"));
        var second = result["second"]!;
        Assert.True(second.Value<bool>("success"));
        Assert.Equal(0, second["data"]!.Value<int>("tagged"));
        Assert.Equal(2, second["data"]!.Value<int>("alreadyTagged"));
        Assert.Equal(2, result.Value<int>("wallTags"));
    }

    [Fact]
    public void Tag_all_walls_does_not_count_a_keynote_tag_as_a_wall_tag()
    {
        var result = Run("walls:keynote");
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(2, result["data"]!.Value<int>("tagged"));
        Assert.Equal(0, result["data"]!.Value<int>("alreadyTagged"));
    }

    // Seen live in Revit 2022: FilteredElementCollector(doc, viewId) left out 81 of 158 tags a plan
    // owned, so tag_all_walls tagged 42 walls again on its second call.
    [Fact]
    public void Tag_all_walls_sees_a_tag_a_view_scoped_collector_misses()
    {
        var result = Run("walls:missed");
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(1, result["data"]!.Value<int>("tagged"));
        Assert.Equal(1, result["data"]!.Value<int>("alreadyTagged"));
    }

    [Fact]
    public void Find_untagged_sees_a_tag_a_view_scoped_collector_misses()
    {
        var result = Run("find:missed_doors");
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(0, result["data"]!.Value<int>("untagged_count"));
    }

    [Fact]
    public void Tag_all_rooms_sees_a_room_tag_a_view_scoped_collector_misses()
    {
        var result = Run("rooms:missed");
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(0, result["data"]!.Value<int>("tagged"));
    }

    [Fact]
    public void Tag_all_areas_sees_an_area_tag_a_view_scoped_collector_misses()
    {
        var result = Run("areas:missed");
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(1, result["data"]!.Value<int>("created_count")); // area 43 only
        Assert.Equal(0, result["data"]!.Value<int>("failed_count")); // area 42 counts as tagged, not as unplaceable
    }

    [Fact]
    public void Tag_all_walls_counts_a_multi_category_tag_as_a_wall_tag()
    {
        var result = Run("walls:multi_category");
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(1, result["data"]!.Value<int>("tagged"));
        Assert.Equal(1, result["data"]!.Value<int>("alreadyTagged"));
    }

    [Fact]
    public void Tag_all_walls_skips_a_wall_without_location_curve_instead_of_failing_it()
    {
        var result = Run("walls:no_location");
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(2, result["data"]!.Value<int>("tagged"));
        Assert.Equal(new long[] { 3 }, result["data"]!["skippedNoLocationCurve"]!.Values<long>().ToArray());
        Assert.Equal(0, result["data"]!.Value<int>("failedCount"));
    }

    [Fact]
    public void Tag_all_walls_rolls_back_and_fails_when_no_wall_can_be_tagged()
    {
        var result = Run("walls:all_fail");
        Assert.False(result.Value<bool>("success"));
        Assert.Equal(2, result["data"]!.Value<int>("failedCount"));
        Assert.Equal(1, result.Value<int>("rollbacks"));
        Assert.Equal(0, result.Value<int>("wallTags"));
    }

    [Theory]
    [InlineData("walls:sheet")]
    [InlineData("find:sheet")]
    [InlineData("category:sheet")]
    [InlineData("elements:sheet")]
    [InlineData("framing:sheet")] // seen live: answered success with 0 candidates on a sheet
    public void Tag_tools_refuse_a_sheet_and_name_the_views_on_it(string scenario)
    {
        var result = Run(scenario);
        Assert.False(result.Value<bool>("success"));
        Assert.Contains("is a sheet", result.Value<string>("error"));
        // tag_all_walls has no viewId parameter: it must ask for the view to be activated.
        Assert.Contains(scenario == "walls:sheet" ? "revit_activate_view" : "pass the viewId", result.Value<string>("error"));
        Assert.Contains("Level 1 (viewId 100)", result.Value<string>("error"));
        Assert.DoesNotContain("Approach", result.Value<string>("error")); // a rendering cannot hold tags
        Assert.Equal(0, result.Value<int>("transactions"));
    }

    [Fact]
    public void A_sheet_holding_only_images_says_no_model_view_is_placed()
    {
        var result = Run("walls:image_sheet");
        Assert.False(result.Value<bool>("success"));
        Assert.Contains("No model view is placed on it.", result.Value<string>("error"));
        Assert.DoesNotContain("Approach", result.Value<string>("error"));
    }

    [Theory]
    [InlineData("walls:unreadable")]
    [InlineData("find:unreadable")]
    [InlineData("category:unreadable")]
    [InlineData("walls:interrupted")] // one host read, then the read fails: the partial set is not used
    public void A_tag_that_cannot_be_read_fails_the_call_instead_of_counting_as_no_tag(string scenario)
    {
        var result = Run(scenario);
        Assert.False(result.Value<bool>("success"));
        Assert.Contains("Failed to read existing tags in view", result.Value<string>("error"));
        Assert.Equal(0, result.Value<int>("transactions"));
    }

    [Theory]
    [InlineData("find:rooms", new long[] { 23 }, 2)] // a room tag tagging a linked room does not count
    [InlineData("find:areas", new long[] { 42 }, 1)]
    [InlineData("find:doors", new long[] { 14 }, 3)] // one door per tag-reading strategy
    public void Find_untagged_counts_room_area_and_element_tags(string scenario, long[] untagged, int tagged)
    {
        var result = Run(scenario);
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(tagged, result["data"]!.Value<int>("tagged_count"));
        Assert.Equal(untagged, result["data"]!["untagged"]!.Select(e => e.Value<long>("element_id")).ToArray());
    }

    [Fact]
    public void Tag_all_by_category_skips_tagged_rooms_and_tags_the_rest()
    {
        var result = Run("category:rooms");
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(2, result["data"]!.Value<int>("already_tagged"));
        Assert.Equal(1, result["data"]!.Value<int>("created"));
        Assert.Equal(23, result["data"]!["tags"]![0]!.Value<long>("element_id"));
    }

    [Fact]
    public void Tag_all_by_category_reports_failedCount_when_nothing_is_tagged()
    {
        var result = Run("category:doors_fail");
        Assert.False(result.Value<bool>("success"));
        Assert.Equal(1, result["data"]!.Value<int>("failedCount"));
    }

    [Fact]
    public void Tag_all_rooms_reports_failedCount_for_a_room_without_location()
    {
        var result = Run("rooms:partial");
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(1, result["data"]!.Value<int>("tagged")); // room 23: its only tag tags a linked room
        Assert.Equal(1, result["data"]!.Value<int>("failedCount"));
    }

    [Fact]
    public void Tag_all_rooms_reports_failedCount_when_nothing_is_tagged()
    {
        var result = Run("rooms:all_fail");
        Assert.False(result.Value<bool>("success"));
        Assert.Equal(1, result["data"]!.Value<int>("failedCount"));
    }

    [Fact]
    public void Tag_all_areas_reports_failedCount_next_to_failed_count()
    {
        var result = Run("areas:partial");
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(1, result["data"]!.Value<int>("created_count")); // area 43; area 42 has no location
        Assert.Equal(1, result["data"]!.Value<int>("failed_count"));
        Assert.Equal(1, result["data"]!.Value<int>("failedCount"));
    }

    [Fact]
    public void Tag_elements_reports_failedCount_for_a_partial_failure()
    {
        var result = Run("elements:partial");
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(1, result["data"]!.Value<int>("created"));
        Assert.Equal(1, result["data"]!.Value<int>("failedCount"));
    }

    [Fact]
    public void Tag_elements_fails_when_the_visible_elements_cannot_be_read()
    {
        var result = Run("elements:visible_fail");
        Assert.False(result.Value<bool>("success"));
        Assert.Contains("Failed to read the elements visible in view", result.Value<string>("error"));
        Assert.Equal(0, result.Value<int>("transactions"));
    }

    // Doubles model categories, per-view visibility, tag ownership and transaction rollback.
    // Encoded Revit facts seen live: OfClass(typeof(RoomTag)) throws ArgumentException, and a
    // tag's hosts are read through GetTaggedElementIds / TaggedLocalElementId / GetTaggedLocalElementIds.
    private const string Seam = """
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace Autodesk.Revit.DB {
 public enum BuiltInCategory { INVALID=-1,OST_Walls=-2000011,OST_Doors=-2000023,OST_Rooms=-2000160,OST_Areas=-2003200,OST_WallTags=-2000220,OST_DoorTags=-2000460,OST_RoomTags=-2000480,OST_AreaTags=-2005020,OST_KeynoteTags=-2005008,OST_MultiCategoryTags=-2008032,OST_StructuralFraming=-2001320,OST_StructuralFramingTags=-2005015 }
 public enum ViewType { FloorPlan,AreaPlan,Rendering,DrawingSheet,Schedule,ColumnSchedule,ProjectBrowser,SystemBrowser,Legend }
 public enum TagMode { TM_ADDBY_CATEGORY }
 public enum TagOrientation { Horizontal,Vertical }
 public enum SpatialElementTagOrientation { Horizontal,Vertical }
 public enum TransactionStatus { Uninitialized,Started,Committed,RolledBack }
 public class ElementId { public int IntegerValue; public ElementId(int id){IntegerValue=id;} public static ElementId InvalidElementId=new ElementId(-1);
  public override bool Equals(object o)=>o is ElementId e&&e.IntegerValue==IntegerValue; public override int GetHashCode()=>IntegerValue;
  public static bool operator==(ElementId a,ElementId b)=>Equals(a,b); public static bool operator!=(ElementId a,ElementId b)=>!Equals(a,b); }
 public class LinkElementId { public ElementId HostElementId {get;} public LinkElementId(ElementId host){HostElementId=host;} }
 public class XYZ { public double X,Y,Z; public XYZ(double x,double y,double z){X=x;Y=y;Z=z;} public static XYZ Zero=new XYZ(0,0,0);
  public static XYZ operator+(XYZ a,XYZ b)=>new XYZ(a.X+b.X,a.Y+b.Y,a.Z+b.Z); public static XYZ operator*(XYZ a,double s)=>new XYZ(a.X*s,a.Y*s,a.Z*s); }
 public class UV { public double U,V; public UV(double u,double v){U=u;V=v;} }
 public abstract class Curve { public abstract XYZ Evaluate(double p,bool normalized); }
 public class Line:Curve { public XYZ A,B; public override XYZ Evaluate(double p,bool n)=>new XYZ(A.X+(B.X-A.X)*p,A.Y+(B.Y-A.Y)*p,A.Z+(B.Z-A.Z)*p); }
 public class Location {}
 public class LocationCurve:Location { public Curve Curve; }
 public class LocationPoint:Location { public XYZ Point; }
 public class BoundingBoxXYZ { public XYZ Min,Max; }
 public class Category { public ElementId Id; public string Name;
  public static Category GetCategory(Document doc,BuiltInCategory bic)=>doc.Settings.Categories.FirstOrDefault(c=>c.Id.IntegerValue==(int)bic)??throw new ArgumentException("no category"); }
 public class Settings { public List<Category> Categories=new List<Category>(); }
 public class Element { public ElementId Id; public string Name=""; public Category Category; public Location Location; public int[] InViews; public bool MissedByViewScan; public ElementId TypeId=ElementId.InvalidElementId;
  public BoundingBoxXYZ get_BoundingBox(View v)=>null; public void ChangeTypeId(ElementId id){TypeId=id;} public ElementId GetTypeId()=>TypeId; }
 public class ElementType:Element {}
 public class FamilySymbol:ElementType { public bool IsActive=true; public void Activate(){IsActive=true;} }
 public class Wall:Element {}
 public class Area:Element {}
 public class AreaTag:Element { public Area Area {get;set;} }
 public class View:Element { public ViewType ViewType; public bool IsTemplate; public XYZ RightDirection=new XYZ(1,0,0),UpDirection=new XYZ(0,1,0); }
 public class ViewPlan:View { public Element AreaScheme; }
 public class ViewSheet:View { public List<ElementId> Viewports=new List<ElementId>(); public ICollection<ElementId> GetAllViewports()=>Viewports; }
 public class Viewport:Element { public ElementId ViewId; }
 public class Reference { public Element Element; public Reference(Element e){Element=e;} }
 public class IndependentTag:Element { public Element Host; public XYZ TagHeadPosition=XYZ.Zero;
  public static IndependentTag Create(Document doc,ElementId view,Reference r,bool leader,TagMode mode,TagOrientation o,XYZ point){
   if(doc.CreateFails)throw new InvalidOperationException("cannot tag");
   var host=r.Element; var bic=host is Wall?BuiltInCategory.OST_WallTags:BuiltInCategory.OST_DoorTags;
   return doc.Add(new TagV2022{Host=host,Category=Category.GetCategory(doc,bic),InViews=new[]{view.IntegerValue},TagHeadPosition=point}); } }
 public class TagV2022:IndependentTag { public ISet<LinkElementId> GetTaggedElementIds()=>new HashSet<LinkElementId>{new LinkElementId(Host.Id)}; }
 public class TagLegacyProperty:IndependentTag { public ElementId TaggedLocalElementId=>Host.Id; }
 public class TagLocalIds:IndependentTag { public ICollection<ElementId> GetTaggedLocalElementIds()=>new List<ElementId>{Host.Id}; }
 public class TagUnreadable:IndependentTag {}
 public class TagInterrupted:IndependentTag {
  public IEnumerable<LinkElementId> GetTaggedElementIds(){ yield return new LinkElementId(Host.Id); throw new InvalidOperationException("read stopped"); }
  public ICollection<ElementId> GetTaggedLocalElementIds()=>throw new InvalidOperationException("read stopped"); }
 public class ItemFactory { public Document Doc;
  public AreaTag NewAreaTag(ViewPlan view,Area area,UV point){ if(Doc.CreateFails)throw new InvalidOperationException("cannot tag");
   return Doc.Add(new AreaTag{Area=area,Category=Category.GetCategory(Doc,BuiltInCategory.OST_AreaTags),InViews=new[]{view.Id.IntegerValue}}); }
  public RoomTag NewRoomTag(LinkElementId room,UV point,ElementId view)=>Doc.CreateFails?throw new InvalidOperationException("cannot tag"):Doc.Add(new RoomTag{Room=(Room)Doc.GetElement(room.HostElementId),Category=Category.GetCategory(Doc,BuiltInCategory.OST_RoomTags),InViews=new[]{view.IntegerValue}}); }
 public class Document {
  public Dictionary<int,Element> Items=new Dictionary<int,Element>(); public Settings Settings=new Settings(); public View ActiveView; public ItemFactory Create;
  public int Transactions,Rollbacks,NextId=1000; public bool CreateFails,VisibleScanFails;
  public Document(){Create=new ItemFactory{Doc=this};}
  public T Add<T>(T e) where T:Element {if(e.Id==null)e.Id=new ElementId(NextId++);Items[e.Id.IntegerValue]=e;return e;}
  public Element GetElement(ElementId id)=>id!=null&&Items.TryGetValue(id.IntegerValue,out var e)?e:null;
  public void Regenerate(){}
 }
 public class FilteredElementCollector:IEnumerable<Element> {
  private Document Doc; private ElementId View; private List<Func<Element,bool>> Rules=new List<Func<Element,bool>>();
  public FilteredElementCollector(Document doc){Doc=doc;}
  public FilteredElementCollector(Document doc,ElementId view){Doc=doc;View=view;}
  public FilteredElementCollector OfClass(Type t){ if(t==typeof(RoomTag))throw new ArgumentException("Input type(RoomTag) is not supported by ElementClassFilter."); Rules.Add(e=>t.IsInstanceOfType(e)); return this; }
  public FilteredElementCollector OfCategory(BuiltInCategory bic){Rules.Add(e=>e.Category!=null&&e.Category.Id.IntegerValue==(int)bic);return this;}
  public FilteredElementCollector OfCategoryId(ElementId id){Rules.Add(e=>e.Category!=null&&e.Category.Id==id);return this;}
  public FilteredElementCollector WhereElementIsNotElementType(){Rules.Add(e=>!(e is ElementType));return this;}
  public FilteredElementCollector OwnedByView(ElementId view){Rules.Add(e=>e.InViews!=null&&e.InViews.Contains(view.IntegerValue));return this;}
  public Element FirstElement()=>this.FirstOrDefault();
  public ICollection<ElementId> ToElementIds(){ if(Doc.VisibleScanFails)throw new InvalidOperationException("view scan failed"); return this.Select(e=>e.Id).ToList(); }
  public IEnumerator<Element> GetEnumerator()=>Doc.Items.Values.Where(e=>View==null||(!e.MissedByViewScan&&(e.InViews==null||e.InViews.Contains(View.IntegerValue)))).Where(e=>Rules.All(r=>r(e))).ToList().GetEnumerator();
  IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
 }
 public class Transaction:IDisposable {
  private Document Doc; private TransactionStatus Status; private Dictionary<int,Element> Snapshot;
  public Transaction(Document doc,string name){Doc=doc;}
  public TransactionStatus Start(){Doc.Transactions++;Snapshot=new Dictionary<int,Element>(Doc.Items);return Status=TransactionStatus.Started;}
  public bool HasStarted()=>Status==TransactionStatus.Started;
  public TransactionStatus RollBack(){Doc.Rollbacks++;Doc.Items=Snapshot;return Status=TransactionStatus.RolledBack;}
  public TransactionStatus Commit()=>Status=TransactionStatus.Committed;
  public void Dispose(){if(HasStarted())RollBack();}
 }
}
namespace Autodesk.Revit.DB.Architecture {
 public class Room:Element { public double Area=10; }
 public class RoomTag:Element { public Room Room; public bool IsTaggingLink; public bool HasLeader; public SpatialElementTagOrientation TagOrientation; public XYZ TagHeadPosition=XYZ.Zero; }
}
namespace Autodesk.Revit.UI { public class UIDocument { public Document Document; public View ActiveView=>Document.ActiveView; } public class UIApplication { public UIDocument ActiveUIDocument; } }
namespace RvtMcp.Plugin {
 public interface IRevitCommand {}
 public class CommandResult { public bool Success;public string Error;public object Data;
  public static CommandResult Fail(string error)=>new CommandResult{Error=error};
  public static CommandResult Ok(object data)=>new CommandResult{Success=true,Data=data};
 }
}
public static class Probe {
 static Category Cat(Document d,BuiltInCategory b)=>Category.GetCategory(d,b);
 public static string Run(string scenario){
  var parts=scenario.Split(':'); var tool=parts[0]; var kase=parts[1];
  var doc=new Document();
  foreach(var c in new[]{("Walls",BuiltInCategory.OST_Walls),("Doors",BuiltInCategory.OST_Doors),("Rooms",BuiltInCategory.OST_Rooms),("Areas",BuiltInCategory.OST_Areas),("Wall Tags",BuiltInCategory.OST_WallTags),("Door Tags",BuiltInCategory.OST_DoorTags),("Room Tags",BuiltInCategory.OST_RoomTags),("Area Tags",BuiltInCategory.OST_AreaTags),("Keynote Tags",BuiltInCategory.OST_KeynoteTags),("Multi-Category Tags",BuiltInCategory.OST_MultiCategoryTags),("Structural Framing",BuiltInCategory.OST_StructuralFraming),("Structural Framing Tags",BuiltInCategory.OST_StructuralFramingTags)})
   doc.Settings.Categories.Add(new Category{Id=new ElementId((int)c.Item2),Name=c.Item1});
  var plan=doc.Add(new ViewPlan{Id=new ElementId(100),Name="Level 1",ViewType=ViewType.FloorPlan});
  var sheet=doc.Add(new ViewSheet{Id=new ElementId(200),Name="Sheet A",ViewType=ViewType.DrawingSheet,InViews=new int[0]});
  doc.Add(new Viewport{Id=new ElementId(201),ViewId=plan.Id,InViews=new[]{200}}); sheet.Viewports.Add(new ElementId(201));
  // A rendering (an image) on the sheet is not suggested as a place for tags.
  var image=doc.Add(new View{Id=new ElementId(400),Name="Approach",ViewType=ViewType.Rendering,InViews=new int[0]});
  doc.Add(new Viewport{Id=new ElementId(202),ViewId=image.Id,InViews=new[]{200}}); sheet.Viewports.Add(new ElementId(202));
  var imageSheet=doc.Add(new ViewSheet{Id=new ElementId(210),Name="Image Sheet",ViewType=ViewType.DrawingSheet,InViews=new int[0]});
  doc.Add(new Viewport{Id=new ElementId(211),ViewId=image.Id,InViews=new[]{210}}); imageSheet.Viewports.Add(new ElementId(211));
  doc.ActiveView=kase=="sheet"?(View)sheet:kase=="image_sheet"?imageSheet:plan;
  Line L(double x)=>new Line{A=new XYZ(x,0,0),B=new XYZ(x,10,0)};
  var w1=doc.Add(new Wall{Id=new ElementId(1),Category=Cat(doc,BuiltInCategory.OST_Walls),Location=new LocationCurve{Curve=L(0)}});
  doc.Add(new Wall{Id=new ElementId(2),Category=Cat(doc,BuiltInCategory.OST_Walls),Location=new LocationCurve{Curve=L(5)}});
  if(kase=="no_location")doc.Add(new Wall{Id=new ElementId(3),Category=Cat(doc,BuiltInCategory.OST_Walls)});
  var doors=new[]{11,12,13,14}.Select(i=>doc.Add(new Element{Id=new ElementId(i),Category=Cat(doc,BuiltInCategory.OST_Doors),Location=new LocationPoint{Point=new XYZ(i,0,0)}})).ToArray();
  var rooms=new[]{21,22,23}.Select(i=>doc.Add(new Room{Id=new ElementId(i),Category=Cat(doc,BuiltInCategory.OST_Rooms),Location=new LocationPoint{Point=new XYZ(i,5,0)}})).ToArray();
  if(tool=="rooms"&&kase=="partial")doc.Add(new Room{Id=new ElementId(24),Category=Cat(doc,BuiltInCategory.OST_Rooms)});
  var areaPlan=doc.Add(new ViewPlan{Id=new ElementId(300),Name="Area plan",ViewType=ViewType.AreaPlan});
  if(tool=="areas"){doc.ActiveView=areaPlan;doc.Add(new Area{Id=new ElementId(43),Category=Cat(doc,BuiltInCategory.OST_Areas),Location=new LocationPoint{Point=new XYZ(43,0,0)}});}
  var areas=new[]{41,42}.Select(i=>doc.Add(new Area{Id=new ElementId(i),Category=Cat(doc,BuiltInCategory.OST_Areas)})).ToArray();
  doc.Add(new FamilySymbol{Id=new ElementId(60),Category=Cat(doc,BuiltInCategory.OST_WallTags)});
  doc.Add(new FamilySymbol{Id=new ElementId(61),Category=Cat(doc,BuiltInCategory.OST_DoorTags)});
  doc.Add(new FamilySymbol{Id=new ElementId(62),Category=Cat(doc,BuiltInCategory.OST_RoomTags)});
  doc.Add(new FamilySymbol{Id=new ElementId(63),Category=Cat(doc,BuiltInCategory.OST_StructuralFramingTags)});
  int[] v={100};
  doc.Add(new TagV2022{Id=new ElementId(71),Host=doors[0],Category=Cat(doc,BuiltInCategory.OST_DoorTags),InViews=v});
  doc.Add(new TagLegacyProperty{Id=new ElementId(72),Host=doors[1],Category=Cat(doc,BuiltInCategory.OST_DoorTags),InViews=v});
  doc.Add(new TagLocalIds{Id=new ElementId(73),Host=doors[2],Category=Cat(doc,BuiltInCategory.OST_DoorTags),InViews=v});
  doc.Add(new RoomTag{Id=new ElementId(31),Room=rooms[0],Category=Cat(doc,BuiltInCategory.OST_RoomTags),InViews=v});
  doc.Add(new RoomTag{Id=new ElementId(32),Room=rooms[1],Category=Cat(doc,BuiltInCategory.OST_RoomTags),InViews=v});
  doc.Add(new RoomTag{Id=new ElementId(33),Room=rooms[2],IsTaggingLink=true,Category=Cat(doc,BuiltInCategory.OST_RoomTags),InViews=v});
  doc.Add(new AreaTag{Id=new ElementId(51),Area=areas[0],Category=Cat(doc,BuiltInCategory.OST_AreaTags),InViews=new[]{100,300}});
  if(kase.StartsWith("missed")){
   // Owned by the plan and shown in it, but left out by a view-scoped collector.
   doc.Add(new TagV2022{Id=new ElementId(83),Host=w1,Category=Cat(doc,BuiltInCategory.OST_WallTags),InViews=v,MissedByViewScan=true});
   doc.Add(new TagV2022{Id=new ElementId(84),Host=doors[3],Category=Cat(doc,BuiltInCategory.OST_DoorTags),InViews=v,MissedByViewScan=true});
   doc.Add(new RoomTag{Id=new ElementId(85),Room=rooms[2],Category=Cat(doc,BuiltInCategory.OST_RoomTags),InViews=v,MissedByViewScan=true});
   doc.Add(new AreaTag{Id=new ElementId(86),Area=areas[1],Category=Cat(doc,BuiltInCategory.OST_AreaTags),InViews=new[]{100,300},MissedByViewScan=true});
  }
  if(kase=="multi_category")doc.Add(new TagV2022{Id=new ElementId(82),Host=w1,Category=Cat(doc,BuiltInCategory.OST_MultiCategoryTags),InViews=v});
  if(kase=="keynote")doc.Add(new TagV2022{Id=new ElementId(81),Host=w1,Category=Cat(doc,BuiltInCategory.OST_KeynoteTags),InViews=v});
  if(kase=="interrupted")doc.Add(new TagInterrupted{Id=new ElementId(92),Host=w1,Category=Cat(doc,BuiltInCategory.OST_WallTags),InViews=v});
  if(kase=="unreadable")doc.Add(new TagUnreadable{Id=new ElementId(91),Host=w1,Category=Cat(doc,BuiltInCategory.OST_WallTags),InViews=v});
  if(kase=="all_fail"||kase=="doors_fail")doc.CreateFails=true;
  if(kase=="visible_fail")doc.VisibleScanFails=true;
  var app=new UIApplication{ActiveUIDocument=new UIDocument{Document=doc}};
  var viewId=kase=="sheet"?200:100;
  Func<JObject> call=()=>{
   RvtMcp.Plugin.CommandResult r;
   switch(tool){
    case "walls": r=new RvtMcp.Plugin.Handlers.TagAllWallsHandler().Execute(app,"{}");break;
    case "find": r=new RvtMcp.Plugin.Handlers.FindUntaggedElementsHandler().Execute(app,new JObject{["category"]=kase=="areas"?"Areas":kase.EndsWith("doors")?"Doors":"Rooms",["view_id"]=viewId}.ToString());break;
    case "framing": r=new RvtMcp.Plugin.Handlers.TagStructuralFramingHandler().Execute(app,new JObject{["view_id"]=viewId}.ToString());break;
    case "rooms": r=new RvtMcp.Plugin.Handlers.TagAllRoomsHandler().Execute(app,"{}");break;
    case "areas": r=new RvtMcp.Plugin.Handlers.TagAllAreasHandler().Execute(app,"{}");break;
    case "category": r=new RvtMcp.Plugin.Handlers.TagAllByCategoryHandler().Execute(app,new JObject{["category"]=kase=="doors_fail"?"Doors":"Rooms",["view_id"]=viewId}.ToString());break;
    default: r=new RvtMcp.Plugin.Handlers.TagElementsHandler().Execute(app,new JObject{["element_ids"]=new JArray(14,999),["view_id"]=viewId}.ToString());break;
   }
   return JObject.FromObject(new{success=r.Success,error=r.Error,data=r.Data});
  };
  var result=call();
  if(kase=="repeat")result=new JObject{["first"]=result,["second"]=call()};
  result["transactions"]=doc.Transactions; result["rollbacks"]=doc.Rollbacks;
  result["wallTags"]=doc.Items.Values.Count(e=>e is TagV2022&&e.Category.Id.IntegerValue==(int)BuiltInCategory.OST_WallTags);
  return result.ToString();
 }
}
""";
}
