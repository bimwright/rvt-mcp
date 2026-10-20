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

namespace RvtMcp.Tests;

// The actual failure callback, recording and receipt methods run against a real disposable
// ledger. Only Revit's native failure/document APIs are doubled; this is not live Revit acceptance.
public class WarningPolicyProcessingTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rvt-warning-processing-" + Guid.NewGuid().ToString("N"));
    private static readonly Lazy<MethodInfo> Run = new(() => Compile());
    private string Db => Path.Combine(_folder, "ledger.db");

    [Theory]
    [InlineData("accept-and-log", "Warning", false, "Continue", "accepted_by_policy")]
    [InlineData("rollback", "Warning", false, "ProceedWithRollBack", "open")]
    [InlineData("accept-and-log", "Error", false, "ProceedWithRollBack", "open")]
    [InlineData("accept-and-log", "DocumentCorruption", false, "ProceedWithRollBack", "open")]
    [InlineData("accept-and-log", "Warning", true, "ProceedWithRollBack", "open")]
    public void Actual_callback_records_the_selected_mode_and_only_allows_complete_warnings(
        string policy, string severity, bool incomplete, string expectedResult, string expectedState)
    {
        var store = new FailureLedgerStore(Db);
        var model = store.ResolveModel(new FailureModelIdentity { PhysicalKey = "fixture", ModelKey = "fixture", Title = "Fixture model" });
        var values = (object[])Run.Value.Invoke(null, new object[] { store, model, policy, severity, incomplete })!;
        Assert.Equal(expectedResult, values[0]);
        Assert.Equal(expectedResult == "Continue", values[1]);
        var incident = Assert.Single(store.Query(new FailureRecordQuery { OperationId = (string)values[2], IncludeDetails = true }).Records);
        Assert.Equal(expectedState, incident.State);
        Assert.Equal(policy, incident.WarningPolicy);
        Assert.Equal(model.ModelScopeKey, incident.ModelScopeKey);
        Assert.Equal(2027, incident.RevitYear);
        Assert.Equal("revision", incident.DocumentRevision);
        Assert.Equal("Fixture warning", Assert.Single(incident.Messages).Description);
        Assert.Equal(new long[] { 11 }, incident.Messages[0].FailingElementIds);
        var receipt = JObject.Parse((string)values[3]);
        Assert.Equal(policy, receipt["_failure"]!.Value<string>("warningPolicy"));
        Assert.Equal(policy, receipt["_operation"]!.Value<string>("warningPolicy"));
        Assert.Equal("open", receipt["_failure"]!.Value<string>("gate"));
        Assert.Single((JArray)receipt["_failure"]!["incidentIds"]!);
        if (expectedResult == "Continue") Assert.Contains("accept-and-log", receipt["_failure"]!.Value<string>("nextStep"));
    }

    [Fact]
    public void A_current_incident_write_failure_still_rolls_back_before_acknowledging_any_warning()
    {
        var store = new FailureLedgerStore(Db);
        var model = store.ResolveModel(new FailureModelIdentity { PhysicalKey = "fixture", ModelKey = "fixture", Title = "Fixture model" });
        using (var c = new SqliteConnection("Data Source=" + Db + ";Pooling=False"))
        {
            c.Open(); using var command = c.CreateCommand();
            command.CommandText = "CREATE TRIGGER deny_incident BEFORE INSERT ON incidents BEGIN SELECT RAISE(ABORT,'fixture write failure'); END;";
            command.ExecuteNonQuery();
        }
        var values = (object[])Run.Value.Invoke(null, new object[] { store, model, "accept-and-log", "Warning", false })!;
        Assert.Equal("ProceedWithRollBack", values[0]);
        Assert.False((bool)values[1]);
        Assert.Equal("failed", JObject.Parse((string)values[3])["_failure"]!.Value<string>("persistence"));
    }

    [Fact]
    public void A_rule_accepted_dialog_is_not_misattributed_to_the_warning_opt_in()
    {
        var method = Run.Value.DeclaringType!.GetMethod("ReceiptForAcceptedDialog")!;
        var receipt = JObject.Parse((string)method.Invoke(null, null)!);
        Assert.Equal("accept-and-log", receipt["_failure"]!.Value<string>("warningPolicy"));
        var nextStep = receipt["_failure"]!.Value<string>("nextStep");
        Assert.DoesNotContain("accept-and-log", nextStep);
        Assert.Contains("rule", nextStep);
    }

    private static MethodInfo Compile([CallerFilePath] string file = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "..", "..", ".."));
        var names = new[] { "Process", "CaptureIds", "Rollback", "NewIncident", "Record", "SaveOperation", "StorageFailure", "Stop", "Enrich", "Bounded" };
        var methods = string.Join("\n", CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "src/shared/Failures/FailureControl.cs")))
            .GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => names.Contains(m.Identifier.ValueText)).Select(m => m.ToFullString()));
        var text = "using System; using System.Collections.Generic; using System.Linq; using System.Text; using Newtonsoft.Json.Linq; using RvtMcp.Failures; using RvtMcp.Plugin; public class Probe {" + methods + Body + "}";
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(FailureLedgerStore).Assembly.Location).Append(typeof(JObject).Assembly.Location).Distinct().Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("WarningProcessing_" + Guid.NewGuid().ToString("N"), new[] { CSharpSyntaxTree.ParseText(text) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream(); var emit = compilation.Emit(output);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        return Assembly.Load(output.ToArray()).GetType("Probe")!.GetMethod("Run")!;
    }

    private const string Body = """
public enum FailureProcessingResult {Continue,ProceedWithRollBack}
public enum FailureSeverity {Warning,Error,DocumentCorruption}
public sealed class ElementId {public long Value=11;}
static class RevitCompat {public static long GetId(ElementId id)=>id.Value; public static ElementId ToElementId(long id)=>new ElementId {Value=id};}
public class XYZ {public double X,Y,Z;}
public class LocationPoint {public XYZ Point;}
public class Named {public string Name;}
public class Element {public object Location; public Named Category; public ElementId LevelId; public ElementId GetTypeId()=>null; public string Name;}
public class Document {public Element GetElement(ElementId id)=>null;}
public class DocumentState {public FailureModel Model;public Document Document=new Document();}
public class FailureDefinitionId {public Guid Guid=Guid.Parse("cf531a66-c854-4e14-bb9b-fd5b6a48e5c9");}
public class NativeFailure {
 public FailureSeverity Severity;
 public FailureSeverity GetSeverity()=>Severity;
 public FailureDefinitionId GetFailureDefinitionId()=>new FailureDefinitionId();
 public string GetDescriptionText()=>"Fixture warning";
 public List<ElementId> GetFailingElementIds()=>new List<ElementId> {new ElementId()};
 public List<ElementId> GetAdditionalElementIds()=>new List<ElementId>();
}
public class FailureHandlingOptions {public FailureHandlingOptions SetClearAfterRollback(bool clear)=>this;}
public class FailuresAccessor {
 public NativeFailure Failure;public bool Deleted;
 public List<NativeFailure> GetFailureMessages()=>new List<NativeFailure> {Failure};
 public Document GetDocument()=>new Document();
 public string GetTransactionName()=>"Fixture transaction";
 public FailureSeverity GetSeverity()=>Failure.Severity;
 public void DeleteWarning(NativeFailure failure){Deleted=true;}
 public FailureHandlingOptions GetFailureHandlingOptions()=>new FailureHandlingOptions();
 public void SetFailureHandlingOptions(FailureHandlingOptions options){}
}
class RequestInfo {public string CommandName="create_wall";}
static class App {public static void DebugLog(string message){}}
class DialogBuffer {public JObject Take(string key)=>null;}
FailureLedgerStore Store;
FailureOperation Operation=new FailureOperation {Tool="create_wall",RequestHash="request",DocumentRevision="revision"};
DocumentState _state;
readonly List<FailureIncident> Incidents=new List<FailureIncident>();
readonly Dictionary<string,HashSet<long>> _before=new Dictionary<string,HashSet<long>>();
readonly HashSet<string> _completeBefore=new HashSet<string>(),_snapshotKeys=new HashSet<string>(),UncertainModels=new HashSet<string>();
readonly RequestInfo Request=new RequestInfo();
readonly DialogBuffer UserDialogs=new DialogBuffer();
int _capturedMessages,_capturedReferences,_capturedBytes,_cycle;
const int Year=2027; const string HostInstanceId="fixture-host";
bool Stopped,StorageFailed,Persisted,IsRead;
string OpenPath,ErrorCode,FailureModelKey,_userDialogKey;
DocumentState State(Document doc)=>_state;
void Attach(DocumentState state){}
static string Revision(DocumentState state)=>"revision";
void DescribeDialog(JObject group){}
public static string ReceiptForAcceptedDialog() {
 var probe=new Probe();
 probe.Operation.WarningPolicy="accept-and-log";
 probe.Operation.Outcome="committed";
 probe.Incidents.Add(new FailureIncident {Source="dialog",State="accepted_by_rule"});
 var receipt=new JObject();probe.Enrich(receipt);return receipt.ToString();
}
public static object[] Run(FailureLedgerStore store,FailureModel model,string policy,string severity,bool incomplete) {
 var probe=new Probe {Store=store,_state=new DocumentState {Model=model}};
 probe.Operation.WarningPolicy=policy;
 probe.Operation.ModelScopeKeys.Add(model.ModelScopeKey);
 probe._before[model.ModelScopeKey]=new HashSet<long> {11};
 if(!incomplete)probe._completeBefore.Add(model.ModelScopeKey);
 var accessor=new FailuresAccessor {Failure=new NativeFailure {Severity=(FailureSeverity)Enum.Parse(typeof(FailureSeverity),severity)}};
 var result=probe.Process(accessor,"preprocessor");
 probe.Operation.Execution="completed";probe.Operation.Outcome=result==FailureProcessingResult.Continue?"committed":"rolled_back";
 probe.Operation.MutationApplied=result==FailureProcessingResult.Continue;
 var receipt=new JObject { ["success"]=result==FailureProcessingResult.Continue };
 probe.Enrich(receipt);
 return new object[]{result.ToString(),accessor.Deleted,probe.Operation.OperationId,receipt.ToString()};
}
""";
    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
}
