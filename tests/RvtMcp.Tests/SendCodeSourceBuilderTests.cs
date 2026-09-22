using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public class SendCodeSourceBuilderTests
    {
        [Theory]
        [InlineData("return 14;", 14)]
        [InlineData("// helper class example\nreturn 14;", 14)]
        [InlineData("return \"a class marker\";", "a class marker")]
        [InlineData("return new Helper().Value;\npublic class Helper { public int Value => 14; }", 14)]
        [InlineData("var h = new Helper();\npublic class Helper { public int Value => 14; }\nreturn h.Value;", 14)]
        [InlineData("public class Helper { public int Value => 14; }\nreturn new Helper().Value;", 14)]
        [InlineData("using X = System.Math;\nreturn X.Abs(-14);", 14)]
        [InlineData("int Value() { return 14; }\nreturn Value();", 14)]
        [InlineData("return (int)Value.Good;\npublic enum Value { Good = 14 }", 14)]
        public void CompilesAndRunsBodyWithOptionalHelperTypes(string code, object expected)
        {
            Assert.Equal(expected, CompileAndRun(SendCodeSourceBuilder.Build(code)));
        }

        [Fact]
        public void PreservesExplicitEntrypointAndHelperClass()
        {
            const string code = @"using Autodesk.Revit.UI;
public class McpDynamicScript { public static object Run(UIApplication app) { return new Helper().Value; } }
public class Helper { public int Value => 14; }";
            Assert.Equal(code, SendCodeSourceBuilder.Build(code));
            Assert.Equal(14, CompileAndRun(SendCodeSourceBuilder.Build(code)));
        }

        [Fact]
        public void PreservesCompilerErrorsForInvalidBody()
        {
            Assert.Contains(Compile(SendCodeSourceBuilder.Build("return missingName; ")).GetDiagnostics(),
                d => d.Id == "CS0103");
        }

        private static CSharpCompilation Compile(string source)
        {
            // Only stand in for UIApplication/Document; use the real Roslyn compiler and entrypoint.
            const string api = @"namespace Autodesk.Revit.DB { public class Document {} }
namespace Autodesk.Revit.UI {
 public class UIApplication { public UIDocument ActiveUIDocument { get; } = new UIDocument(); }
 public class UIDocument { public Autodesk.Revit.DB.Document Document { get; } = new Autodesk.Revit.DB.Document(); }
}";
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                .Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
            return CSharpCompilation.Create("SendCodeTest_" + Guid.NewGuid().ToString("N"),
                new[] { CSharpSyntaxTree.ParseText(source), CSharpSyntaxTree.ParseText(api) }, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }

        private static object CompileAndRun(string source)
        {
            using var bytes = new MemoryStream();
            var result = Compile(source).Emit(bytes);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            var assembly = Assembly.Load(bytes.ToArray());
            var app = Activator.CreateInstance(assembly.GetType("Autodesk.Revit.UI.UIApplication"));
            return assembly.GetType("McpDynamicScript").GetMethod("Run").Invoke(null, new[] { app });
        }
    }
}
