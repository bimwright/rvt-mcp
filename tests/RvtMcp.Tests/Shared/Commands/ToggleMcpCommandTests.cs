using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests;

[Collection("ServerStateConfig")]
public sealed class ToggleMcpCommandTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "rvtmcp-toggle-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly string _previousPath = RvtMcpConfig.ConfigFilePathOverride;

    public ToggleMcpCommandTests() => RvtMcpConfig.ConfigFilePathOverride = _path;

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Successful_ribbon_toggle_changes_transport_and_is_remembered(bool before, bool after)
    {
        Assert.True(RvtMcpConfig.TrySaveAutoStartTransport(before, out var error, _path), error);
        var result = Run(before);
        Assert.Equal("Succeeded", result.Value<string>("status"));
        Assert.Equal(after, result.Value<bool>("running"));
        Assert.Equal(after, result.Value<bool>("preference"));
        Assert.Equal(after, RvtMcpConfig.LoadReadOnly(_path, _ => null).AutoStartTransportOrDefault);
    }

    [Fact]
    public void Failed_transport_start_keeps_the_previous_saved_off_state()
    {
        Assert.True(RvtMcpConfig.TrySaveAutoStartTransport(false, out var error, _path), error);
        var before = File.ReadAllBytes(_path);
        var result = Run(false, failStart: true);
        Assert.Equal("transport_start_failed", result.Value<string>("exception"));
        Assert.Equal(before, File.ReadAllBytes(_path));
        Assert.False(RvtMcpConfig.LoadReadOnly(_path, _ => null).AutoStartTransportOrDefault);
    }

    [Fact]
    public void Save_failure_is_reported_after_the_session_toggle_without_overwriting_config()
    {
        const string malformed = "{broken";
        File.WriteAllText(_path, malformed);
        var result = Run(true);
        Assert.Equal("Failed", result.Value<string>("status"));
        Assert.False(result.Value<bool>("running"));
        Assert.False(string.IsNullOrWhiteSpace(result.Value<string>("message")));
        Assert.Equal(malformed, File.ReadAllText(_path));
    }

    private JObject Run(bool running, bool failStart = false) =>
        JObject.Parse((string)Probe.Value.Invoke(null, new object[] { running, failStart })!);

    private static readonly Lazy<MethodInfo> Probe = new Lazy<MethodInfo>(() => CompileProbe());

    private static MethodInfo CompileProbe([CallerFilePath] string testFile = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", ".."));
        var source = File.ReadAllText(Path.Combine(root, "src", "shared", "Commands", "ToggleMcpCommand.cs"));
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Append(typeof(JObject).Assembly.Location)
            .Append(typeof(RvtMcpConfig).Assembly.Location).Distinct()
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("McpToggleProbe_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(source), CSharpSyntaxTree.ParseText(Api) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var emitted = compilation.Emit(output);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return Assembly.Load(output.ToArray()).GetType("McpToggleProbe")!.GetMethod("Run")!;
    }

    // Only the Revit command boundary and native transport are doubled. The ribbon
    // command, config writer and subsequent config load use production code.
    private const string Api = """
using System;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
namespace Autodesk.Revit.Attributes {
 public class TransactionAttribute : Attribute { public TransactionAttribute(Autodesk.Revit.DB.TransactionMode mode) {} }
}
namespace Autodesk.Revit.DB { public enum TransactionMode { Manual } public class ElementSet {} }
namespace Autodesk.Revit.UI {
 public enum Result { Failed, Succeeded }
 public class ExternalCommandData {}
 public interface IExternalCommand { Result Execute(ExternalCommandData data, ref string message, Autodesk.Revit.DB.ElementSet elements); }
}
namespace RvtMcp.Plugin.Localization { public static class L { public static string T(string key) => "Preference could not be saved; this session is still using the new state."; } }
namespace RvtMcp.Plugin {
 public class App {
  public static App Instance;
  public bool IsTransportRunning;
  public bool FailStart;
  public RvtMcpConfig Config;
  public void StopTransport() { IsTransportRunning = false; }
  public void CreateAndStartTransport() {
   StopTransport();
   if (FailStart) throw new InvalidOperationException("transport_start_failed");
   IsTransportRunning = true;
  }
  public static void DebugLog(string message) {}
 }
}
public static class McpToggleProbe {
 public static string Run(bool running, bool failStart) {
  var app = new RvtMcp.Plugin.App { IsTransportRunning = running, FailStart = failStart,
   Config = new RvtMcp.Plugin.RvtMcpConfig { AutoStartTransport = running } };
  RvtMcp.Plugin.App.Instance = app;
  string message = "";
  try {
   var status = new RvtMcp.Plugin.Commands.ToggleMcpCommand().Execute(null, ref message, null);
   return JsonConvert.SerializeObject(new { status = status.ToString(), running = app.IsTransportRunning,
    preference = app.Config.AutoStartTransport, message });
  } catch (Exception ex) { return JsonConvert.SerializeObject(new { exception = ex.Message }); }
  finally { RvtMcp.Plugin.App.Instance = null; }
 }
}
""";

    public void Dispose()
    {
        RvtMcpConfig.ConfigFilePathOverride = _previousPath;
        if (File.Exists(_path)) File.Delete(_path);
    }
}
