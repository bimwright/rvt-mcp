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

public class DocumentTargetGuardTests
{
    [Fact]
    public void Closed_document_does_not_break_stamps_for_other_or_reopened_documents()
    {
        var result = JObject.Parse((string)CompileProbe().Invoke(null, null)!);
        Assert.Equal(result.Value<string>("first"), result.Value<string>("anotherWrapper"));
        Assert.Equal(result.Value<string>("other"), result.Value<string>("afterClose"));
        Assert.NotEqual(result.Value<string>("first"), result.Value<string>("other"));
        Assert.NotEqual(result.Value<string>("first"), result.Value<string>("reopened"));
        Assert.Null(result.Value<string>("noDocument"));
    }

    private static MethodInfo CompileProbe([CallerFilePath] string testFile = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", ".."));
        var source = File.ReadAllText(Path.Combine(root, "src", "shared", "Infrastructure", "Execution", "DocumentTargetGuard.cs"));
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Append(typeof(JObject).Assembly.Location).Distinct()
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("DocumentStampProbe_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(source), CSharpSyntaxTree.ParseText(Api) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var emitted = compilation.Emit(output);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return Assembly.Load(output.ToArray()).GetType("DocumentStampProbe")!.GetMethod("Run")!;
    }

    // Revit's Document hash accesses native state. An invalid wrapper must never be
    // hashed while pruning closed documents, but equivalent live wrappers keep a token.
    private const string Api = """
using System;
using Newtonsoft.Json.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
namespace Autodesk.Revit.DB {
 public class Lifetime { public int Id; public bool Valid = true; }
 public class Document {
  public Lifetime Lifetime;
  public bool IsValidObject => Lifetime.Valid;
  public string Title => "same-name";
  public bool IsFamilyDocument => false;
  public override int GetHashCode() => IsValidObject ? Lifetime.Id : throw new InvalidOperationException("closed_document_hash");
  public override bool Equals(object value) => value is Document other && Lifetime.Id == other.Lifetime.Id;
 }
}
namespace Autodesk.Revit.UI {
 public class UIDocument { public Document Document; }
 public class Application { public string VersionNumber => "2022"; }
 public class UIApplication { public UIDocument ActiveUIDocument; public Application Application = new Application(); }
}
public class DocumentStampProbe {
 public static string Run() {
  var life = new Lifetime { Id = 1 };
  var first = new Document { Lifetime = life };
  var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = first } };
  Func<string> token = () => RvtMcp.Plugin.DocumentTargetGuard.Capture(app).Value<string>("document_id");
  var a = token();
  app.ActiveUIDocument.Document = new Document { Lifetime = life };
  var wrapper = token();
  var other = new Document { Lifetime = new Lifetime { Id = 2 } };
  app.ActiveUIDocument.Document = other;
  var b = token();
  life.Valid = false;
  var afterClose = token();
  other.Lifetime.Valid = false;
  app.ActiveUIDocument = null;
  var noDocument = token();
  app.ActiveUIDocument = new UIDocument { Document = new Document { Lifetime = new Lifetime { Id = 3 } } };
  var reopened = token();
  return new JObject { ["first"] = a, ["anotherWrapper"] = wrapper, ["other"] = b,
   ["afterClose"] = afterClose, ["noDocument"] = noDocument, ["reopened"] = reopened }.ToString();
 }
}
""";
}
