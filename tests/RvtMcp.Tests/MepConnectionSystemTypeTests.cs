using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace RvtMcp.Tests
{
    public class MepConnectionSystemTypeTests
    {
        private static readonly Lazy<MethodInfo> Probe = new(() => CompileProbe());

        [Theory]
        [InlineData("pipe_mismatch", "True")]
        [InlineData("hvac_mismatch", "True")]
        [InlineData("same_type_different_instances", "False")]
        [InlineData("same_name_different_types", "True")]
        [InlineData("unassigned_equipment", "False")]
        [InlineData("selected_equipment_port", "20")]
        [InlineData("unassigned_curve_type", "10")]
        [InlineData("unassigned_curve_no_type", "null")]
        [InlineData("cable_tray", "null")]
        public void ResolvesSelectedPortsAndComparesTypeIds(string scenario, string expected)
            => Assert.Equal(expected, Probe.Value.Invoke(null, new object[] { scenario }));

        [Theory]
        [InlineData("system_getter_throws")]
        [InlineData("curve_parameter_missing")]
        [InlineData("assigned_type_missing")]
        [InlineData("type_not_resolvable")]
        public void UnreadableMetadataIsNotTreatedAsUnassigned(string scenario)
        {
            var exception = Assert.Throws<TargetInvocationException>(() => Probe.Value.Invoke(null, new object[] { scenario }));
            Assert.IsAssignableFrom<InvalidOperationException>(exception.InnerException);
        }

        private static MethodInfo CompileProbe([CallerFilePath] string testFile = "")
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));
            var source = File.ReadAllText(Path.Combine(root, "src", "shared", "Handlers", "MepConnectionSystemType.cs"));
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create("ConnectionTypeProbe_" + Guid.NewGuid().ToString("N"),
                new[] { source, ApiAndProbe }.Select(s => CSharpSyntaxTree.ParseText(s)), references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var output = new MemoryStream();
            var result = compilation.Emit(output);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            return Assembly.Load(output.ToArray()).GetType("ConnectionTypeProbe")!.GetMethod("Run")!;
        }

        private const string ApiAndProbe = """
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using RvtMcp.Plugin.Handlers;
namespace Autodesk.Revit.DB {
 public enum Domain {DomainPiping,DomainHvac,DomainCableTrayConduit}
 public enum BuiltInParameter {RBS_PIPING_SYSTEM_TYPE_PARAM,RBS_DUCT_SYSTEM_TYPE_PARAM}
 public class ElementId {
  public long Value; public ElementId(long v){Value=v;}
  public static ElementId InvalidElementId=new ElementId(-1);
  public static bool operator ==(ElementId a,ElementId b)=>ReferenceEquals(a,b)||(!ReferenceEquals(a,null)&&!ReferenceEquals(b,null)&&a.Value==b.Value);
  public static bool operator !=(ElementId a,ElementId b)=>!(a==b);
  public override bool Equals(object o)=>o is ElementId id&&this==id;
  public override int GetHashCode()=>Value.GetHashCode();
 }
 public class Document {public Dictionary<long,Element> Elements=new Dictionary<long,Element>();public Element GetElement(ElementId id)=>Elements.TryGetValue(id.Value,out var e)?e:null;}
 public class Element {public ElementId Id;public Document Document;public Parameter Parameter;public Parameter get_Parameter(BuiltInParameter p)=>Parameter;}
 public class MEPCurve:Element {}
 public class MEPSystemType:Element {public string Name="same display name";}
 public class MEPSystem {public ElementId TypeId;public ElementId GetTypeId()=>TypeId;}
 public class Parameter {public bool HasValue=true;public ElementId TypeId;public ElementId AsElementId()=>TypeId;}
 public class Connector {public Domain Domain;public Element Owner;public MEPSystem System;public bool Throw;
  public MEPSystem MEPSystem {get {if(Throw)throw new InvalidOperationException("API read failed");return System;}}
 }
}
public class ConnectionTypeProbe {
 public static string Run(string scenario) {
  var doc=new Document();var a=new MEPSystemType {Id=new ElementId(10)};var b=new MEPSystemType {Id=new ElementId(20)};
  doc.Elements.Add(10,a);doc.Elements.Add(20,b);
  var first=new Connector {Owner=new MEPCurve {Document=doc,Parameter=new Parameter {TypeId=a.Id}},Domain=Domain.DomainPiping,System=new MEPSystem {TypeId=a.Id}};
  var second=new Connector {Owner=new Element {Document=doc},Domain=Domain.DomainPiping,System=new MEPSystem {TypeId=b.Id}};
  switch(scenario) {
   case "hvac_mismatch":first.Domain=second.Domain=Domain.DomainHvac;break;
   case "pipe_mismatch":case "same_name_different_types":break;
   case "same_type_different_instances":second.System=new MEPSystem {TypeId=new ElementId(10)};break;
   case "unassigned_equipment":second.System=null;break;
   case "selected_equipment_port":return MepConnectionSystemType.Read(second).Id.Value.ToString();
   case "unassigned_curve_type":first.System=null;return MepConnectionSystemType.Read(first).Id.Value.ToString();
   case "unassigned_curve_no_type":first.System=null;first.Owner.Parameter.HasValue=false;return MepConnectionSystemType.Read(first)==null?"null":"assigned";
   case "cable_tray":first.Domain=Domain.DomainCableTrayConduit;first.Throw=true;return MepConnectionSystemType.Read(first)==null?"null":"assigned";
   case "system_getter_throws":first.Throw=true;break;
   case "curve_parameter_missing":first.System=null;first.Owner.Parameter=null;break;
   case "assigned_type_missing":first.System.TypeId=ElementId.InvalidElementId;break;
   case "type_not_resolvable":first.System.TypeId=new ElementId(999);break;
   default:throw new Exception("Unknown scenario");
  }
  return MepConnectionSystemType.Mismatch(MepConnectionSystemType.Read(first),MepConnectionSystemType.Read(second)).ToString();
 }
}
""";
    }
}
