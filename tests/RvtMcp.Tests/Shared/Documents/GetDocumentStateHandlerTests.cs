using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Documents;
using Xunit;

namespace RvtMcp.Tests.Shared.Documents;

public class GetDocumentStateHandlerTests
{
    private static readonly Lazy<MethodInfo> Probe = new(Compile);
    private readonly string root = Path.Combine(Path.GetTempPath(), "rvt-document-state-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Scope_all_omits_loaded_links_and_keeps_open_documents()
    {
        var result = Run("all");
        Assert.True(result.Value<bool>("success"), result.ToString());
        Assert.Equal(new[] { "Host", "Busy", "Other" }, result["data"]["documents"].Select(d => d.Value<string>("title")));
        Assert.Equal("local_file", result["data"]["policy"].Value<string>("mode"));
    }

    [Fact]
    public void A_failed_document_keeps_an_error_item_without_losing_other_documents()
    {
        var result = Run("broken");
        Assert.True(result.Value<bool>("success"), result.ToString());
        var documents = (JArray)result["data"]["documents"];
        Assert.Equal(3, documents.Count);
        Assert.Equal("Host", documents[0].Value<string>("title"));
        Assert.Equal("Broken", documents[1].Value<string>("title"));
        Assert.Equal("DOCUMENT_STATE_UNAVAILABLE", documents[1].Value<string>("error"));
        Assert.False(documents[1].Value<bool>("is_active"));
        Assert.DoesNotContain("private failure details", result.ToString());
        Assert.Equal("Other", documents[2].Value<string>("title"));
    }

    [Fact]
    public void Active_document_failure_is_reported_as_one_error_item()
    {
        var result = Run("active-broken");
        Assert.True(result.Value<bool>("success"), result.ToString());
        var document = Assert.Single((JArray)result["data"]["documents"]);
        Assert.Equal("Broken", document.Value<string>("title"));
        Assert.Equal("DOCUMENT_STATE_UNAVAILABLE", document.Value<string>("error"));
        Assert.True(document.Value<bool>("is_active"));
        Assert.Equal(JTokenType.Null, result["data"]["policy"].Type);
    }

    [Fact]
    public void Failure_before_active_identity_is_known_is_reported_as_unknown()
    {
        var result = Run("link-status-error");
        Assert.True(result.Value<bool>("success"), result.ToString());
        var documents = (JArray)result["data"]["documents"];
        var failed = Assert.Single(documents, d => d.Value<string>("title") == "Linked");
        Assert.Equal("DOCUMENT_STATE_UNAVAILABLE", failed.Value<string>("error"));
        Assert.Equal(JTokenType.Null, failed["is_active"].Type);
        Assert.Contains(documents, d => d.Value<string>("title") == "Other");
        Assert.DoesNotContain("private failure details", result.ToString());
    }

    [Fact]
    public async Task Busy_index_is_local_to_its_document_in_all_and_active_scopes()
    {
        // Both the real index implementation in this test and the compiled handler
        // use the same named mutex. Dedicated threads keep waits off the thread pool.
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var indexPath = Path.Combine(root, "Busy.checkpoints", "index.json");
        var holder = StartWorker(() => CheckpointIndex.Update(indexPath, "Busy.rvt", _ =>
        {
            entered.SetResult(true);
            release.Task.GetAwaiter().GetResult();
            return false;
        }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var all = StartWorker(() => Run("all"));
            var active = StartWorker(() => Run("active-busy"));
            await Task.WhenAll(all, active).WaitAsync(TimeSpan.FromSeconds(30));

            var allResult = await all;
            Assert.True(allResult.Value<bool>("success"), allResult.ToString());
            var documents = (JArray)allResult["data"]["documents"];
            Assert.Equal(new[] { "Host", "Busy", "Other" }, documents.Select(d => d.Value<string>("title")));
            Assert.Equal("not_created", documents[0]["checkpoints"].Value<string>("index_status"));
            Assert.Equal("busy", documents[1]["checkpoints"].Value<string>("index_status"));
            Assert.Equal(JTokenType.Null, documents[1]["checkpoints"]["count"].Type);
            Assert.Equal("not_created", documents[2]["checkpoints"].Value<string>("index_status"));
            Assert.Equal("local_file", allResult["data"]["policy"].Value<string>("mode"));

            var activeResult = await active;
            Assert.True(activeResult.Value<bool>("success"), activeResult.ToString());
            var activeDocument = Assert.Single((JArray)activeResult["data"]["documents"]);
            Assert.Equal("busy", activeDocument["checkpoints"].Value<string>("index_status"));
            Assert.Equal("local_file", activeResult["data"]["policy"].Value<string>("mode"));
        }
        finally
        {
            release.TrySetResult(true);
            await holder.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private JObject Run(string scenario) => JObject.Parse((string)Probe.Value.Invoke(null, new object[] { scenario, root }));

    private static Task<T> StartWorker<T>(Func<T> action) => Task.Factory.StartNew(action,
        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static MethodInfo Compile() => CompileAt();
    private static MethodInfo CompileAt([CallerFilePath] string testFile = "")
    {
        var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile), "..", "..", "..", ".."));
        var files = new[]
        {
            "src/shared/Handlers/Documents/GetDocumentStateHandler.cs",
            "src/shared/Documents/CheckpointIndex.cs",
            "src/shared/Documents/CheckpointPlanner.cs",
            "src/shared/Documents/DocumentPolicy.cs"
        };
        var sources = files.Select(file => File.ReadAllText(Path.Combine(repo, file))).Append(Doubles);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            .Split(Path.PathSeparator).Append(typeof(JObject).Assembly.Location).Distinct()
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("DocumentStateProbe_" + Guid.NewGuid().ToString("N"),
            sources.Select(source => CSharpSyntaxTree.ParseText(source)), references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var emitted = compilation.Emit(output);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return Assembly.Load(output.ToArray()).GetType("DocumentStateProbe").GetMethod("Run");
    }

    // Compile and execute the production handler and index with a minimal Revit boundary.
    private const string Doubles = """
using System;
using System.IO;
using System.Collections.Generic;
using Newtonsoft.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
namespace Autodesk.Revit.DB {
 public class DocumentVersion { public int NumberOfSaves = 1; public Guid VersionGUID = Guid.Empty; }
 public class Document {
  public string Title, PathName; public bool IsFamilyDocument, IsModified, FailState, FailLink;
  private bool linked;
  public bool IsLinked { get { if (FailLink) throw new InvalidOperationException("private failure details"); return linked; } set { linked=value; } }
  public static DocumentVersion GetDocumentVersion(Document doc) => new DocumentVersion();
 }
}
namespace Autodesk.Revit.UI {
 public class UIDocument { public Document Document; }
 public class Application { public List<Document> Documents = new List<Document>(); }
 public class UIApplication { public UIDocument ActiveUIDocument; public Application Application = new Application(); }
}
namespace RvtMcp.Plugin {
 public interface IRevitCommand { }
 public class CommandResult {
  public bool Success; public object Data; public string Error;
  public static CommandResult Ok(object data) => new CommandResult { Success=true, Data=data };
  public static CommandResult Fail(string error) => new CommandResult { Error=error };
 }
 public static class DocumentTargetGuard { public static string GetOrCreateToken(Document doc) => doc.Title; }
}
namespace RvtMcp.Plugin.Documents {
 public class BackupLimitRecord { public int MinBackups; public string EnsuredBy; }
 public class BackupLimitRegistry { public BackupLimitRecord Get(string path) => null; }
 public static class BackupFileCounter { public static int Count(string path) => 0; }
 public static class CheckpointSpace { public static long AvailableBytes(string path) => 1; }
}
namespace RvtMcp.Plugin.Handlers {
 internal static class DocumentLifecycleSupport {
  internal class Context { internal string Path, Mode, ModeError, CentralPath; }
  internal class Versions { internal int MemorySaves, DiskSaves; internal Guid MemoryGuid, DiskGuid; internal string Status, SavedInVersion; internal bool? UpgradeOnSave; }
  internal static Context Capture(Document doc) {
   if (doc.FailState) throw new InvalidOperationException("private failure details");
   return new Context { Path=doc.PathName, Mode="local_file" };
  }
  internal static int HostYear(UIApplication app) => 2026;
  internal static Versions ReadVersions(Document doc, string path, int year) => new Versions();
  internal static string CheckpointDirectory(string path) => Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + ".checkpoints");
  internal static string IndexPath(string path) => Path.Combine(CheckpointDirectory(path), "index.json");
 }
}
public static class DocumentStateProbe {
 public static string Run(string scenario, string root) {
  Document D(string name) => new Document { Title=name, PathName=Path.Combine(root, name + ".rvt") };
  var host=D("Host"); var busy=D("Busy"); var other=D("Other");
  var link=D("Linked"); link.IsLinked=true; link.FailLink=scenario == "link-status-error";
  var broken=D("Broken"); broken.FailState=true;
  var app=new UIApplication();
  app.Application.Documents.Add(host);
  if (scenario == "broken" || scenario == "active-broken") app.Application.Documents.Add(broken);
  else app.Application.Documents.Add(busy);
  app.Application.Documents.Add(link); app.Application.Documents.Add(other);
  var selected=scenario == "active-busy" ? busy : scenario == "active-broken" ? broken : host;
  app.ActiveUIDocument=new UIDocument { Document=selected };
  var scope=scenario.StartsWith("active-") ? "active" : "all";
  var result=new RvtMcp.Plugin.Handlers.GetDocumentStateHandler().Execute(app, "{\"scope\":\"" + scope + "\"}");
  return JsonConvert.SerializeObject(new { success=result.Success, data=result.Data, error=result.Error });
 }
}
""";
}
