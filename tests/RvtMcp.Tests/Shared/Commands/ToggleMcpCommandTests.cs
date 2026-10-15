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

    [Fact]
    public void Manual_start_warns_before_starting_and_remembers_on_after_acknowledgment()
    {
        var result = Run(false, otherMcpCount: 1);
        Assert.Equal("Succeeded", result.Value<string>("status"));
        Assert.True(result.Value<bool>("running"));
        Assert.Equal(1, result.Value<int>("dialogs"));
        Assert.Contains("2", result.Value<string>("notice"));
        Assert.False(result.Value<bool>("runningAtNotice"));
        Assert.True(RvtMcpConfig.LoadReadOnly(_path, _ => null).AutoStartTransportOrDefault);
    }

    [Fact]
    public void Dismissing_the_notice_keeps_transport_off_and_does_not_change_saved_preference()
    {
        Assert.True(RvtMcpConfig.TrySaveAutoStartTransport(true, out var error, _path), error);
        var before = File.ReadAllBytes(_path);
        var result = Run(false, otherMcpCount: 2, acknowledge: false);
        Assert.Equal("Cancelled", result.Value<string>("status"));
        Assert.False(result.Value<bool>("running"));
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    [Fact]
    public void Manual_stop_does_not_show_a_coexistence_notice()
    {
        var result = Run(true, otherMcpCount: 1);
        Assert.Equal("Succeeded", result.Value<string>("status"));
        Assert.False(result.Value<bool>("running"));
        Assert.Equal(0, result.Value<int>("dialogs"));
    }

    [Fact]
    public void Manual_start_with_no_other_third_party_MCP_does_not_show_a_notice()
    {
        var result = Run(false);
        Assert.True(result.Value<bool>("running"));
        Assert.Equal(0, result.Value<int>("dialogs"));
    }

    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(false, 0, false)]
    [InlineData(true, 1, false)]
    [InlineData(false, 1, false)]
    public void Actual_startup_block_prioritizes_coexistence_without_overwriting_preference(bool preference, int otherMcpCount, bool expectedStart)
    {
        Assert.True(RvtMcpConfig.TrySaveAutoStartTransport(preference, out var error, _path), error);
        var before = File.ReadAllBytes(_path);
        var startup = Probe.Value.DeclaringType!.Assembly.GetType("McpStartupProbe")!.GetMethod("Run")!;
        var result = JObject.Parse((string)startup.Invoke(null, new object[] { preference, otherMcpCount })!);
        Assert.Equal(expectedStart, result.Value<bool>("started"));
        Assert.Equal(preference, result.Value<bool>("preference"));
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    private JObject Run(bool running, bool failStart = false, int otherMcpCount = 0, bool acknowledge = true) =>
        JObject.Parse((string)Probe.Value.Invoke(null, new object[] { running, failStart, otherMcpCount, acknowledge })!);

    private static readonly Lazy<MethodInfo> Probe = new Lazy<MethodInfo>(() => CompileProbe());

    private static MethodInfo CompileProbe([CallerFilePath] string testFile = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", ".."));
        var source = File.ReadAllText(Path.Combine(root, "src", "shared", "Commands", "ToggleMcpCommand.cs"));
        string startupBlock = null;
        foreach (var year in new[] { 22, 23, 24, 25, 26, 27 })
        {
            var app = File.ReadAllText(Path.Combine(root, "src", "plugin-r" + year, "App.cs")).Replace("\r\n", "\n");
            var start = app.IndexOf("            var otherMcps =", StringComparison.Ordinal);
            var end = app.IndexOf("            _ribbonApplication =", start, StringComparison.Ordinal);
            Assert.True(start >= 0 && end > start, "startup block not found R" + year);
            var block = app.Substring(start, end - start);
            if (startupBlock == null) startupBlock = block;
            else Assert.Equal(startupBlock, block);
        }
        var startupSource = """
using System;
using RvtMcp.Plugin;
public static class McpStartupProbe {
 public static string Run(bool preference, int otherMcpCount) {
  var Config = new RvtMcpConfig { AutoStartTransport = preference };
  McpAddinDiscovery.OtherCount = otherMcpCount;
  var started = false;
  void CreateAndStartTransport() { started = true; }
  void DebugLog(string text) {}
""" + startupBlock + "return Newtonsoft.Json.JsonConvert.SerializeObject(new {started, preference=Config.AutoStartTransport}); } }";
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Append(typeof(JObject).Assembly.Location)
            .Append(typeof(RvtMcpConfig).Assembly.Location).Distinct()
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("McpToggleProbe_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(source), CSharpSyntaxTree.ParseText(Api), CSharpSyntaxTree.ParseText(startupSource) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var emitted = compilation.Emit(output);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return Assembly.Load(output.ToArray()).GetType("McpToggleProbe")!.GetMethod("Run")!;
    }

    // Revit UI, native transport and installed-addin discovery are doubled. The
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
 public enum Result { Failed, Succeeded, Cancelled }
 public class ExternalCommandData {}
 public interface IExternalCommand { Result Execute(ExternalCommandData data, ref string message, Autodesk.Revit.DB.ElementSet elements); }
 public enum TaskDialogResult { Ok, Cancel }
 public enum TaskDialogCommonButtons { Ok }
 public enum TaskDialogIcon { TaskDialogIconWarning }
 public class TaskDialog {
  public static int Shows;
  public static bool Acknowledge, WasRunning;
  public static string Notice;
  public TaskDialog(string title) {}
  public string MainInstruction { get; set; }
  public TaskDialogIcon MainIcon { get; set; }
  public TaskDialogCommonButtons CommonButtons { get; set; }
  public TaskDialogResult Show() {
   Shows++; Notice = MainInstruction; WasRunning = RvtMcp.Plugin.App.Instance.IsTransportRunning;
   return Acknowledge ? TaskDialogResult.Ok : TaskDialogResult.Cancel;
  }
 }
}
namespace RvtMcp.Plugin.Localization { public static class L {
 public static string T(string key, params (string Name, object Value)[] args) {
  foreach (var arg in args) if (arg.Name == "count") return "MCP count=" + arg.Value;
  return "Preference could not be saved; this session is still using the new state.";
 }
} }
namespace RvtMcp.Plugin {
 public static class AuthToken { public static string RevitVersion = "2027"; }
 public static class McpAddinDiscovery {
  public static int OtherCount;
  public static System.Collections.Generic.IReadOnlyList<string> FindOtherMcpAddins(string year, Action<string> log) {
   var names = new System.Collections.Generic.List<string>();
   for (var i=0; i<OtherCount; i++) names.Add("Other MCP");
   return names;
  }
 }
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
 public static string Run(bool running, bool failStart, int otherMcpCount, bool acknowledge) {
  RvtMcp.Plugin.McpAddinDiscovery.OtherCount = otherMcpCount;
  TaskDialog.Shows = 0; TaskDialog.Notice = null; TaskDialog.WasRunning = false; TaskDialog.Acknowledge = acknowledge;
  var app = new RvtMcp.Plugin.App { IsTransportRunning = running, FailStart = failStart,
   Config = new RvtMcp.Plugin.RvtMcpConfig { AutoStartTransport = running } };
  RvtMcp.Plugin.App.Instance = app;
  string message = "";
  try {
   var status = new RvtMcp.Plugin.Commands.ToggleMcpCommand().Execute(null, ref message, null);
   return JsonConvert.SerializeObject(new { status = status.ToString(), running = app.IsTransportRunning,
    preference = app.Config.AutoStartTransport, message, dialogs = TaskDialog.Shows,
    notice = TaskDialog.Notice, runningAtNotice = TaskDialog.WasRunning });
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
