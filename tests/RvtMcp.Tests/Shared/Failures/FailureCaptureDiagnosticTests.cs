using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RvtMcp.Failures;
using Xunit;

namespace RvtMcp.Tests;

public class FailureCaptureDiagnosticTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Zero_native_messages_either_continue_or_report_the_actual_capture_exception(bool invalidDocument)
    {
        var values = (object[])CompileProbe().Invoke(null, new object[] { invalidDocument })!;
        Assert.Equal(invalidDocument ? "ProceedWithRollBack" : "Continue", values[0]);
        if (!invalidDocument) { Assert.Null(values[1]); return; }
        Assert.Equal("FAILURE_CAPTURE_INCOMPLETE", values[1]);
        Assert.Contains("resolve_document", (string)values[2]);
        Assert.Contains("InvalidOperationException", (string)values[2]);
        Assert.Contains("native object no longer valid", (string)values[2]);
        Assert.False((bool)values[3]);
        Assert.Equal(0, values[4]); // Internal diagnostics never pretend to be Revit failure messages.
        Assert.True((bool)values[5]);
    }

    private static MethodInfo CompileProbe([CallerFilePath] string file = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "..", "..", ".."));
        var method = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "src/shared/Failures/FailureControl.cs")))
            .GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "Process");
        var attempt = method.Body!.Statements.OfType<TryStatementSyntax>().Single();
        // Keep the actual zero-message/identity path and actual catch. Nonempty-message policy
        // is outside this regression seam and covered by the policy tests.
        var prefix = attempt.Block.Statements.TakeWhile(statement =>
            !(statement is LocalDeclarationStatementSyntax local && local.Declaration.Variables.Any(v => v.Identifier.ValueText == "incident")));
        attempt = attempt.WithBlock(SyntaxFactory.Block(prefix.Append(SyntaxFactory.ParseStatement("return FailureProcessingResult.Continue;"))));
        method = method.WithBody(method.Body.ReplaceNode(method.Body.Statements.OfType<TryStatementSyntax>().Single(), attempt));
        var text = "using System; using System.Collections.Generic; using RvtMcp.Failures; public class Probe {" + method + Body + "}";
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(FailureCapture).Assembly.Location).Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("CaptureProbe_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(text) }, refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var emit = compilation.Emit(output);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        return Assembly.Load(output.ToArray()).GetType("Probe")!.GetMethod("Run")!;
    }
    private const string Body = """
public class DocumentState {public FailureModel Model = new FailureModel {ModelScopeKey="model"};}
public class FailuresAccessor {
 public List<int> GetFailureMessages()=>new List<int>();
 public object GetDocument()=>new object();
}
public enum FailureProcessingResult {Continue,ProceedWithRollBack}
class RequestInfo {public string CommandName="send_code_to_revit";}
static class App {public static void DebugLog(string message) {}}
bool Invalid,Stopped; string ErrorCode,OpenPath;
FailureOperation Operation=new FailureOperation();
RequestInfo Request=new RequestInfo();
HashSet<string> UncertainModels=new HashSet<string>();
FailureIncident Incident;
DocumentState State(object doc) => Invalid ? throw new InvalidOperationException("native object no longer valid") : new DocumentState();
void Attach(DocumentState state) {}
FailureProcessingResult Rollback(FailuresAccessor accessor)=>FailureProcessingResult.ProceedWithRollBack;
void Stop(string code,DocumentState state) {Stopped=true;ErrorCode=code;}
void StorageFailure() {ErrorCode="FAILURE_STORAGE_FAILED";}
FailureIncident NewIncident(DocumentState state,string source,string title,string action) => new FailureIncident {Source=source,Action=action,TransactionName=title};
void Record(FailureIncident incident) {Incident=incident;}
public static object[] Run(bool invalid) {
 var probe=new Probe {Invalid=invalid};
 var result=probe.Process(new FailuresAccessor(),"preprocessor");
 return new object[]{result.ToString(),probe.ErrorCode,probe.Incident?.NativeContextDescription,
 probe.Incident?.Complete ?? true,probe.Incident?.MessageCount ?? 0,probe.Operation.RequiresReview};
}
""";
}
