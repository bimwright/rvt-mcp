using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace RvtMcp.Tests;

public class FailureDocumentLifetimeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Native_close_cleanup_never_hashes_a_dead_document_and_preserves_live_wrappers(bool succeeded)
    {
        var result = (object[])CompileProbe().Invoke(null, new object[] { succeeded })!;
        Assert.Null(result[0]);
        Assert.Equal(succeeded ? 0 : 1, result[1]);
        Assert.Equal(succeeded ? 0 : 1, result[2]);
        Assert.Equal(succeeded ? 0 : 1, result[3]);
        Assert.True((bool)result[4]);
    }

    private static MethodInfo CompileProbe([CallerFilePath] string file = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "..", "..", ".."));
        var names = new[] { "OnDocumentClosed", "OnDocumentClosing", "PruneClosedDocuments", "PruneDocuments", "IsLiveDocument" };
        var methods = string.Join("\n", new[] { "FailureControl.cs", "FailureNativeEvents.cs" }
            .SelectMany(name => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "src/shared/Failures", name)))
                .GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            .Where(method => names.Contains(method.Identifier.ValueText)).Select(method => method.ToFullString()));
        var text = "using System; using System.Linq; using System.Collections.Generic; public class Probe {" + methods + Body + "}";
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("Lifetime_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(text) }, refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var emit = compilation.Emit(output);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        return Assembly.Load(output.ToArray()).GetType("Probe")!.GetMethod("Run")!;
    }

    private const string Body = """
public class Life { public bool Valid = true; }
public class Document {
 public Life Life;
 public bool IsModified;
 public bool IsValidObject => Life.Valid;
 public override int GetHashCode() => IsValidObject ? 1 : throw new InvalidOperationException("dead_document_hash");
 public override bool Equals(object obj) {
  if (!IsValidObject) throw new InvalidOperationException("dead_document_equals");
  return obj is Document other && ReferenceEquals(Life, other.Life);
 }
}
public class Model { public string ModelScopeKey="model"; }
public class DocumentState { public Document Document; public Model Model = new(); }
public enum RevitAPIEventStatus { Succeeded, Cancelled }
public class DocumentClosedEventArgs { public int DocumentId; public RevitAPIEventStatus Status; }
public class DocumentClosingEventArgs { public int DocumentId; public Document Document; public bool Cancelled; }
static Dictionary<Document,DocumentState> Documents = new();
static Dictionary<Document,string> Saving = new(), SyncAuthorities = new();
static Dictionary<int,DocumentState> Closing = new();
static bool InScope, Stopped, Authorized;
static int StateCalls;
static bool Scoped() => InScope;
static class McpDocument {public static bool IsDiscardAuthorized(Document doc)=>Authorized;}
static DocumentState State(Document doc) {StateCalls++;return new DocumentState {Document=doc};}
static void DenyIncomplete(DocumentClosingEventArgs args,DocumentState state,string action,string description) {args.Cancelled=true;}
static bool Guard(DocumentClosingEventArgs args,DocumentState state,Model model,string action,string hash,string revision,string description,bool grant,bool complete) {
 args.Cancelled=Stopped;return !Stopped;
}
static string Hash(string value)=>value;
static string Revision(DocumentState state)=>"revision";
static void Invalidate(DocumentState state) {}
static void ObserveCancelledOrFailed(RevitAPIEventStatus status,string action,Document doc) {}
public static object[] ClosingProbe(bool authorized,bool modified,bool stopped) {
 InScope=true;Stopped=stopped;Authorized=authorized;
 var doc=new Document {Life=new Life(),IsModified=modified};
 Documents.Add(doc,new DocumentState {Document=doc});
 var args=new DocumentClosingEventArgs {DocumentId=1,Document=doc};
 OnDocumentClosing(null,args);
 return new object[]{args.Cancelled,StateCalls};
}
public static object[] Run(bool succeeded) {
 var life = new Life(); var doc = new Document { Life=life }; var state = new DocumentState { Document=doc };
 Documents.Add(doc,state); Saving.Add(doc,"save"); SyncAuthorities.Add(doc,"sync"); Closing.Add(1,state);
 life.Valid = !succeeded;
 string error=null;
 try { OnDocumentClosed(null,new DocumentClosedEventArgs { DocumentId=1, Status=succeeded?RevitAPIEventStatus.Succeeded:RevitAPIEventStatus.Cancelled }); }
 catch(Exception ex) { error=ex.Message; }
 var newLife = new Life(); var next = new Document { Life=newLife };
 bool equivalent=false;
 try {
  Documents.Add(next,new DocumentState { Document=next });
  equivalent=Documents.ContainsKey(new Document { Life=newLife });
  Documents.Remove(next);
 } catch(Exception ex) { error ??= ex.Message; }
 return new object[]{error,Documents.Count,Saving.Count,SyncAuthorities.Count,equivalent};
}
""";
}
