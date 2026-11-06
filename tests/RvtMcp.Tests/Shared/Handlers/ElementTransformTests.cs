using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Plugin.Handlers;
using RvtMcp.Server;
using RvtMcp.Tests.Helpers;
using Xunit;

namespace RvtMcp.Tests;

public class ElementTransformTests
{
    private static readonly Lazy<MethodInfo> Probe = new(() => Compile());

    [Theory]
    [InlineData("invalid_json")]
    [InlineData("empty_ids")]
    [InlineData("fractional_id")]
    [InlineData("missing_id")]
    [InlineData("missing_delta")]
    [InlineData("string_delta")]
    [InlineData("nonfinite")]
    [InlineData("pinned")]
    [InlineData("type")]
    [InlineData("group_member")]
    [InlineData("no_location")]
    [InlineData("unbounded_curve")]
    [InlineData("readonly")]
    [InlineData("no_document")]
    public void InvalidSelectionOrInputDoesNotStartTransaction(string scenario)
    {
        var result = Run(scenario);
        Assert.False(result.Value<bool>("success"));
        Assert.Equal(0, result.Value<int>("started"));
        Assert.Equal(0, result.Value<int>("calls"));
    }

    [Theory]
    [InlineData("move")]
    [InlineData("move_curve")]
    [InlineData("duplicate_ids")]
    [InlineData("rotate")]
    [InlineData("rotate_curve")]
    [InlineData("rotate_negative")]
    [InlineData("rotate_wrapped")]
    public void ValidTransformsReportActualPlacementAndCommitOnce(string scenario)
    {
        var result = Run(scenario);
        Assert.True(result.Value<bool>("success"), result.Value<string>("error"));
        Assert.Equal(1, result.Value<int>("committed"));
        Assert.Equal(1, result.Value<int>("groupCommitted"));
        Assert.Equal(0, result.Value<int>("groupRolledBack"));
        Assert.Equal(0, result.Value<int>("markerDepth"));
        Assert.Equal(0, result.Value<int>("rolledBack"));
        Assert.Equal(1, result["data"]!.Value<int>("count"));
        var after = result["data"]!["after"]![0]!;
        var point = after["location_mm"]!;
        if (scenario.StartsWith("rotate"))
        {
            Assert.Equal(1000, point.Value<double>("x"), 6);
            Assert.Equal(scenario == "rotate_negative" ? 1900 : 2100, point.Value<double>("y"), 6);
        }
        else
        {
            Assert.Equal(1200, point.Value<double>("x"), 6);
            Assert.Equal(1950, point.Value<double>("y"), 6);
            Assert.Equal(325, point.Value<double>("z"), 6);
        }
    }

    [Theory]
    [InlineData("move_constrained")]
    [InlineData("move_second_constrained")]
    [InlineData("move_curve_end_constrained")]
    [InlineData("rotate_ignored")]
    [InlineData("rotate_angle_constrained")]
    [InlineData("lost_location")]
    [InlineData("api_throw")]
    [InlineData("commit_rejected")]
    public void FailureRollsBackWholeSelection(string scenario)
    {
        var result = Run(scenario);
        Assert.False(result.Value<bool>("success"));
        Assert.Equal(0, result.Value<int>("committed"));
        Assert.Equal(1, result.Value<int>("rolledBack"));
        Assert.True(result.Value<bool>("restored"));
    }

    [Fact]
    public void IdValidationLimitsRawInputAndDeduplicates()
    {
        Assert.Throws<ArgumentException>(() => PlacementInput.ElementIds(null));
        Assert.Throws<ArgumentException>(() => PlacementInput.ElementIds(Array.Empty<long>()));
        Assert.Throws<ArgumentException>(() => PlacementInput.ElementIds(new long[] { 0 }));
        Assert.Throws<ArgumentException>(() => PlacementInput.ElementIds(new long[] { -1 }));
        Assert.Throws<ArgumentException>(() => PlacementInput.ElementIds(Enumerable.Repeat(1L, 1001).ToArray()));
        Assert.Equal(new long[] { 2, 1 }, PlacementInput.ElementIds(new long[] { 2, 1, 2 }));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonfiniteInputsFail(double value)
        => Assert.Throws<ArgumentException>(() => PlacementInput.Finite(value));

    [Theory]
    [InlineData("move_elements")]
    [InlineData("rotate_elements")]
    public void TransformsRequireConfirmationAndCannotRunReadOnly(string command)
    {
        Assert.True(ToolReadPolicy.RequiresDocumentConfirmation(command));
        Assert.NotNull(ToolReadPolicy.Rejection(command, true, false));
        Assert.Equal(RvtMcp.Plugin.Views.Toast.ToolActivityKind.Write,
            RvtMcp.Plugin.Views.Toast.ToolActivityClassifier.Classify(command));
    }

    [Fact]
    public async Task WrappersPreserveUnitsAndExplicitPivot()
    {
        var move = await Capture.Send(() => ModifyTools.MoveElements(new long[] { 42 }, 100, -50, 25));
        Assert.Equal("move_elements", move.Command);
        Assert.Equal(25, move.Json().Value<double>("dz_mm"));
        var rotate = await Capture.Send(() => ModifyTools.RotateElements(new long[] { 42 }, -90, 1200, 3400, 0));
        Assert.Equal("rotate_elements", rotate.Command);
        Assert.Equal(-90, rotate.Json().Value<double>("angle_degrees"));
        Assert.Equal(3400, rotate.Json().Value<double>("pivot_y_mm"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void TransformFailurePreservesBatchRollbackPolicy(bool continueOnError, bool rollsBack)
    {
        var calls = 0;
        var commands = JArray.Parse("[{\"command\":\"move_elements\",\"params\":{}},{\"command\":\"rotate_elements\",\"params\":{}}]");
        var outcome = BatchExecutor.Run(commands, continueOnError, (name, json) =>
        {
            calls++;
            return name == "move_elements" ? BatchExecutor.InvokeResult.Fail("constrained transform")
                : BatchExecutor.InvokeResult.Ok(null);
        });
        Assert.True(outcome.AnyFailed);
        Assert.Equal(rollsBack, outcome.ShouldRollBack(continueOnError));
        Assert.Equal(continueOnError ? 2 : 1, calls);
    }

    [Theory]
    [InlineData("move_commit_adjusted")]
    [InlineData("rotate_commit_angle")]
    [InlineData("move_commit_curve_end")]
    [InlineData("move_commit_second_adjusted")]
    [InlineData("move_commit_deleted")]
    [InlineData("move_commit_snapshot_failure")]
    [InlineData("move_group_rejected")]
    [InlineData("move_group_throw")]
    public void CommitTimeChangesCannotEscapeAtomicRollback(string scenario)
    {
        var result = Run(scenario);
        Assert.False(result.Value<bool>("success"), result.ToString());
        Assert.True(result.Value<bool>("restored"), result.ToString());
        Assert.Equal(1, result.Value<int>("groupRolledBack"));
        Assert.Equal(0, result.Value<int>("groupCommitted"));
        Assert.Equal(0, result.Value<int>("markerDepth"));
    }

    [Theory]
    [InlineData("group_start_rejected")]
    [InlineData("transaction_start_rejected")]
    public void RefusedTransactionStartCannotMutateTheDocument(string scenario)
    {
        var result = Run(scenario);
        Assert.False(result.Value<bool>("success"), result.ToString());
        Assert.True(result.Value<bool>("restored"));
        Assert.Equal(0, result.Value<int>("started"));
        Assert.Equal(0, result.Value<int>("calls"));
        Assert.Equal(0, result.Value<int>("committed"));
        Assert.Equal(0, result.Value<int>("groupCommitted"));
        Assert.Equal(0, result.Value<int>("markerDepth"));
    }

    private static JObject Run(string scenario) => JObject.Parse((string)Probe.Value.Invoke(null, new object[] { scenario })!);

    private static MethodInfo Compile([CallerFilePath] string file = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "..", "..", ".."));
        var sources = new[] { "TransformElementsHandlers.cs", "PlacementInput.cs" }
            .Select(name => File.ReadAllText(Path.Combine(root, "src/shared/Handlers/Elements", name))).Append(Doubles);
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(JObject).Assembly.Location).Distinct().Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("TransformProbe_" + Guid.NewGuid().ToString("N"),
            sources.Select(s => CSharpSyntaxTree.ParseText(s)), refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return Assembly.Load(output.ToArray()).GetType("TransformProbe")!.GetMethod("Run")!;
    }

    // Exercise the production handlers at the API seam, including a partially
    // applied selection. These doubles do not replace live Revit acceptance.
    private const string Doubles = """
using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Autodesk.Revit.DB;
namespace Autodesk.Revit.DB {
 public class ElementId { public long Value; public ElementId(long v){Value=v;} public static readonly ElementId InvalidElementId=new ElementId(-1); public static bool operator ==(ElementId a,ElementId b)=>ReferenceEquals(a,b)||(!(a is null)&&!(b is null)&&a.Value==b.Value); public static bool operator !=(ElementId a,ElementId b)=>!(a==b); public override bool Equals(object o)=>o is ElementId id&&this==id; public override int GetHashCode()=>Value.GetHashCode(); }
 public class XYZ { public double X,Y,Z; public XYZ(double x,double y,double z){X=x;Y=y;Z=z;} public static XYZ BasisZ=>new XYZ(0,0,1); public static XYZ operator +(XYZ a,XYZ b)=>new XYZ(a.X+b.X,a.Y+b.Y,a.Z+b.Z); public double DistanceTo(XYZ b)=>Math.Sqrt((X-b.X)*(X-b.X)+(Y-b.Y)*(Y-b.Y)+(Z-b.Z)*(Z-b.Z)); }
 public class Transform { double a; XYZ pivot,delta; public static Transform CreateRotationAtPoint(XYZ axis,double angle,XYZ p)=>new Transform{a=angle,pivot=p}; public static Transform CreateTranslation(XYZ d)=>new Transform{delta=d}; public XYZ OfPoint(XYZ p){if(delta!=null)return p+delta;var x=p.X-pivot.X;var y=p.Y-pivot.Y;return new XYZ(pivot.X+x*Math.Cos(a)-y*Math.Sin(a),pivot.Y+x*Math.Sin(a)+y*Math.Cos(a),p.Z);} }
 public class Curve { public bool IsBound=true; public XYZ A,B; public XYZ GetEndPoint(int i)=>i==0?A:B; }
 public class Line:Curve {public static Line CreateBound(XYZ a,XYZ b)=>new Line{A=a,B=b};}
 public class Location {}
 public class LocationPoint:Location { public XYZ Point; public double Rotation; }
 public class LocationCurve:Location { public Curve Curve; }
 public class BoundingBoxXYZ {public XYZ Min,Max;}
 public class Element {public ElementId Id; public Location Location; public bool Pinned; public ElementId GroupId=ElementId.InvalidElementId; public bool ThrowOnSnapshot; public BoundingBoxXYZ get_BoundingBox(object view){if(ThrowOnSnapshot)throw new InvalidOperationException("injected post-commit snapshot failure");return null;}}
 public class ElementType:Element {}
 public enum TransactionStatus {Started,Committed,RolledBack}
 public class Document {public string Scenario; public bool IsReadOnly; public List<Element> Elements=new List<Element>(); public int Started,Committed,RolledBack,Calls,GroupCommitted,GroupRolledBack; public Element GetElement(ElementId id)=>Elements.FirstOrDefault(e=>e.Id==id); public void Regenerate(){} }
 public class Transaction:IDisposable {
  Document d; List<Location> saved; TransactionStatus status;
  public Transaction(Document doc,string name){d=doc;}
  public static Location Clone(Location l){if(l is LocationPoint p)return new LocationPoint{Point=p.Point,Rotation=p.Rotation};if(l is LocationCurve c)return new LocationCurve{Curve=new Curve{A=c.Curve.A,B=c.Curve.B,IsBound=c.Curve.IsBound}};return null;}
  public TransactionStatus Start(){if(d.Scenario=="transaction_start_rejected")return status=TransactionStatus.RolledBack;d.Started++;saved=d.Elements.Select(e=>Clone(e.Location)).ToList();return status=TransactionStatus.Started;}
  public TransactionStatus GetStatus()=>status;
  public TransactionStatus Commit(){if(d.Scenario=="move_commit_adjusted"||d.Scenario=="move_commit_second_adjusted"){((LocationPoint)d.Elements.Last().Location).Point=new XYZ(1100/304.8,2000/304.8,300/304.8);}if(d.Scenario=="rotate_commit_angle")((LocationPoint)d.Elements[0].Location).Rotation=0;if(d.Scenario=="move_commit_curve_end")((LocationCurve)d.Elements[0].Location).Curve.B=new XYZ(1100/304.8,2000/304.8+1,300/304.8);if(d.Scenario=="move_commit_deleted")d.Elements.RemoveAt(0);if(d.Scenario=="move_commit_snapshot_failure")d.Elements[0].ThrowOnSnapshot=true;if(d.Scenario=="commit_rejected"){RollBack();return status;}d.Committed++;return status=TransactionStatus.Committed;}
  public void RollBack(){d.RolledBack++;for(int i=0;i<saved.Count;i++)d.Elements[i].Location=saved[i];status=TransactionStatus.RolledBack;}
  public void Dispose(){if(status==TransactionStatus.Started)RollBack();}
 }
 public class TransactionGroup:IDisposable {
  Document d; Element[] elements; Location[] locations; TransactionStatus status=TransactionStatus.RolledBack;
  public TransactionGroup(Document doc,string name){d=doc;}
  public TransactionStatus Start(){if(d.Scenario=="group_start_rejected")return status=TransactionStatus.RolledBack;elements=d.Elements.ToArray();locations=elements.Select(e=>Transaction.Clone(e.Location)).ToArray();return status=TransactionStatus.Started;}
  public TransactionStatus GetStatus()=>status;
  public TransactionStatus Assimilate(){if(d.Scenario=="move_group_throw")throw new InvalidOperationException("injected group failure");if(d.Scenario=="move_group_rejected")return RollBack();d.GroupCommitted++;return status=TransactionStatus.Committed;}
  public TransactionStatus RollBack(){d.GroupRolledBack++;d.Elements=elements.ToList();for(int i=0;i<elements.Length;i++){elements[i].Location=locations[i];elements[i].ThrowOnSnapshot=false;}return status=TransactionStatus.RolledBack;}
  public void Dispose(){if(status==TransactionStatus.Started)RollBack();}
 }
 public static class ElementTransformUtils {
  public static void MoveElements(Document d,ICollection<ElementId> ids,XYZ delta)=>Apply(d,ids,Transform.CreateTranslation(delta),0);
  public static void RotateElements(Document d,ICollection<ElementId> ids,Line axis,double radians)=>Apply(d,ids,Transform.CreateRotationAtPoint(XYZ.BasisZ,radians,axis.A),radians);
  static void Apply(Document d,ICollection<ElementId> ids,Transform t,double angle){
   d.Calls++;if(d.Scenario=="api_throw")throw new Exception("injected API failure");
   foreach(var id in ids){var e=d.GetElement(id);if(d.Scenario=="rotate_ignored"||d.Scenario=="move_constrained"||(d.Scenario=="move_second_constrained"&&id.Value==2))continue;
    if(e.Location is LocationPoint p){p.Point=t.OfPoint(p.Point);if(d.Scenario!="rotate_angle_constrained")p.Rotation=(p.Rotation+angle)%(2*Math.PI);}
    else if(e.Location is LocationCurve c){c.Curve.A=t.OfPoint(c.Curve.A);if(d.Scenario!="move_curve_end_constrained")c.Curve.B=t.OfPoint(c.Curve.B);}
    if(d.Scenario=="lost_location")e.Location=null;
   }
  }
 }
}
namespace Autodesk.Revit.UI { public class UIDocument {public Document Document;} public class UIApplication {public UIDocument ActiveUIDocument;} }
namespace RvtMcp.Plugin.Failures {
 public static class FailureControl {
  public static int Depth,Rollbacks;
  public static IDisposable BeginBatch(){Depth++;return new Marker();}
  public static void NotifyBatchRolledBack(){if(Depth<=0)throw new InvalidOperationException("Missing group marker");Rollbacks++;}
  class Marker:IDisposable {public void Dispose(){Depth--;}}
 }
}
namespace RvtMcp.Plugin {
 public interface IRevitCommand {string Name{get;} string Description{get;} string ParametersSchema{get;} CommandResult Execute(Autodesk.Revit.UI.UIApplication app,string json);}
 public class CommandResult {public bool Success;public string Error;public object Data;public static CommandResult Fail(string e)=>new CommandResult{Error=e};public static CommandResult Ok(object d)=>new CommandResult{Success=true,Data=d};}
 public static class RevitCompat {public static ElementId ToElementId(long id)=>new ElementId(id);public static long GetId(ElementId id)=>id.Value;}
}
public static class TransformProbe {
 public static string Run(string scenario){
  RvtMcp.Plugin.Failures.FailureControl.Depth=0;RvtMcp.Plugin.Failures.FailureControl.Rollbacks=0;
  var d=new Document{Scenario=scenario,IsReadOnly=scenario=="readonly"};
  var original=new XYZ(1100/304.8,2000/304.8,300/304.8);
  Element e=scenario=="type"?new ElementType():new Element();e.Id=new ElementId(1);e.Location=new LocationPoint{Point=original};e.Pinned=scenario=="pinned";if(scenario=="group_member")e.GroupId=new ElementId(7);
  if(scenario.Contains("curve"))e.Location=new LocationCurve{Curve=new Curve{A=original,B=original+new XYZ(0,1,0),IsBound=scenario!="unbounded_curve"}};
  if(scenario=="no_location")e.Location=null;
  d.Elements.Add(e);
  var args=new JObject{["elementIds"]=new JArray(1),["dx_mm"]=100,["dy_mm"]=-50,["dz_mm"]=25,["angle_degrees"]=90,["pivot_x_mm"]=1000,["pivot_y_mm"]=2000,["pivot_z_mm"]=0};
  if(scenario=="missing_id")args["elementIds"]=new JArray(1,999);
  if(scenario=="fractional_id")args["elementIds"]=new JArray(1.5);
  if(scenario=="empty_ids")args["elementIds"]=new JArray();
  if(scenario=="duplicate_ids")args["elementIds"]=new JArray(1,1);
  if(scenario=="missing_delta")args.Remove("dz_mm");
  if(scenario=="string_delta")args["dx_mm"]="100";
  if(scenario=="nonfinite")args["dx_mm"]=double.NaN;
  if(scenario=="rotate_negative")args["angle_degrees"]=-90;
  if(scenario=="rotate_wrapped")args["angle_degrees"]=450;
  if(scenario=="move_second_constrained"||scenario=="move_commit_second_adjusted"){d.Elements.Add(new Element{Id=new ElementId(2),Location=new LocationPoint{Point=original}});args["elementIds"]=new JArray(1,2);}
  var app=new Autodesk.Revit.UI.UIApplication{ActiveUIDocument=scenario=="no_document"?null:new Autodesk.Revit.UI.UIDocument{Document=d}};
  RvtMcp.Plugin.IRevitCommand handler=scenario.StartsWith("rotate")?(RvtMcp.Plugin.IRevitCommand)new RvtMcp.Plugin.Handlers.RotateElementsHandler():new RvtMcp.Plugin.Handlers.MoveElementsHandler();
  var result=handler.Execute(app,scenario=="invalid_json"?"{":args.ToString(Formatting.None));
  bool restored=d.Elements.All(el=>el.Location is LocationPoint p?p.Point.DistanceTo(original)<1e-9:el.Location is LocationCurve c&&c.Curve.A.DistanceTo(original)<1e-9&&c.Curve.B.DistanceTo(original+new XYZ(0,1,0))<1e-9);
  return JsonConvert.SerializeObject(new{success=result.Success,error=result.Error,data=result.Data,started=d.Started,committed=d.Committed,rolledBack=d.RolledBack,calls=d.Calls,groupCommitted=d.GroupCommitted,groupRolledBack=d.GroupRolledBack,markerDepth=RvtMcp.Plugin.Failures.FailureControl.Depth,restored});
 }
}
""";
}
