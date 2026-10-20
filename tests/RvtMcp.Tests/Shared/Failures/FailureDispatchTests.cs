using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using RvtMcp.Failures;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class FailureDispatchTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rvt-dispatch-tests-" + Guid.NewGuid().ToString("N"));
        private static readonly Lazy<MethodInfo> Probe = new(() => CompileProbe());
        private string Db => Path.Combine(_root, "ledger.db");

        [Theory]
        [InlineData("unknown", false)]
        [InlineData("partial", false)]
        [InlineData("rolled_back", false)]
        [InlineData("unknown", true)]
        [InlineData("partial", true)]
        [InlineData("rolled_back", true)]
        public void ActualStartAdmitsTheSameRequestWithOldIssuesAndRechecksOldStorageErrors(string outcome, bool oldStorageError)
        {
            var store = new FailureLedgerStore(Db);
            var model = Model(store);
            var old = new FailureOperation { Execution = "completed", Outcome = outcome, RequiresReview = outcome != "rolled_back",
                RequestHash = "same_request", ModelScopeKeys = new List<string> { model.ModelScopeKey } };
            Assert.True(store.SaveOperation(old).Recorded);
            var incident = new FailureIncident { ModelScopeKey = model.ModelScopeKey, OperationId = old.OperationId,
                RequestHash = old.RequestHash, Action = outcome == "rolled_back" ? "rolled_back" : "outcome_unknown" };
            Assert.True(store.RecordIncident(incident).Recorded);
            if (oldStorageError)
            {
                Directory.CreateDirectory(Path.Combine(_root, "pending"));
                File.WriteAllText(Path.Combine(_root, "pending", "old.json"), "unreadable old capture");
            }
            var result = (object[])Probe.Value.Invoke(null, new object[] { store, model, oldStorageError })!;
            Assert.False((bool)result[0]); Assert.Null(result[1]);
            Assert.True((bool)result[2]); Assert.True((bool)result[3]);
            var retained = Assert.Single(store.Query(new FailureRecordQuery { OperationId = old.OperationId }).Records);
            Assert.Equal(incident.IncidentId, retained.IncidentId); Assert.Equal("open", retained.State);
        }

        [Fact]
        public void ActualStartStillStopsTheCurrentOperationWhenCurrentPersistenceFails()
        {
            var store = new FailureLedgerStore(Db); var model = Model(store);
            using (var c = new SqliteConnection("Data Source=" + Db + ";Pooling=False"))
            {
                c.Open(); using var command = c.CreateCommand();
                command.CommandText = "CREATE TRIGGER fail_current BEFORE INSERT ON operations BEGIN SELECT RAISE(ABORT,'current write failed'); END;";
                command.ExecuteNonQuery();
            }
            File.WriteAllText(Path.Combine(_root, "pending"), "cannot save fallback");
            var result = (object[])Probe.Value.Invoke(null, new object[] { store, model, false })!;
            Assert.True((bool)result[0]); Assert.Equal("FAILURE_STORAGE_FAILED", result[1]);
            Assert.False((bool)result[2]); Assert.False((bool)result[3]);
        }

        [Fact]
        public void ActualStartDoesNotMistakeTheCurrentActivityFaultForAnOldStorageError()
        {
            var store = new FailureLedgerStore(Db); var model = Model(store);
            var result = (object[])Probe.Value.DeclaringType!.GetMethod("ActivityFault")!.Invoke(null, new object[] { store, model })!;
            Assert.True((bool)result[0]); Assert.Equal("FAILURE_STORAGE_FAILED", result[1]);
            Assert.False((bool)result[2]); Assert.False((bool)result[3]);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void ActualFailureReceiptKeepsIncidentsAndReviewStatusWithoutLockingLaterCommands(bool review, bool storageFailed)
        {
            var receipt = JObject.Parse((string)Probe.Value.DeclaringType!.GetMethod("Receipt")!.Invoke(null, new object[] { review, storageFailed })!);
            Assert.False(receipt.Value<bool>("success"));
            Assert.Equal("open", receipt["_failure"]!.Value<string>("gate"));
            Assert.Equal(review, receipt["_failure"]!.Value<bool>("requiresReview"));
            Assert.Equal("incident", Assert.Single((JArray)receipt["_failure"]!["incidentIds"]!).Value<string>());
            Assert.Single((JArray)receipt["_failure"]!["groups"]!);
            Assert.Equal(storageFailed ? "failed" : "recorded", receipt["_failure"]!.Value<string>("persistence"));
            Assert.Contains("Previous Issues do not block", receipt["_failure"]!.Value<string>("nextStep"));
            Assert.Equal(review, receipt["_operation"]!.Value<bool>("requiresReview"));
        }

        private static FailureModel Model(FailureLedgerStore store) => store.ResolveModel(new FailureModelIdentity {
            PhysicalKey = "physical", ModelKey = "model", Title = "Sample", HostInstanceId = "host", LiveDocumentId = "document" });

        private static MethodInfo CompileProbe([CallerFilePath] string testFile = "")
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", ".."));
            var source = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "src", "shared", "Failures", "FailureControl.cs"))).GetRoot();
            var names = new[] { "Start", "Reserve", "Attach", "ClaimModel", "StorageFailure", "Stop", "Enrich" };
            var methods = string.Join("\n", source.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(method => names.Contains(method.Identifier.ValueText)).Select(method => method.ToFullString()));
            // Execute the real admission chain against the real ledger; only Revit objects and baseline measurement are doubles.
            var text = "using System; using System.Collections.Generic; using System.Linq; using Newtonsoft.Json.Linq; using RvtMcp.Failures; using RvtMcp.Plugin; public class DispatchProbe {" + methods + ProbeBody + "}";
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Append(typeof(FailureCapture).Assembly.Location).Append(typeof(JObject).Assembly.Location).Distinct().Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("DispatchProbe_" + Guid.NewGuid().ToString("N"), new[] { CSharpSyntaxTree.ParseText(text) },
                references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var output = new MemoryStream(); var result = compilation.Emit(output);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            return Assembly.Load(output.ToArray()).GetType("DispatchProbe")!.GetMethod("Run")!;
        }

        private const string ProbeBody = """
public sealed class FakeDocument { public string PathName = "sample.rvt", Title = "Sample"; }
public sealed class FakeUiDocument { public FakeDocument Document = new FakeDocument(); }
public sealed class FakeApplication { public FakeUiDocument ActiveUIDocument = new FakeUiDocument(); }
public sealed class FakeRequest { public string CommandName = "send_code_to_revit"; }
public sealed class DocumentState { public FailureModel Model; }
private static class App { public static void DebugLog(string text) { } }
private static class UserDialogBuffer { public static string Key(string path, string title) => "context"; }
private FailureLedgerStore Store;
private readonly FakeApplication Application = new FakeApplication();
private readonly FakeRequest Request = new FakeRequest();
private readonly Dictionary<FakeDocument,DocumentState> Documents = new Dictionary<FakeDocument,DocumentState>();
private readonly HashSet<string> UncertainModels = new HashSet<string>();
private readonly FailureOperation Operation = new FailureOperation { Tool="send_code_to_revit", RequestHash="same_request" };
private readonly List<FailureIncident> Incidents = new List<FailureIncident>();
private const string HostInstanceId="host";
private sealed class DialogBuffer { public JObject Take(string key) => null; }
private readonly DialogBuffer UserDialogs = new DialogBuffer();
private void DescribeDialog(JObject group) { }
private DocumentState _state;
private string _storageError, _userDialogKey, ErrorCode, FailureModelKey;
private bool IsRead, _opensModel, Stopped, Persisted, StorageFailed, ExecutionStarted, _activeDocumentBeforeObserved, Indexed, ActivityFailed;
private DocumentState State(FakeDocument document, bool refresh=false) => _state;
private static string Revision(DocumentState state) => "revision";
private void IndexAndBaseline(DocumentState state) { Indexed=true; }
public static object[] Run(FailureLedgerStore store, FailureModel model, bool oldStorageError) {
    var scope=new DispatchProbe { Store=store, _state=new DocumentState { Model=model },
        _storageError=oldStorageError ? "FAILURE_STORAGE_FAILED" : null };
    scope.UncertainModels.Add(model.ModelScopeKey);
    using(store.BeginActivity(scope.Operation.OperationId)) scope.Start();
    return new object[]{scope.Stopped,scope.ErrorCode,scope.Indexed,store.GetOperation(scope.Operation.OperationId)!=null};
}
public static string Receipt(bool review,bool storageFailed) {
    var scope=new DispatchProbe { Stopped=true, StorageFailed=storageFailed,
        ErrorCode=storageFailed ? "FAILURE_STORAGE_FAILED" : "REVIT_FAILURE_ROLLED_BACK" };
    scope.Operation.Execution="completed"; scope.Operation.Outcome=review ? "unknown" : "rolled_back";
    scope.Operation.RequiresReview=review; scope.Operation.Complete=!review;
    scope.Operation.ModelScopeKeys.Add("model");
    var incident=new FailureIncident { IncidentId="incident", ModelScopeKey="model", Source="preprocessor", State="open",
        Action="rolled_back", Complete=!review, Messages={new FailureMessage { DefinitionId="warning", Severity="Warning", Description="Failure" }} };
    scope.Incidents.Add(incident); scope.Operation.IncidentIds.Add(incident.IncidentId);
    var envelope=new JObject { ["success"]=false, ["error"]=scope.ErrorCode };
    scope.Enrich(envelope); return envelope.ToString();
}
public static object[] ActivityFault(FailureLedgerStore store,FailureModel model) {
    var scope=new DispatchProbe { Store=store, _state=new DocumentState { Model=model }, ActivityFailed=true, _storageError="old_error" };
    scope.Start();
    return new object[]{scope.Stopped,scope.ErrorCode,scope.Indexed,store.GetOperation(scope.Operation.OperationId)!=null};
}
""";

        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
