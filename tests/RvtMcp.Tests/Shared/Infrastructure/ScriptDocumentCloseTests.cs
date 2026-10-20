using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests;

public class ScriptDocumentCloseTests
{
    [Theory]
    [InlineData("app.Application.OpenDocumentFile(\"new\")")]
    [InlineData("app.ActiveUIDocument.Document.EditFamily(null)")]
    [InlineData("app.Application.NewFamilyDocument(\"template\")")]
    [InlineData("app.Application.NewProjectDocument(\"template\")")]
    public void Explicit_discard_is_allowed_only_for_documents_opened_by_this_script(string open)
    {
        Assert.Equal("True", Execute($"var doc={open}; doc.Modified=true; return doc.Close(saveModified: false).ToString();"));
    }

    [Theory]
    [InlineData("var doc=app.ActiveUIDocument.Document; doc.Modified=true; return doc.Close(false).ToString();")]
    [InlineData("var doc=app.Application.OpenDocumentFile(\"existing\"); doc.Modified=true; return doc.Close(false).ToString();")]
    [InlineData("var doc=app.Application.OpenDocumentFile(\"new\"); doc.Modified=true; return doc.Close(true).ToString();")]
    public void Existing_documents_and_save_on_close_keep_the_native_guard(string script)
    {
        Assert.Equal("False", Execute(script));
    }

    [Fact]
    public void Cleanup_works_in_finally_and_does_not_leave_a_discard_token()
    {
        Assert.Equal("True|False", Execute("""
var doc=app.Application.OpenDocumentFile("new"); doc.Modified=true; bool closed=false;
try { throw new Exception("transaction failed"); }
catch { }
finally { closed=doc.Close(false); }
return closed + "|" + RvtMcp.Plugin.McpDocument.IsDiscardAuthorized(doc);
"""));
    }

    [Fact]
    public void Non_Revit_Close_calls_are_not_rewritten_and_receivers_run_once()
    {
        Assert.Equal("stream|True|1", Execute("""
var other=new Other();
var doc=app.Application.OpenDocumentFile("new"); doc.Modified=true;
return other.Close(false) + "|" + Get(doc).Close(false) + "|" + Count;
"""));
    }

    [Theory]
    [InlineData("var doc=app.Application?.OpenDocumentFile(\"new\"); return (doc!=null).ToString();")]
    [InlineData("Autodesk.Revit.ApplicationServices.Application native=null; return (native?.NewProjectDocument(\"template\")==null).ToString();")]
    [InlineData("var doc=app.ActiveUIDocument.Document?.EditFamily(null); return (doc!=null).ToString();")]
    [InlineData("var doc=app?.Application.OpenDocumentFile(\"new\"); return (doc!=null).ToString();")]
    [InlineData("UIApplication native=null; return (native?.Application.NewFamilyDocument(\"template\")==null).ToString();")]
    [InlineData("var doc=RvtMcp.Plugin.McpDocument.TrackOpened(app?.Application.NewProjectDocument(\"template\")); doc.Modified=true; return RvtMcp.Plugin.McpDocument.Close(doc,false).ToString();")]
    [InlineData("var doc=RvtMcp.Plugin.McpDocument.TrackOpened(app.Application?.OpenDocumentFile(\"new\")); doc.Modified=true; return RvtMcp.Plugin.McpDocument.Close(doc,false).ToString();")]
    public void Null_conditional_calls_keep_valid_syntax_and_allow_explicit_tracking(string script)
    {
        Assert.Equal("True", Execute(script));
    }

    [Fact]
    public void Ownership_does_not_leak_to_a_later_script_scope()
    {
        Assert.Equal("False", Execute("""
var doc=app.Application.OpenDocumentFile("new"); doc.Modified=true;
return "unused";
""", secondScope: true));
    }

    private static string Execute(string body, bool secondScope = false, [CallerFilePath] string file = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "..", "..", ".."));
        var helper = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "src/shared/Infrastructure/McpDocument.cs")));
        var script = CSharpSyntaxTree.ParseText("""
using System; using Autodesk.Revit.DB; using Autodesk.Revit.UI;
public class Script {
 static int Count;
 static Document Get(Document doc) { Count++; return doc; }
 class Other { public string Close(bool value) => "stream"; }
 public static string Run(UIApplication app) {
""" + body + "}}");
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("CloseProbe_" + Guid.NewGuid().ToString("N"),
            new[] { helper, script, CSharpSyntaxTree.ParseText(Api) }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        compilation = SendCodeDocumentRewriter.Apply(compilation, script);
        using var output = new MemoryStream();
        var emit = compilation.Emit(output);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        return (string)Assembly.Load(output.ToArray()).GetType("Probe")!.GetMethod("Run")!.Invoke(null, new object[] { secondScope })!;
    }

    private const string Api = """
using System; using System.Collections.Generic; using Autodesk.Revit.DB; using Autodesk.Revit.UI;
namespace Autodesk.Revit.DB {
 public class Document {
  public bool IsValidObject {get;set;} = true;
  public bool Modified;
  public Autodesk.Revit.ApplicationServices.Application App;
  public Document EditFamily(object family) => App.OpenDocumentFile("new");
  public bool Close(bool saveModified) {
   if (Modified && !RvtMcp.Plugin.McpDocument.IsDiscardAuthorized(this)) return false;
   IsValidObject=false; return true;
  }
 }
}
namespace Autodesk.Revit.ApplicationServices {
 public class Application {
  public List<Document> Documents = new List<Document>();
  public Document OpenDocumentFile(string path) {
   if(path=="existing") return Documents[0];
   var doc=new Document {App=this}; Documents.Add(doc); return doc;
  }
  public Document NewFamilyDocument(string path) => OpenDocumentFile("new");
  public Document NewProjectDocument(string path) => OpenDocumentFile("new");
 }
}
namespace Autodesk.Revit.UI {
 public class UIDocument {public Document Document;}
 public class UIApplication {
  public Autodesk.Revit.ApplicationServices.Application Application = new Autodesk.Revit.ApplicationServices.Application();
  public UIDocument ActiveUIDocument;
 }
}
public class Probe {
 public static string Run(bool secondScope) {
  var app=new UIApplication();
  app.ActiveUIDocument=new UIDocument {Document=app.Application.OpenDocumentFile("new")};
  string result;
  using(RvtMcp.Plugin.McpDocument.Begin(app)) result=Script.Run(app);
  if(!secondScope)return result;
  using(RvtMcp.Plugin.McpDocument.Begin(app))
   return RvtMcp.Plugin.McpDocument.Close(app.Application.Documents[1],false).ToString();
 }
}
""";
}
