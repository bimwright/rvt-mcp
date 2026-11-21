using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>revit_describe_api's core, against a small stand-in for the Revit API (the real one is checked outside the suite).</summary>
    public class ApiDescriberTests
    {
        private const string Api = @"
using System;
using System.Collections.Generic;
namespace Autodesk.Revit.DB {
  public class ElementId { }
  public class Element {
    public ElementId Id => null;
    public static bool IsValid(ElementId id) => true;
    public string Name { get; set; }
  }
  public class CurveElement : Element { public void SetGeometryCurve(object curve, bool overrideJoins) { } }
  public class ModelCurve : CurveElement { }
  public class DetailCurve : CurveElement { }
  public class XYZ {
    public XYZ(double x, double y, double z) { }
    public double this[int index] => 0;
    public static XYZ Zero => null;
    [Obsolete(""Use Length instead."")] public double GetLength() => 0;
    public bool TryGet(out double x, ref int y) { x = 0; return true; }
    public IList<ElementId> Ids(double? tolerance = null) => null;
    public static XYZ operator +(XYZ left, XYZ right) => null;
  }
  public enum DisplayStyle { Shading, ShadingWithEdges, Wireframe }
  public class Document { }
}
namespace Autodesk.Revit.Creation { public class Document : Autodesk.Revit.DB.Element { } }
namespace Autodesk.Revit.DB.Architecture { public class Room : Autodesk.Revit.DB.Element { } }";

        private static readonly Assembly[] Assemblies = { Compile() };

        private static Assembly Compile()
        {
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("DescribeApi_" + Guid.NewGuid().ToString("N"), new[] { CSharpSyntaxTree.ParseText(Api) },
                references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var bytes = new MemoryStream();
            var emitted = compilation.Emit(bytes);
            Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
            return Assembly.Load(bytes.ToArray());
        }

        private static Dictionary<string, object> Describe(string type, string? member = null)
        {
            var result = ApiDescriber.Describe(Assemblies, type, member);
            Assert.Null(result.Error);
            return (Dictionary<string, object>)result.Value;
        }

        private static string[] Group(Dictionary<string, object> value, string key) =>
            (string[])((Dictionary<string, object>)value["members"])[key];

        [Fact]
        public void Lists_constructors_static_factories_and_members_as_csharp()
        {
            var value = Describe("XYZ");
            Assert.Equal("Autodesk.Revit.DB.XYZ", value["type"]);
            Assert.Equal(new[]
            {
                "new XYZ(double x, double y, double z)",
                "static Zero: XYZ { get; }",
                "this[int index]: double { get; }",
                "GetLength() -> double [obsolete: Use Length instead.]",
                "Ids(double? tolerance = optional) -> IList<ElementId>",
                "TryGet(out double x, ref int y) -> bool",
                "operator +(XYZ left, XYZ right) -> XYZ",
            }, Group(value, "declared"));
        }

        [Fact]
        public void Groups_inherited_members_and_lists_base_types_and_subtypes()
        {
            var value = Describe("CurveElement");
            Assert.Equal(new[] { "Element" }, value["base_types"]);
            Assert.Equal(new[] { "DetailCurve", "ModelCurve" }, value["subtypes"]);
            Assert.Equal(new[] { "new CurveElement()", "SetGeometryCurve(object curve, bool overrideJoins) -> void" }, Group(value, "declared"));
            Assert.Contains("static IsValid(ElementId id) -> bool", Group(value, "from Element"));
            Assert.Contains("Name: string { get; set; }", Group(value, "from Element"));
        }

        [Fact]
        public void Member_filter_keeps_matching_names_and_says_when_none_match()
        {
            Assert.Equal(new[] { "Id: ElementId { get; }", "static IsValid(ElementId id) -> bool" }.OrderBy(s => s),
                Group(Describe("Element", "id"), "declared").OrderBy(s => s));
            // get_ only finds named indexed properties (called as get_Name in C#), not plain ones.
            Assert.Empty((Dictionary<string, object>)Describe("Element", "get_Id")["members"]);
            var none = Describe("Element", "Nothing");
            Assert.Empty((Dictionary<string, object>)none["members"]);
            Assert.Contains("has 'Nothing' in its name", (string)none["note"]);
        }

        [Fact]
        public void A_short_name_shared_by_two_api_types_is_shown_in_full()
        {
            Assert.Equal(new[] { "Autodesk.Revit.Creation.Document", "CurveElement", "Room" }, Describe("Element")["subtypes"]);
        }

        [Fact]
        public void Enum_lists_its_values()
        {
            var value = Describe("DisplayStyle");
            Assert.Equal("enum", value["kind"]);
            Assert.Equal(new[] { "Shading", "ShadingWithEdges", "Wireframe" }, Group(value, "declared"));
        }

        [Fact]
        public void Ambiguous_short_name_asks_for_the_full_name()
        {
            var result = ApiDescriber.Describe(Assemblies, "Document", null);
            Assert.StartsWith("AMBIGUOUS_TYPE: 'Document' matches Autodesk.Revit.Creation.Document, Autodesk.Revit.DB.Document. Pass the full name.", result.Error);
            Assert.Equal("Autodesk.Revit.DB.Document", Describe("Autodesk.Revit.DB.Document")["type"]);
            Assert.Equal("Autodesk.Revit.DB.Architecture.Room", Describe("Architecture.Room")["type"]);
        }

        [Fact]
        public void Unknown_type_names_the_closest_types()
        {
            var result = ApiDescriber.Describe(Assemblies, "CurveElemnt", null);
            Assert.StartsWith("TYPE_NOT_FOUND: no public Revit API type 'CurveElemnt' in this Revit version. Closest: CurveElement", result.Error);
            Assert.StartsWith("INVALID_ARGUMENT", ApiDescriber.Describe(Assemblies, " ", null).Error);
        }

        [Fact]
        public void Long_lists_are_capped_with_a_note()
        {
            var many = "namespace Autodesk.Revit.DB { public enum Big { " +
                       string.Join(", ", Enumerable.Range(0, ApiDescriber.MaxLines + 5).Select(i => "V" + i)) + " } }";
            var compilation = CSharpCompilation.Create("Big_" + Guid.NewGuid().ToString("N"), new[] { CSharpSyntaxTree.ParseText(many) },
                ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var bytes = new MemoryStream();
            Assert.True(compilation.Emit(bytes).Success);
            var value = (Dictionary<string, object>)ApiDescriber.Describe(new[] { Assembly.Load(bytes.ToArray()) }, "Big", null).Value;
            Assert.Equal(ApiDescriber.MaxLines, Group(value, "declared").Length);
            Assert.Equal(true, value["truncated"]);
            Assert.Equal(ApiDescriber.MaxLines + 5, value["member_count"]);
        }
    }
}
