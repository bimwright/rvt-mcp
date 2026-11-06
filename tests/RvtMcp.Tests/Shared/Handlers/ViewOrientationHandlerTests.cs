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

public class ViewOrientationHandlerTests
{
    private const double OldEyeMm = 304.8;

    [Fact]
    public void Named_view_saves_orientation_and_is_tracked_for_undo()
    {
        var r = Run("orient", "named");
        Assert.True(r.Value<bool>("success"), r.Value<string>("error"));
        Assert.True(r["data"]!.Value<bool>("orientation_saved"));
        Assert.Equal(-50000, r.Value<double>("saved_eye_x_mm"));
        Assert.Equal(-50000, r.Value<double>("current_eye_x_mm"));
        Assert.True(r.Value<bool>("tracked"));
        Assert.Equal(1, r.Value<int>("transactions"));
    }

    [Fact]
    public void Default_view_moves_only_the_temporary_camera()
    {
        var r = Run("orient", "default");
        Assert.True(r.Value<bool>("success"), r.Value<string>("error"));
        Assert.False(r["data"]!.Value<bool>("orientation_saved"));
        Assert.Equal(OldEyeMm, r.Value<double>("saved_eye_x_mm"));
        Assert.Equal(-50000, r.Value<double>("current_eye_x_mm"));
        Assert.False(r.Value<bool>("tracked"));
        Assert.Contains(r["data"]!["notes"]!, note => note.Value<string>()!.StartsWith("Default 3D view", StringComparison.Ordinal));
        // The crop change is an ordinary model edit and still commits.
        Assert.False(r.Value<bool>("crop"));
    }

    [Theory]
    [InlineData("named_commit_failure")]
    [InlineData("named_exception")]
    [InlineData("default_commit_failure")]
    public void Failed_orientation_leaves_saved_and_temporary_camera_unchanged(string scenario)
    {
        var r = Run("orient", scenario);
        Assert.False(r.Value<bool>("success"));
        Assert.Equal(OldEyeMm, r.Value<double>("saved_eye_x_mm"));
        Assert.Equal(OldEyeMm, r.Value<double>("current_eye_x_mm"));
        Assert.True(r.Value<bool>("crop"));
        Assert.False(r.Value<bool>("tracked"));
    }

    [Fact]
    public void Locked_view_is_refused_before_a_transaction()
    {
        var r = Run("orient", "locked");
        Assert.False(r.Value<bool>("success"));
        Assert.Equal(0, r.Value<int>("transactions"));
    }

    [Fact]
    public void New_camera_turns_off_far_clip_active_and_leaves_section_far_clipping()
    {
        var r = Run("camera", "new");
        Assert.True(r.Value<bool>("success"), r.Value<string>("error"));
        Assert.True(r["data"]!.Value<bool>("created"));
        Assert.False(r["data"]!.Value<bool>("far_clip_active"));
        Assert.Equal(0, r.Value<int>("active_far"));
        Assert.Equal(1, r.Value<int>("section_far"));
        Assert.True(r.Value<bool>("tracked"));
    }

    [Fact]
    public void Far_clip_true_keeps_far_clip_active()
    {
        var r = Run("camera", "far_clip_kept");
        Assert.True(r.Value<bool>("success"), r.Value<string>("error"));
        Assert.True(r["data"]!.Value<bool>("far_clip_active"));
        Assert.Equal(1, r.Value<int>("active_far"));
    }

    [Fact]
    public void Missing_far_clip_parameter_is_reported_as_unknown()
    {
        var r = Run("camera", "missing_far_param");
        Assert.True(r.Value<bool>("success"), r.Value<string>("error"));
        Assert.Equal(JTokenType.Null, r["data"]!["far_clip_active"]!.Type);
    }

    [Fact]
    public void A_plan_with_the_same_name_does_not_block_a_camera()
    {
        var r = Run("camera", "plan_same_name");
        Assert.True(r.Value<bool>("success"), r.Value<string>("error"));
        Assert.True(r["data"]!.Value<bool>("created"));
    }

    [Fact]
    public void An_isometric_3d_view_with_the_same_name_is_refused()
    {
        var r = Run("camera", "isometric_same_name");
        Assert.False(r.Value<bool>("success"));
        Assert.Equal(0, r.Value<int>("transactions"));
    }

    [Theory]
    [InlineData("reaim_commit_failure")]
    [InlineData("reaim_exception")]
    public void Failed_re_aim_restores_the_existing_camera(string scenario)
    {
        var r = Run("camera", scenario);
        Assert.False(r.Value<bool>("success"));
        Assert.Equal(OldEyeMm, r.Value<double>("saved_eye_x_mm"));
        Assert.Equal(OldEyeMm, r.Value<double>("current_eye_x_mm"));
    }

    [Fact]
    public void Failed_new_camera_is_rolled_back_without_touching_the_discarded_view()
    {
        var r = Run("camera", "new_commit_failure");
        Assert.False(r.Value<bool>("success"));
        Assert.Equal(0, r.Value<int>("cameras"));
    }

    private static JObject Run(string tool, string scenario, [CallerFilePath] string file = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "..", "..", ".."));
        var sources = new[]
        {
            "src/shared/Handlers/Views/SetViewOrientationHandler.cs", "src/shared/Handlers/Views/CreateCameraViewHandler.cs",
            "src/shared/Handlers/Views/ViewPresentationSupport.cs", "src/shared/Handlers/Views/ViewOrientationMath.cs",
            "src/shared/Handlers/Views/SavedViewOrientationRefresh.cs", "src/shared/Infrastructure/RevitCompat.cs"
        }.Select(path => File.ReadAllText(Path.Combine(root, path))).Append(ApiSeam);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(JObject).Assembly.Location).Distinct().Select(path => MetadataReference.CreateFromFile(path));
        // Compiled per run: SavedViewOrientationRefresh keeps static tracking state.
        var compilation = CSharpCompilation.Create("ViewOrientation_" + Guid.NewGuid().ToString("N"),
            sources.Select(source => CSharpSyntaxTree.ParseText(source)), references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var emitted = compilation.Emit(output);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        var probe = Assembly.Load(output.ToArray()).GetType("Probe")!.GetMethod("Run")!;
        return JObject.Parse((string)probe.Invoke(null, new object[] { tool, scenario })!);
    }

    // Doubles keep document state (saved orientation, crop, far clip, created views), which a
    // transaction rolls back, apart from the temporary camera, which a rollback leaves as set.
    // They exercise production control flow, not native Revit camera or Undo behaviour.
    private const string ApiSeam = """
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace Autodesk.Revit.Exceptions {
 public class ApplicationException:Exception{public ApplicationException(string m=null):base(m){}}
 public class InvalidOperationException:ApplicationException{public InvalidOperationException(string m=null):base(m){}}
}
namespace Autodesk.Revit.DB {
 public enum BuiltInParameter{VIEWER_BOUND_ACTIVE_FAR,VIEWER_BOUND_FAR_CLIPPING}
 public enum ViewFamily{ThreeDimensional,FloorPlan}
 public enum TransactionStatus{Uninitialized,Started,Committed,RolledBack}
 public enum UndoOperation{TransactionCommitted,TransactionUndone,TransactionRedone,TransactionGroupRolledBack}
 public class ElementId{public int IntegerValue;public ElementId(int id){IntegerValue=id;}
  public static ElementId InvalidElementId=new ElementId(-1);
  public override bool Equals(object o)=>o is ElementId e&&e.IntegerValue==IntegerValue;public override int GetHashCode()=>IntegerValue;
  public static bool operator ==(ElementId a,ElementId b)=>a is null?b is null:a.Equals(b);public static bool operator !=(ElementId a,ElementId b)=>!(a==b);}
 public class XYZ{public double X,Y,Z;public XYZ(double x,double y,double z){X=x;Y=y;Z=z;}
  public static XYZ operator -(XYZ a,XYZ b)=>new XYZ(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
  public XYZ Normalize(){var l=Math.Sqrt(X*X+Y*Y+Z*Z);return new XYZ(X/l,Y/l,Z/l);}
  public bool IsAlmostEqualTo(XYZ o)=>Math.Abs(X-o.X)<1e-9&&Math.Abs(Y-o.Y)<1e-9&&Math.Abs(Z-o.Z)<1e-9;}
 public class ViewOrientation3D{public XYZ EyePosition,UpDirection,ForwardDirection;
  public ViewOrientation3D(XYZ eye,XYZ up,XYZ forward){EyePosition=eye;UpDirection=up;ForwardDirection=forward;}}
 public class Element{public ElementId Id;public string Name;public Document Document;public bool IsValidObject=true;}
 public class View:Element{public bool IsTemplate;}
 public class ViewPlan:View{}
 public class ViewFamilyType:Element{public ViewFamily ViewFamily;}
 public class Parameter{public Func<int> Get;public Action<int> Write;public bool IsReadOnly;
  public int AsInteger()=>Get();public bool Set(int v){Write(v);return true;}}
 public class View3D:View{
  public static readonly ViewOrientation3D Old=new ViewOrientation3D(new XYZ(1,0,0),new XYZ(0,0,1),new XYZ(-1,0,0));
  public static bool CreateWithoutActiveFar;
  public bool IsPerspective,Crop,CanSave=true,IsLocked,HasActiveFar=true,HasSectionFar=true;public int ActiveFar=1,SectionFar=1;
  public ViewOrientation3D Saved=Old,Current=Old;public string FailOn;
  public bool CropBoxActive{get=>Crop;set{Document.Require();if(FailOn=="crop")throw new Autodesk.Revit.Exceptions.ApplicationException("crop failure");Crop=value;}}
  public bool CanSaveOrientation()=>CanSave;
  public bool CanToggleBetweenPerspectiveAndIsometric()=>true;
  public void ToggleToPerspective(){Document.Require();IsPerspective=true;}
  public void ToggleToIsometric(){Document.Require();IsPerspective=false;}
  public ViewOrientation3D GetOrientation()=>Current;
  public ViewOrientation3D GetSavedOrientation()=>Saved;
  public void SetOrientation(ViewOrientation3D o){if(IsLocked)throw new Autodesk.Revit.Exceptions.InvalidOperationException("locked");if(!IsValidObject)throw new Exception("invalid view");Current=o;}
  public void SaveOrientation(){Document.Require();if(!CanSave)throw new Autodesk.Revit.Exceptions.InvalidOperationException("cannot save");Saved=Current;}
  public Parameter get_Parameter(BuiltInParameter p){
   if(p==BuiltInParameter.VIEWER_BOUND_ACTIVE_FAR&&HasActiveFar)return new Parameter{Get=()=>ActiveFar,Write=v=>{Document.Require();if(FailOn=="far")throw new Autodesk.Revit.Exceptions.ApplicationException("far failure");ActiveFar=v;}};
   if(p==BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING&&HasSectionFar)return new Parameter{Get=()=>SectionFar,Write=v=>{Document.Require();SectionFar=v;}};
   return null;}
  public static View3D CreatePerspective(Document doc,ElementId typeId){doc.Require();
   var v=new View3D{Id=new ElementId(doc.NextId++),IsPerspective=true,HasActiveFar=!CreateWithoutActiveFar};doc.Add(v);return v;}
 }
 public class Document{
  public List<Element> Items=new List<Element>();public int Transactions,NextId=100;public bool InTx,FailCommit,IsValidObject=true;
  public void Require(){if(!InTx)throw new System.InvalidOperationException("Modification outside a transaction");}
  public Element GetElement(ElementId id)=>Items.FirstOrDefault(e=>e.Id.Equals(id));
  public void Add(Element e){e.Document=this;Items.Add(e);}
  public override int GetHashCode()=>1;
 }
 public class FilteredElementCollector:IEnumerable<Element>{
  private readonly Document _doc;private Type _type=typeof(Element);public FilteredElementCollector(Document doc){_doc=doc;}
  public FilteredElementCollector OfClass(Type type){_type=type;return this;}
  public IEnumerator<Element> GetEnumerator()=>_doc.Items.Where(x=>_type.IsInstanceOfType(x)).ToList().GetEnumerator();
  IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
 }
 public class Transaction:IDisposable{
  private readonly Document _doc;private TransactionStatus _status;private List<Element> _items;
  private List<(View3D v,bool p,bool c,int a,int s,ViewOrientation3D o,string n)> _state;
  public Transaction(Document doc,string name){_doc=doc;}
  public TransactionStatus Start(){_doc.Transactions++;_doc.InTx=true;_items=_doc.Items.ToList();
   _state=_doc.Items.OfType<View3D>().Select(v=>(v,v.IsPerspective,v.Crop,v.ActiveFar,v.SectionFar,v.Saved,v.Name)).ToList();return _status=TransactionStatus.Started;}
  public bool HasStarted()=>_status==TransactionStatus.Started;
  public TransactionStatus RollBack(){
   foreach(var created in _doc.Items.Except(_items))created.IsValidObject=false;
   _doc.Items=_items.ToList();
   foreach(var s in _state){s.v.IsPerspective=s.p;s.v.Crop=s.c;s.v.ActiveFar=s.a;s.v.SectionFar=s.s;s.v.Saved=s.o;s.v.Name=s.n;}
   _doc.InTx=false;return _status=TransactionStatus.RolledBack;}
  public TransactionStatus Commit(){if(_doc.FailCommit)return RollBack();_doc.InTx=false;return _status=TransactionStatus.Committed;}
  public void Dispose(){if(HasStarted())RollBack();}
 }
}
namespace Autodesk.Revit.DB.Events {
 public class DocumentClosedEventArgs:EventArgs{}
 public class DocumentChangedEventArgs:EventArgs{public Document Document;public UndoOperation Operation;
  public Document GetDocument()=>Document;
  public ICollection<ElementId> GetModifiedElementIds()=>Array.Empty<ElementId>();
  public ICollection<ElementId> GetAddedElementIds()=>Array.Empty<ElementId>();}
}
namespace Autodesk.Revit.UI.Events {public class IdlingEventArgs:EventArgs{}}
namespace Autodesk.Revit.UI {
 public class Application{
  public event EventHandler<DocumentChangedEventArgs> DocumentChanged;public event EventHandler<DocumentClosedEventArgs> DocumentClosed;
  public bool Subscribed=>DocumentChanged!=null;}
 public class UIView{public ElementId ViewId;public void ZoomToFit(){}}
 public class UIDocument{public Document Document;public View ActiveView;public List<UIView> Open=new List<UIView>();public IList<UIView> GetOpenUIViews()=>Open;}
 public class UIApplication{public UIDocument ActiveUIDocument;public Application Application=new Application();public event EventHandler<IdlingEventArgs> Idling;}
}
namespace RvtMcp.Plugin {
 public interface IRevitCommand{}
 public class CommandResult{public bool Success;public string Error;public object Data;
  public static CommandResult Fail(string error)=>new CommandResult{Error=error};
  public static CommandResult Ok(object data)=>new CommandResult{Success=true,Data=data};}
 public static class App{public static void DebugLog(string message){}}
}
public static class Probe {
 public static string Run(string tool,string scenario){
  var doc=new Document{FailCommit=scenario.EndsWith("commit_failure")};
  doc.Add(new ViewFamilyType{Id=new ElementId(10),Name="3D View",ViewFamily=ViewFamily.ThreeDimensional});
  var ui=new UIDocument{Document=doc};var app=new UIApplication{ActiveUIDocument=ui};
  View3D view=null;RvtMcp.Plugin.CommandResult result;
  if(tool=="orient"){
   var isDefault=scenario.StartsWith("default");
   view=new View3D{Id=new ElementId(20),Name=isDefault?"{3D}":"Overview",CanSave=!isDefault,Crop=true,
    IsLocked=scenario=="locked",FailOn=scenario=="named_exception"?"crop":null};
   doc.Add(view);ui.ActiveView=view;ui.Open.Add(new UIView{ViewId=view.Id});
   var request=new JObject{["eye"]=new JArray(-50000,-50000,30000),["target"]=new JArray(0,0,0)};
   result=new RvtMcp.Plugin.Handlers.SetViewOrientationHandler().Execute(app,request.ToString());
  }else{
   View3D.CreateWithoutActiveFar=scenario=="missing_far_param";
   doc.Add(new View3D{Id=new ElementId(20),Name="Overview"});
   if(scenario=="plan_same_name")doc.Add(new ViewPlan{Id=new ElementId(30),Name="Entrance Camera"});
   if(scenario=="isometric_same_name")doc.Add(new View3D{Id=new ElementId(31),Name="entrance camera"});
   if(scenario.StartsWith("reaim")){view=new View3D{Id=new ElementId(32),Name="Entrance Camera",IsPerspective=true,
    FailOn=scenario=="reaim_exception"?"far":null};doc.Add(view);}
   var request=new JObject{["name"]="Entrance Camera",["eye"]=new JArray(0,-30000,1700),["target"]=new JArray(0,0,1700),["far_clip"]=scenario=="far_clip_kept"};
   result=new RvtMcp.Plugin.Handlers.CreateCameraViewHandler().Execute(app,request.ToString());
   view=view??doc.Items.OfType<View3D>().FirstOrDefault(v=>v.IsPerspective&&v.Name=="Entrance Camera");
  }
  return JsonConvert.SerializeObject(new{success=result.Success,error=result.Error,data=result.Data,transactions=doc.Transactions,
   tracked=app.Application.Subscribed,cameras=doc.Items.OfType<View3D>().Count(v=>v.IsPerspective),
   saved_eye_x_mm=view==null?(double?)null:Math.Round(view.Saved.EyePosition.X*304.8,1),
   current_eye_x_mm=view==null?(double?)null:Math.Round(view.Current.EyePosition.X*304.8,1),
   crop=view?.Crop,active_far=view?.ActiveFar,section_far=view?.SectionFar});
 }
}
""";
}
