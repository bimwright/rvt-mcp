using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests;

/// <summary>
/// Compiles unchanged production handler sources against a test-only Revit API seam and
/// returns the seam's <c>Probe.Run(string scenario)</c>, which answers a JSON string. No
/// newer-year symbol is defined, so shared sources follow their Revit 2022 / int32 path.
/// The seam proves the handler's control flow, not native Revit behaviour.
/// </summary>
internal static class HandlerProbe
{
    public static Func<string, JObject> Compile(string seam, string[] sources, [CallerFilePath] string file = "")
    {
        var root = RepoRoot(file);
        var texts = sources.Select(path => File.ReadAllText(Path.Combine(root, path))).Append(seam);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(JObject).Assembly.Location).Distinct().Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("HandlerProbe_" + Guid.NewGuid().ToString("N"),
            texts.Select(text => CSharpSyntaxTree.ParseText(text)), references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var emitted = compilation.Emit(output);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var run = Assembly.Load(output.ToArray()).GetType("Probe")!.GetMethod("Run")!;
        return scenario => JObject.Parse((string)run.Invoke(null, new object[] { scenario })!);
    }

    private static string RepoRoot(string file)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "RvtMcp.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found from " + file);
    }
}
