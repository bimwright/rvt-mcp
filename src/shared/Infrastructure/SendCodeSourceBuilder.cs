using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RvtMcp.Plugin
{
    public static class SendCodeSourceBuilder
    {
        // Body scripts get these imports unless the caller already has them. The DB sub-namespaces hold discipline types
        // (Stairs, Room, CableTray, Duct, Pipe, StructuralType); no public type name in them collides with another default
        // import or with each other (checked for Revit 2022–2027).
        private static readonly string[] DefaultImports =
        {
            "System", "System.Linq", "System.Collections.Generic", "Autodesk.Revit.DB",
            "Autodesk.Revit.DB.Architecture", "Autodesk.Revit.DB.Electrical", "Autodesk.Revit.DB.Mechanical",
            "Autodesk.Revit.DB.Plumbing", "Autodesk.Revit.DB.Structure", "Autodesk.Revit.UI"
        };

        internal static string BuildEnhanced(string code, bool hasData, bool taskContract)
        {
            var root = CSharpSyntaxTree.ParseText(code).GetCompilationUnitRoot();
            // #define/#undef must precede every token, so the caller's go above the generated imports.
            // Blanking them in place keeps the line numbers that #line mapping relies on.
            var symbolsEnd = root.GetFirstToken(includeZeroWidth: true).LeadingTrivia
                .Where(t => t.IsKind(SyntaxKind.DefineDirectiveTrivia) || t.IsKind(SyntaxKind.UndefDirectiveTrivia))
                .Select(t => t.FullSpan.End).DefaultIfEmpty(0).Max();
            var symbols = symbolsEnd == 0 ? "" : "\n#line 1 \"script\"\n" + code.Substring(0, symbolsEnd) + "\n#line hidden\n";
            if (symbolsEnd != 0)
                root = CSharpSyntaxTree.ParseText(new string(code.Substring(0, symbolsEnd).Where(c => c == '\r' || c == '\n').ToArray())
                    + code.Substring(symbolsEnd)).GetCompilationUnitRoot();
            var statements = root.Members.OfType<GlobalStatementSyntax>().ToArray();
            var fullSource = statements.Length == 0 && root.Members.Count != 0;
            var imports = new List<string>();
            imports.AddRange(root.Externs.Select(Mapped));
            // Generated imports follow the caller's: global usings must precede ordinary ones.
            imports.AddRange(root.Usings.Select(Mapped));
            if (!fullSource)
                foreach (var name in DefaultImports)
                    if (!root.Usings.Any(u => u.Alias == null && u.StaticKeyword.IsKind(SyntaxKind.None) && u.Name.ToString() == name))
                        imports.Add("using " + name + ";\n");
            if (hasData && !fullSource && !root.Usings.Any(u => u.Name.ToString() == "Newtonsoft.Json.Linq" && u.Alias == null))
                imports.Add("using Newtonsoft.Json.Linq;\n");
            if (taskContract && !root.Usings.Any(u => u.Name.ToString() == "RvtMcp.Plugin" && u.Alias == null))
                imports.Add("using RvtMcp.Plugin;\n");
            imports.AddRange(root.AttributeLists.Select(Mapped));
            var ending = root.EndOfFileToken.LeadingTrivia.ToFullString();
            if (fullSource)
                return "#line hidden\n" + symbols + string.Concat(imports) + string.Concat(root.Members.Select(Mapped)) + ending;
            // global:: keeps caller aliases such as "using Autodesk = ..." from capturing generated references.
            return "#line hidden\n" + symbols + string.Concat(imports) + @"
#line hidden
public class McpDynamicScript {
 public static object Run(global::Autodesk.Revit.UI.UIApplication app" + (hasData ? ", global::Newtonsoft.Json.Linq.JObject data" : "") + @") {
  var uidoc = app.ActiveUIDocument;
  var doc = uidoc?.Document;
" + string.Concat(statements.Select(Mapped)) + @"
#line hidden
 }
}
" + string.Concat(root.Members.Where(m => !(m is GlobalStatementSyntax)).Select(Mapped)) + ending;
        }

        private static string Mapped(SyntaxNode node)
        {
            var start = node.SyntaxTree.GetLineSpan(node.FullSpan).StartLinePosition;
            return "\n#line " + (start.Line + 1) + " \"script\"\n" + new string(' ', start.Character) + node.ToFullString() + "\n#line hidden\n";
        }

        public static string Build(string code)
        {
            var root = CSharpSyntaxTree.ParseText(code).GetCompilationUnitRoot();
            var statements = root.Members.OfType<GlobalStatementSyntax>().ToArray();
            // A compilation unit with declarations only is the existing full-source mode.
            if (statements.Length == 0 && root.Members.Count != 0) return code;

            var body = string.Concat(statements.Select(s => s.ToFullString()));
            var wrapper = SyntaxFactory.ParseMemberDeclaration(@"
public class McpDynamicScript
{
    public static object Run(UIApplication app)
    {
        var uidoc = app.ActiveUIDocument;
        var doc = uidoc?.Document;
" + body + @"
    }
}");
            // Keep helper types outside Run; even a type between statements is valid input.
            var members = root.Members.Where(m => !(m is GlobalStatementSyntax)).Concat(new[] { wrapper });
            root = root.WithMembers(SyntaxFactory.List(members));
            foreach (var name in DefaultImports)
            {
                if (!root.Usings.Any(u => u.Alias == null && u.StaticKeyword.IsKind(SyntaxKind.None) && u.Name.ToString() == name))
                    root = root.AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(name)));
            }
            return root.NormalizeWhitespace().ToFullString();
        }
    }
}
