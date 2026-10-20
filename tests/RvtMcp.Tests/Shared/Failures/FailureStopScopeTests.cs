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

namespace RvtMcp.Tests
{
    public class FailureStopScopeTests
    {
        private static readonly Lazy<MethodInfo> Probe = new(() => CompileProbe());

        [Theory]
        [InlineData("open_model", "model_a", "model_b", "model_b", null, null, "REVIT_OPERATION_REQUIRES_USER", "model_b")]
        [InlineData("open_model", "model_a", "model_b", "model_b", null, null, "REVIT_MODEL_BLOCKED", "model_b")]
        [InlineData("open_model", "model_a", null, null, null, null, "FAILURE_CAPTURE_INCOMPLETE", null)]
        [InlineData("open_model", "model_a", "model_b|model_c", null, null, null, "REVIT_OPERATION_REQUIRES_USER", null)]
        [InlineData("open_model", null, "model_b", "model_b", null, null, "REVIT_OPERATION_REQUIRES_USER", "model_b")]
        [InlineData("send_code_to_revit", "model_a", null, null, null, null, "REVIT_OPERATION_REQUIRES_USER", "model_a")]
        [InlineData("open_model", "model_a", "model_b", null, "known_target", null, "REVIT_OPERATION_REQUIRES_USER", "known_target")]
        [InlineData("open_model", "model_a", "model_b", null, null, "state_target", "REVIT_OPERATION_REQUIRES_USER", "state_target")]
        public void ActualStopDoesNotTurnActiveOpenContextIntoAnExplicitFailureTarget(string tool, string active,
            string claims, string incident, string existing, string state, string code, string expected)
        {
            var result = (object[])Probe.Value.Invoke(null, new object[] { tool, active, claims, incident, existing, state, code })!;
            Assert.Equal(expected, result[0]); // Scope.Stop, before native Guard records its incident.
            Assert.Equal(expected, result[1]); // Receipt resolver, after that incident.
            Assert.True((bool)result[3]);
            Assert.Equal(code, result[4]);
            Assert.Null(result[2]); // Stopping this operation never consults a historical model gate.
            Assert.Empty((string[])result[5]);
        }

        private static MethodInfo CompileProbe([CallerFilePath] string testFile = "")
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", ".."));
            var source = File.ReadAllText(Path.Combine(root, "src", "shared", "Failures", "FailureControl.cs"));
            // Compile and execute the real Stop method. Only native state and ledger collaborators are doubles.
            var stop = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
                .OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "Stop").ToFullString();
            var probeSource = "using System; using System.Collections.Generic; using RvtMcp.Failures; public class StopProbe {"
                + stop + ProbeBody + "}";
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator).Append(typeof(FailureCapture).Assembly.Location).Distinct()
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("StopProbe_" + Guid.NewGuid().ToString("N"),
                new[] { CSharpSyntaxTree.ParseText(probeSource) }, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var output = new MemoryStream();
            var result = compilation.Emit(output);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            return Assembly.Load(output.ToArray()).GetType("StopProbe")!.GetMethod("Run")!;
        }

        private const string ProbeBody = """
public sealed class DocumentState { public FailureModel Model; }
public sealed class Gate { public List<string> IncidentIds = new List<string>(); }
public sealed class Ledger {
    public string LastModel;
    public Gate GetGate(string model, string operation) {
        LastModel=model;
        return new Gate { IncidentIds = new List<string> { "gate_" + model } };
    }
}
internal FailureOperation Operation = new FailureOperation();
internal bool Stopped;
internal string ErrorCode, FailureModelKey;
private readonly List<string> _gateIncidentIds = new List<string>();
private readonly Ledger Store = new Ledger();
internal void Attach(DocumentState state) {
    if (state != null && !Operation.ModelScopeKeys.Contains(state.Model.ModelScopeKey))
        Operation.ModelScopeKeys.Add(state.Model.ModelScopeKey);
}
private void StorageFailure() { throw new Exception("Unexpected gate lookup failure."); }
public static object[] Run(string tool, string active, string claims, string incident, string existing, string state, string code) {
    var scope = new StopProbe();
    scope.Operation.Tool=tool; scope.Operation.ActiveModelBefore=active;
    if(claims!=null)scope.Operation.ModelScopeKeys.AddRange(claims.Split('|'));
    scope.FailureModelKey=existing;
    scope.Stop(code, state==null ? null : new DocumentState { Model=new FailureModel { ModelScopeKey=state } });
    var receipt=FailureCapture.ReceiptModelScopeKey(scope.FailureModelKey,tool,active,scope.Operation.ModelScopeKeys,
        incident==null ? Array.Empty<string>() : new[]{incident});
    return new object[]{scope.FailureModelKey,receipt,scope.Store.LastModel,scope.Stopped,scope.ErrorCode,scope._gateIncidentIds.ToArray()};
}
""";
    }
}

