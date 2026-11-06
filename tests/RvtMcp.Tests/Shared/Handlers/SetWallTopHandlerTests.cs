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

public class SetWallTopHandlerTests
{
    private static readonly Lazy<MethodInfo> Probe = new(() => Compile());

    [Theory]
    [InlineData(300, 4300)]
    [InlineData(-300, 3700)]
    public void Attaches_unconnected_walls_before_setting_offset_and_reports_stored_values(int offset, int height)
    {
        var result = Run("valid", offset);
        Assert.True(result.Value<bool>("success"));
        Assert.Equal(1, result.Value<int>("transactions"));
        Assert.Equal(2, result["data"]!.Value<int>("updated"));
        foreach (var wall in result["data"]!["walls"]!)
        {
            Assert.Equal("Level 2", wall.Value<string>("top_constraint"));
            Assert.Equal(offset, wall.Value<double>("top_offset_mm"));
            Assert.Equal(height, wall.Value<double>("height_mm"));
        }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("non_wall")]
    [InlineData("missing_id")]
    [InlineData("overflow")]
    [InlineData("below_base")]
    [InlineData("shared_top_below_base")]
    [InlineData("unknown_level")]
    [InlineData("no_document")]
    public void Rejects_invalid_input_before_starting_transaction(string scenario)
    {
        var result = Run(scenario);
        Assert.False(result.Value<bool>("success"));
        Assert.Equal(0, result.Value<int>("transactions"));
        Assert.True(JToken.DeepEquals(result["before"], result["after"]));
    }

    [Theory]
    [InlineData("top_readonly")]
    [InlineData("offset_readonly")]
    [InlineData("top_missing")]
    [InlineData("offset_missing")]
    [InlineData("offset_throw")]
    [InlineData("commit_failure")]
    public void Later_wall_or_commit_failure_restores_every_wall(string scenario)
    {
        var result = Run(scenario);
        Assert.False(result.Value<bool>("success"));
        Assert.Equal(1, result.Value<int>("transactions"));
        Assert.True(JToken.DeepEquals(result["before"], result["after"]));
        Assert.Equal(JTokenType.Null, result["data"]!.Type);
    }

    [Theory]
    [InlineData("shared_top_negative")]
    [InlineData("shared_base_positive")]
    public void Compares_physical_elevations_when_levels_use_different_display_bases(string scenario)
    {
        var result = Run(scenario);
        Assert.True(result.Value<bool>("success"));
        Assert.Equal(2, result["data"]!.Value<int>("updated"));
        Assert.All(result["data"]!["walls"]!, wall => Assert.Equal(4000, wall.Value<double>("height_mm")));
    }

    private static JObject Run(string scenario, int offset = 0)
        => JObject.Parse((string)Probe.Value.Invoke(null, new object[] { scenario, offset })!);

    private static MethodInfo Compile([CallerFilePath] string file = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "..", "..", ".."));
        var sources = new[] { "src/shared/Handlers/Elements/SetWallTopHandler.cs", "src/shared/Infrastructure/RevitCompat.cs" }
            .Select(path => File.ReadAllText(Path.Combine(root, path))).Append(ApiSeam);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(JObject).Assembly.Location).Distinct().Select(path => MetadataReference.CreateFromFile(path));
        // No newer-year symbol: the unchanged RevitCompat source follows its R22/int32 path.
        var compilation = CSharpCompilation.Create("WallTop_" + Guid.NewGuid().ToString("N"),
            sources.Select(source => CSharpSyntaxTree.ParseText(source)), references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var emitted = compilation.Emit(output);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return Assembly.Load(output.ToArray()).GetType("Probe")!.GetMethod("Run")!;
    }

    // These doubles model parameter mutability and transaction restoration only.
    // They exercise production control flow, not native Revit geometry or Undo acceptance.
    private const string ApiSeam = """
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace Autodesk.Revit.DB {
 public enum BuiltInParameter { WALL_BASE_CONSTRAINT,WALL_BASE_OFFSET,WALL_HEIGHT_TYPE,WALL_TOP_OFFSET,WALL_USER_HEIGHT_PARAM }
 public class ElementId { public int IntegerValue; public ElementId(int id){IntegerValue=id;} public static ElementId InvalidElementId=new ElementId(-1); }
 public class Element { public ElementId Id; public string Name; }
 public class Level:Element { public double Elevation,ProjectElevation; }
 public class Wall:Element {
  public Document Doc; public int Top=-1; public double Offset,BaseOffset; public string Fault;
  public Parameter get_Parameter(BuiltInParameter kind) {
   if((kind==BuiltInParameter.WALL_HEIGHT_TYPE && Fault=="top_missing") || (kind==BuiltInParameter.WALL_TOP_OFFSET && Fault=="offset_missing"))return null;
   return new Parameter{Wall=this,Kind=kind};
  }
 }
 public class Parameter {
  public Wall Wall; public BuiltInParameter Kind;
  public bool IsReadOnly => (Kind==BuiltInParameter.WALL_HEIGHT_TYPE && Wall.Fault=="top_readonly")
   || (Kind==BuiltInParameter.WALL_TOP_OFFSET && (Wall.Top==-1 || Wall.Fault=="offset_readonly"));
  public ElementId AsElementId()=>new ElementId(Kind==BuiltInParameter.WALL_BASE_CONSTRAINT?100:Wall.Top);
  public double AsDouble()=>Kind==BuiltInParameter.WALL_BASE_OFFSET?Wall.BaseOffset:Kind==BuiltInParameter.WALL_TOP_OFFSET?Wall.Offset
   :Wall.Top==-1?2000/304.8:((Level)Wall.Doc.GetElement(new ElementId(Wall.Top))).ProjectElevation-Wall.BaseOffset+Wall.Offset;
  public bool Set(ElementId value){if(IsReadOnly)throw new InvalidOperationException();Wall.Top=value.IntegerValue;return true;}
  public bool Set(double value){if(IsReadOnly || Wall.Fault=="offset_throw")throw new InvalidOperationException("offset failure");Wall.Offset=value;return true;}
 }
 public class Document {
  public Dictionary<int,Element> Items=new Dictionary<int,Element>();public int Transactions;public bool FailCommit;
  public Element GetElement(ElementId id)=>Items.TryGetValue(id.IntegerValue,out var item)?item:null;
  public Wall[] Walls=>Items.Values.OfType<Wall>().OrderBy(w=>w.Id.IntegerValue).ToArray();
 }
 public class FilteredElementCollector:IEnumerable<Element> {
  private Document Doc;private Type Type;public FilteredElementCollector(Document doc){Doc=doc;}
  public FilteredElementCollector OfClass(Type type){Type=type;return this;}
  public IEnumerator<Element> GetEnumerator()=>Doc.Items.Values.Where(x=>Type.IsInstanceOfType(x)).GetEnumerator();
  IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
 }
 public enum TransactionStatus { Uninitialized,Started,Committed,RolledBack }
 public class Transaction:IDisposable {
  private Document Doc;private TransactionStatus Status;private int[] Tops;private double[] Offsets;
  public Transaction(Document doc,string name){Doc=doc;}
  public TransactionStatus Start(){Doc.Transactions++;Tops=Doc.Walls.Select(w=>w.Top).ToArray();Offsets=Doc.Walls.Select(w=>w.Offset).ToArray();return Status=TransactionStatus.Started;}
  public bool HasStarted()=>Status==TransactionStatus.Started;
  public TransactionStatus RollBack(){for(int i=0;i<Tops.Length;i++){Doc.Walls[i].Top=Tops[i];Doc.Walls[i].Offset=Offsets[i];}return Status=TransactionStatus.RolledBack;}
  public TransactionStatus Commit()=>Doc.FailCommit?RollBack():Status=TransactionStatus.Committed;
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
 public static string Run(string scenario,int offset){
  var doc=new Document{FailCommit=scenario=="commit_failure"};
  doc.Items[100]=new Level{Id=new ElementId(100),Name="Level 1",Elevation=scenario=="shared_base_positive"?100000/304.8:0,ProjectElevation=0};
  doc.Items[200]=new Level{Id=new ElementId(200),Name="Level 2",Elevation=(scenario=="shared_top_negative"?-96000:scenario=="shared_top_below_base"?100500:4000)/304.8,ProjectElevation=(scenario=="shared_top_below_base"?500:4000)/304.8};
  doc.Items[1]=new Wall{Id=new ElementId(1),Doc=doc};
  doc.Items[2]=new Wall{Id=new ElementId(2),Doc=doc,Fault=scenario,BaseOffset=scenario=="below_base"?4500/304.8:scenario=="shared_top_below_base"?1000/304.8:0};
  doc.Items[3]=new Element{Id=new ElementId(3)};
  Func<object> state=()=>doc.Walls.Select(w=>new{top=w.Top,offset=w.Offset,base_offset=w.BaseOffset}).ToArray();
  var before=state();var ids=scenario=="empty"?new long[0]:new long[]{1,scenario=="non_wall"?3:scenario=="missing_id"?999:scenario=="overflow"?2147483648L:2};
  var request=new JObject{["wall_ids"]=JArray.FromObject(ids),["top_level"]=scenario=="unknown_level"?"missing":"level 2",["top_offset"]=offset};
  var app=new UIApplication{ActiveUIDocument=scenario=="no_document"?null:new UIDocument{Document=doc}};
  var result=new RvtMcp.Plugin.Handlers.SetWallTopHandler().Execute(app,request.ToString());
  return JsonConvert.SerializeObject(new{success=result.Success,error=result.Error,data=result.Data,transactions=doc.Transactions,before,after=state()});
 }
}
""";
}
