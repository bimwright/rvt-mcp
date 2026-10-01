using System;
using System.Collections.Generic;
using System.Linq;
using RvtMcp.Plugin.ToolBaker;
using Microsoft.CodeAnalysis;
using Xunit;

namespace RvtMcp.Tests
{
    public class BakeCompilerPolicyTests
    {
        [Theory]
        [InlineData("UIApplication", "Autodesk.Revit.UI")]
        [InlineData("Transaction", "Autodesk.Revit.DB")]
        public void Validate_AllowsPublicRevitTypeShadowedByInternalGlobalTypes(string typeName, string namespaceName)
        {
            var references = References();
            MetadataReference Fixture(string name, string source)
            {
                var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                    name,
                    new[] { Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source) },
                    references,
                    new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
                using var image = new System.IO.MemoryStream();
                var emitted = compilation.Emit(image);
                Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
                return MetadataReference.CreateFromImage(image.ToArray());
            }

            // Revit 2027 assemblies contain inaccessible global names that shadow the public API.
            var api = Fixture("RevitPublicFixture", $@"
internal class {typeName} {{ }}
namespace {namespaceName} {{ public class {typeName} {{ }} }}");
            var native = Fixture("RevitNativeFixture", $"internal class {typeName} {{ }}");
            var source = $@"
using {namespaceName};
public class BakedTool_public_api
{{
    public object Run() {{ return new {typeName}(); }}
}}";
            var allReferences = references.Concat(new[] { api, native }).ToArray();
            var compiler = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                "BakedPublicFixture",
                new[] { Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source) },
                allReferences,
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.DoesNotContain(compiler.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);

            var result = BakeCompilerPolicy.Validate(source, allReferences);

            Assert.True(result.Allowed, result.Error);
        }

        [Fact]
        public void Validate_RejectsDirectProcessStart()
        {
            var result = ValidateBody(@"System.Diagnostics.Process.Start(""cmd"");");

            AssertDenied(result, "System.Diagnostics.Process");
        }

        [Fact]
        public void Validate_RejectsAliasedProcessStart()
        {
            var result = ValidateSource(@"
using P = System.Diagnostics.Process;
public class BakedTool_alias
{
    public object Run() { P.Start(""cmd""); return null; }
}");

            AssertDenied(result, "System.Diagnostics.Process");
        }

        [Fact]
        public void Validate_RejectsFullyQualifiedFileMutation()
        {
            var result = ValidateBody(@"System.IO.File.WriteAllText(""c:\\temp\\x.txt"", ""x"");");

            AssertDenied(result, "System.IO.File");
        }

        [Fact]
        public void Validate_RejectsHttpClient()
        {
            var result = ValidateBody(@"var client = new System.Net.Http.HttpClient();");

            AssertDenied(result, "System.Net.Http");
        }

        [Fact]
        public void Validate_RejectsProcessStartInfo()
        {
            var result = ValidateBody(@"var startInfo = new System.Diagnostics.ProcessStartInfo(""cmd"");");

            AssertDenied(result, "System.Diagnostics.ProcessStartInfo");
        }

        [Fact]
        public void Validate_RejectsSystemNetNamespaceUse()
        {
            var result = ValidateSource(@"
using System.Net;
public class BakedTool_net
{
    public object Run() { var client = new WebClient(); return client; }
}");

            AssertDenied(result, "System.Net");
        }

        [Fact]
        public void Validate_RejectsAssemblyLoad()
        {
            var result = ValidateBody(@"var assembly = System.Reflection.Assembly.Load(""SomeAssembly"");");

            AssertDenied(result, "System.Reflection.Assembly.Load*");
        }

        [Fact]
        public void Validate_RejectsAppDomainCreateDomain()
        {
            var result = ValidateBody(@"var domain = System.AppDomain.CreateDomain(""baked"");");

            AssertDenied(result, "System.AppDomain.CreateDomain");
        }

        [Fact]
        public void Validate_RejectsToolBakerRegistryEscape()
        {
            var result = ValidateSource(@"
using RvtMcp.Plugin.ToolBaker;
public class BakedTool_registry_escape
{
    public object Run()
    {
        new BakedToolRegistry().Save(new BakedToolMeta { Name = ""x"" }, ""source"");
        return null;
    }
}");

            AssertDenied(result, "RvtMcp.Plugin.ToolBaker");
        }

        [Fact]
        public void Validate_RejectsHandlerNamespaceEscape()
        {
            var result = ValidateSource(@"
using RvtMcp.Plugin.Handlers;
public class BakedTool_handler_escape
{
    public object Run() { return null; }
}");

            AssertDenied(result, "RvtMcp.Plugin.Handlers");
        }

        [Theory]
        [InlineData(@"[System.Runtime.InteropServices.DllImport(""user32.dll"")] public static extern int MessageBoxA(System.IntPtr h, string t, string c, int type);")]
        [InlineData(@"public unsafe object Run() { int value = 0; int* p = &value; return value; }")]
        [InlineData(@"public extern object Run();")]
        [InlineData(@"public object Run() { var t = System.Type.GetType(""System.Diagnostics.Process""); return t; }")]
        public void Validate_RejectsSyntaxEscapesAndReflectionStringPatterns(string memberSource)
        {
            var result = ValidateSource($@"
public class BakedTool_escape
{{
    {memberSource}
}}");

            Assert.False(result.Allowed);
            Assert.StartsWith("SecurityViolation:", result.Error);
        }

        [Fact]
        public void Validate_AllowsGeneratedWrapperRevitLinqAndJObject()
        {
            var result = ValidateSource(@"
using System;
using System.Linq;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;

public class BakedTool_allowed : IRevitCommand
{
    public string Name => ""allowed"";
    public string Description => ""allowed"";
    public string ParametersSchema => ""{}"";

    public CommandResult Execute(UIApplication app, string paramsJson)
    {
        try
        {
            var doc = app.ActiveUIDocument.Document;
            var uidoc = app.ActiveUIDocument;
            var request = JObject.Parse(paramsJson ?? ""{}"");
            var ids = new List<ElementId> { new ElementId(1), new ElementId(2) };
            var selected = ids.Where(id => id.IntegerValue > 0).Select(id => id.IntegerValue).ToList();
            var rounded = Math.Round(12.5);
            var stamp = DateTime.UtcNow.ToString(""o"");
            var marker = Guid.NewGuid().ToString();
            return CommandResult.Ok(new { count = selected.Count, rounded, stamp, marker, hasDoc = doc != null, hasUiDoc = uidoc != null, request });
        }
        catch (Exception ex)
        {
            return CommandResult.Fail(ex.Message);
        }
    }
}

namespace Autodesk.Revit.DB
{
    public class Document { }
    public class ElementId
    {
        public ElementId(int value) { IntegerValue = value; }
        public int IntegerValue { get; }
    }
}

namespace Autodesk.Revit.UI
{
    public class UIApplication
    {
        public UIDocument ActiveUIDocument { get; } = new UIDocument();
    }

    public class UIDocument
    {
        public Autodesk.Revit.DB.Document Document { get; } = new Autodesk.Revit.DB.Document();
    }
}

namespace RvtMcp.Plugin
{
    public interface IRevitCommand
    {
        string Name { get; }
        string Description { get; }
        string ParametersSchema { get; }
        CommandResult Execute(Autodesk.Revit.UI.UIApplication app, string paramsJson);
    }

    public class CommandResult
    {
        public static CommandResult Ok(object data) => new CommandResult();
        public static CommandResult Fail(string error) => new CommandResult();
    }
}");

            Assert.True(result.Allowed, result.Error);
        }

        private static BakeCompilerPolicyResult ValidateBody(string body)
        {
            return ValidateSource($@"
public class BakedTool_test
{{
    public object Run()
    {{
        {body}
        return null;
    }}
}}");
        }

        private static BakeCompilerPolicyResult ValidateSource(string source)
        {
            return BakeCompilerPolicy.Validate(source, References());
        }

        private static MetadataReference[] References()
        {
            var assemblies = new[]
            {
                typeof(object).Assembly,
                typeof(Enumerable).Assembly,
                typeof(List<>).Assembly,
                typeof(Console).Assembly,
                typeof(Newtonsoft.Json.Linq.JObject).Assembly,
                typeof(System.Net.Http.HttpClient).Assembly,
            };

            return assemblies
                .Concat(AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location)))
                .GroupBy(a => a.GetName().Name)
                .Select(g => g.OrderByDescending(a => a.GetName().Version).First())
                .Select(a => MetadataReference.CreateFromFile(a.Location))
                .ToArray();
        }

        private static void AssertDenied(BakeCompilerPolicyResult result, string expected)
        {
            Assert.False(result.Allowed);
            Assert.StartsWith("SecurityViolation:", result.Error);
            Assert.Contains(expected, result.Error);
        }
    }
}
