using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Scripting;
using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>Compile-error hints, against a small stand-in for the Revit API (the real one is replayed outside the suite).</summary>
    public class ScriptCompileHintsTests
    {
        private const string Api = @"
namespace Autodesk.Revit.DB {
  public class ElementId { }
  public class Category { }
  public class Element { public ElementId Id => null; public Category Category => null; }
  public class CurveElement : Element { public string LineStyleName => """"; }
  public class ModelCurve : CurveElement { public Category Subcategory => null; }
  public class Definition { public string Name => """"; }
  public class FamilyParameter { public Definition Definition => null; public string Formula => """"; }
  public class Document { public Autodesk.Revit.ApplicationServices.Application Application => null; }
  public class Frame { }
  public class XYZ { public XYZ(double x, double y, double z) { } }
  public class Plane {
    private Plane() { }
    public static Plane Create(Frame frame) => null;
    public static Plane CreateByNormalAndOrigin(XYZ normal, XYZ origin) => null;
  }
  public class OptionsA { } public class OptionsB { }
  public class Importer {
    public bool Import(string file, OptionsA options, int view, out int id) { id = 0; return true; }
    public int Import(string file, OptionsB options, int view) => 0;
  }
  public enum DisplayStyle { Shading, ShadingWithEdges, Wireframe }
}
namespace Autodesk.Revit.DB.Architecture { public class Room { } }
namespace Autodesk.Revit.DB.Electrical { public class CableTray { } }
namespace Autodesk.Revit.DB.Mechanical { public class Duct { } }
namespace Autodesk.Revit.DB.Plumbing { public class Pipe { } }
namespace Autodesk.Revit.DB.Structure { public enum StructuralType { NonStructural } }
namespace Autodesk.Revit.DB.Visual { public class AssetPropertyDouble { } }
namespace Autodesk.Revit.ApplicationServices { public class Application { public string FamilyTemplatePath => """"; public string VersionName => """"; } }
namespace Autodesk.Revit.UI {
  public class UIApplication { public UIDocument ActiveUIDocument => null; public Autodesk.Revit.ApplicationServices.Application Application => null; }
  public class UIDocument { public Autodesk.Revit.DB.Document Document => null; }
}";

        private static readonly MetadataReference ApiReference = CompileApi();

        private static MetadataReference CompileApi()
        {
            var compilation = CSharpCompilation.Create("HintApi_" + Guid.NewGuid().ToString("N"), new[] { CSharpSyntaxTree.ParseText(Api) },
                Platform(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var bytes = new MemoryStream();
            var emitted = compilation.Emit(bytes);
            Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
            return MetadataReference.CreateFromImage(bytes.ToArray());
        }

        private static MetadataReference[] Platform() =>
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Concat(new[] { typeof(JObject).Assembly.Location, typeof(McpTaskResult).Assembly.Location, typeof(ScriptInput).Assembly.Location })
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToArray();

        private static ScriptCompileDiagnostic[] Errors(string code)
        {
            var result = SendCodeCompiler.Compile(new ScriptInput(code), Array.Empty<ScriptModuleRevision>(), Platform().Concat(new[] { ApiReference }));
            Assert.False(result.Success);
            return result.Diagnostics;
        }

        private static string Hint(string code, string id) => Assert.Single(Errors(code), d => d.DiagnosticId == id).Hint;

        [Fact]
        public void Missing_member_reached_through_a_property_names_the_path()
        {
            var hint = Hint("return app.FamilyTemplatePath;", "CS1061");
            Assert.Contains("is a member of Application, not of UIApplication: use x.Application.FamilyTemplatePath.", hint);
        }

        [Fact]
        public void Missing_member_of_a_subtype_advises_a_checked_cast()
        {
            var hint = Hint("CurveElement c = null; return c.Subcategory;", "CS1061");
            Assert.Contains("'Subcategory' is a member of ModelCurve (a subtype of CurveElement)", hint);
            Assert.Contains("if (x is ModelCurve y)", hint);
        }

        [Fact]
        public void Missing_member_of_an_unrelated_type_is_not_advised_as_a_cast()
        {
            var hint = Hint("FamilyParameter p = null; return p.Name;", "CS1061");
            Assert.Contains("use x.Definition.Name", hint);
            Assert.DoesNotContain("cast", hint);
        }

        [Fact]
        public void Member_that_exists_nowhere_lists_the_closest_names()
        {
            var hint = Hint("return DisplayStyle.ShadedWithEdges;", "CS0117");
            Assert.StartsWith("No Revit API type in this Revit version has a member 'ShadedWithEdges'.", hint);
            Assert.Contains("closest to 'ShadedWithEdges': ShadingWithEdges", hint);
        }

        [Fact]
        public void Unknown_type_names_the_namespace_to_import()
        {
            var hint = Hint("AssetPropertyDouble p = null; return p;", "CS0246");
            Assert.Equal("'AssetPropertyDouble' is Autodesk.Revit.DB.Visual.AssetPropertyDouble: add `using Autodesk.Revit.DB.Visual;` " +
                         "or write Autodesk.Revit.DB.Visual.AssetPropertyDouble.", hint);
        }

        [Fact]
        public void Type_without_a_public_constructor_lists_its_factories()
        {
            var hint = Hint("return new Plane(new Frame());", "CS1729");
            Assert.StartsWith("Plane has no public constructor; create it with: static Plane.Create(Frame frame) -> Plane", hint);
        }

        [Fact]
        public void Wrong_argument_ranks_the_overload_that_accepts_it_first()
        {
            var hint = Hint("var i = new Importer(); return i.Import(\"f\", new OptionsB(), 1, out int id);", "CS1503");
            Assert.StartsWith("Signature: Importer.Import(string file, OptionsB options, int view) -> int | ", hint);
        }

        [Fact]
        public void Non_api_errors_and_plain_unknown_names_get_no_hint()
        {
            Assert.Null(Hint("return \"x\".Lenght;", "CS1061"));
            Assert.Null(Hint("return missingName;", "CS0103"));
        }

        [Fact]
        public void A_hint_is_given_once_per_compile_and_survives_redaction()
        {
            var errors = Errors("return app.FamilyTemplatePath + app.FamilyTemplatePath;");
            Assert.Equal(2, errors.Count(d => d.DiagnosticId == "CS1061"));
            var hint = Assert.Single(errors, d => d.Hint != null).Hint;
            Assert.Equal(hint, McpResponsePrivacy.RedactErrorForResponse(hint));
        }
    }
}
