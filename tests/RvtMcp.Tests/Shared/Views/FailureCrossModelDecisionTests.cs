using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RvtMcp.Failures;
using RvtMcp.Plugin.Views;
using Xunit;

namespace RvtMcp.Tests;

public class FailureCrossModelDecisionTests
{
    private static readonly Lazy<MethodInfo> Apply = new(() => CompileFacade());

    [Theory]
    [InlineData("rework")]
    [InlineData("abandon")]
    [InlineData("accept_once")]
    [InlineData("create_rule")]
    [InlineData("native_grant")]
    public void All_five_actions_persist_for_the_selected_issue_from_another_project_or_without_a_document(string action)
    {
        foreach (var context in new[] {
            new FailureIssuesContext { IsAvailable=true, ModelScopeKey="unrelated", RevitYear=2022, DocumentRevision="unrelated-revision" },
            new FailureIssuesContext(), null })
        {
            var folder = Path.Combine(Path.GetTempPath(), "rvt-review-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FailureLedgerStore(Path.Combine(folder, "ledger.db"));
                var model = store.ResolveModel(new FailureModelIdentity {
                    ModelKey="selected", PhysicalKey="physical", Title="Selected model",
                    LiveDocumentId="document", HostInstanceId="original-host" });
                var operation = new FailureOperation {
                    Execution="completed", Outcome="rolled_back", RequestHash="request",
                    DocumentRevision="recorded-revision", ModelScopeKeys=new List<string> {model.ModelScopeKey} };
                Assert.True(store.SaveOperation(operation).Recorded);
                var incident = new FailureIncident {
                    OperationId=operation.OperationId, ModelScopeKey=model.ModelScopeKey, RevitYear=2027,
                    RequestHash=operation.RequestHash, DocumentRevision=operation.DocumentRevision, OperationContext="edit",
                    MessageCount=1, Messages=new List<FailureMessage> {
                        new FailureMessage {DefinitionId=Guid.NewGuid().ToString(),Severity="Warning"} } };
                if (action == "native_grant")
                {
                    incident.NativeAction="save_as"; incident.NativeContextHash="native-context";
                    incident.NativeContextDescription="Save selected model to its reviewed destination.";
                }
                Assert.True(store.RecordIncident(incident).Recorded);
                var decision = action == "native_grant"
                    ? FailureIssuesPresentation.CreateNativeDecision(incident,operation,context,"Reviewed",false)
                    : FailureIssuesPresentation.CreateDecision(action,incident,operation,context,"Reviewed",false);
                Assert.Equal(model.ModelScopeKey,decision.ModelScopeKey);
                Assert.Equal("recorded-revision",decision.DocumentRevision);
                Assert.All(decision.Rules.Cast<FailureRule>().Concat(decision.Grants), rule => {
                    Assert.Equal(model.ModelScopeKey,rule.ModelScopeKey); Assert.Equal(2027,rule.RevitYear);
                });
                var result=(FailurePersistenceResult)Apply.Value.Invoke(null,new object[] {store,decision})!;
                Assert.True(result.Recorded,result.ErrorCode);
                Assert.True(result.Durable);
                if(action=="native_grant")
                {
                    Assert.NotNull(FailurePolicy.MatchNativeGrant(store.GetGrants(model.ModelScopeKey),model.ModelScopeKey,2027,
                        "save_as","native-context","request","recorded-revision"));
                    Assert.Null(FailurePolicy.MatchNativeGrant(store.GetGrants(model.ModelScopeKey),"unrelated",2022,
                        "save_as","native-context","request","unrelated-revision"));
                }
            }
            finally { if(Directory.Exists(folder))Directory.Delete(folder,true); }
        }
    }

    private static MethodInfo CompileFacade([CallerFilePath] string file = "")
    {
        var root=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!,"..","..","..",".."));
        var method=CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root,"src/shared/Failures/FailureControl.cs")))
            .GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="ApplyUiDecision");
        var source="using System; using System.Linq; using System.Collections.Generic; using RvtMcp.Failures; public class Probe {"
            + method + """
static FailureLedgerStore Store;
static object Active=null;
const string HostInstanceId="review-host";
static string _revitUser="reviewer";
static int Year=2022;
class DocumentState {public FailureModel Model; public string Revision;}
static Dictionary<string,DocumentState> Documents=new();
static HashSet<string> UncertainModels=new();
static void PruneClosedDocuments() {}
static string Revision(DocumentState state)=>state.Revision;
public static FailurePersistenceResult Run(FailureLedgerStore store,FailureDecision decision) { Store=store;return ApplyUiDecision(decision); }
}
""";
        var refs=((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(FailureLedgerStore).Assembly.Location).Select(path=>MetadataReference.CreateFromFile(path));
        var compilation=CSharpCompilation.Create("ReviewFacade_"+Guid.NewGuid().ToString("N"),
            new[]{CSharpSyntaxTree.ParseText(source)},refs,new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output=new MemoryStream();
        var emit=compilation.Emit(output);
        Assert.True(emit.Success,string.Join("\n",emit.Diagnostics));
        return Assembly.Load(output.ToArray()).GetType("Probe")!.GetMethod("Run")!;
    }
}
