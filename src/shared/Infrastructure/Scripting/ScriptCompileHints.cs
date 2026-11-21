using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// Hints for send_code compile errors, computed from the Revit API the script was compiled against: for a missing
    /// member, where that member really is (a subtype to cast to, a property that leads to it, or the closest members);
    /// for a wrong call, the real signatures; for an unknown type, the namespace that has it. Everything comes from the
    /// script's own compilation (types resolved by the semantic model, never by name alone), so a hint names only what
    /// exists in the loaded Revit version. Advisory: any failure gives no hint rather than a wrong one. Host-free.
    /// </summary>
    internal static class ScriptCompileHints
    {
        internal const int MaxHintChars = 600;
        private const int MaxNear = 6;
        private const int MaxOwners = 4;
        private const int MaxSignatures = 4;
        private const string ApiNamespace = "Autodesk.Revit";

        private static readonly SymbolDisplayFormat TypeFormat = new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

        private static readonly ConcurrentDictionary<string, ApiIndex> Indexes = new ConcurrentDictionary<string, ApiIndex>();

        /// <summary>One hint (or null) per diagnostic, in order. A hint already given for an earlier diagnostic is not repeated.</summary>
        internal static string[] For(IReadOnlyList<Diagnostic> diagnostics, Compilation compilation)
        {
            var hints = new string[diagnostics.Count];
            var given = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < diagnostics.Count; i++)
            {
                string hint = null;
                try { hint = One(diagnostics[i], compilation); }
                catch { hint = null; }
                if (hint == null) continue;
                if (hint.Length > MaxHintChars) hint = hint.Substring(0, MaxHintChars - 1) + "…";
                if (given.Add(hint)) hints[i] = hint;
            }
            return hints;
        }

        private static string One(Diagnostic d, Compilation compilation)
        {
            var tree = d.Location.SourceTree;
            if (tree == null || !d.Location.IsInSource) return null;
            var node = tree.GetRoot().FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true);
            switch (d.Id)
            {
                case "CS1061":
                case "CS0117":
                    return MissingMember(node, compilation.GetSemanticModel(tree), compilation);
                case "CS0246":
                case "CS0103":
                    return MissingType(node, d.Id, compilation);
                case "CS1620":
                case "CS1503":
                case "CS1615":
                case "CS1501":
                case "CS7036":
                case "CS1729":
                    return Signatures(node, d, compilation.GetSemanticModel(tree));
                default:
                    return null;
            }
        }

        // ---- missing member (CS1061 / CS0117) ----

        private static string MissingMember(SyntaxNode node, SemanticModel model, Compilation compilation)
        {
            var name = node as SimpleNameSyntax ?? node.AncestorsAndSelf().OfType<SimpleNameSyntax>().FirstOrDefault();
            if (name == null) return null;
            ExpressionSyntax receiver = null;
            if (name.Parent is MemberAccessExpressionSyntax access && access.Name == name) receiver = access.Expression;
            else if (name.Parent is MemberBindingExpressionSyntax)
                receiver = name.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault()?.Expression;
            if (receiver == null) return null;

            var type = model.GetTypeInfo(receiver).Type as INamedTypeSymbol
                       ?? model.GetSymbolInfo(receiver).Symbol as INamedTypeSymbol;
            if (type == null || type.TypeKind == TypeKind.Error || !IsApi(type)) return null;
            var member = name.Identifier.ValueText;
            var index = Index(compilation);
            var shown = Display(index);

            var mine = Members(type);
            var owners = index.Owners(member, compilation)
                .Where(o => !SymbolEqualityComparer.Default.Equals(o, type) && !InheritsFrom(type, o)).ToArray();
            var parts = new List<string>();

            var subtypes = owners.Where(o => InheritsFrom(o, type))
                .OrderBy(o => Depth(o, type)).ThenBy(o => o.Name.Length).ThenBy(o => o.Name, StringComparer.Ordinal)
                .Take(MaxOwners).ToArray();
            var via = subtypes.Length > 0 ? null : PathTo(type, owners);
            if (subtypes.Length > 0)
            {
                var first = shown(subtypes[0]);
                parts.Add($"'{member}' is a member of {string.Join(", ", subtypes.Select(shown))} " +
                          $"({(subtypes.Length == 1 ? "a subtype" : "subtypes")} of {shown(type)}): check and cast, e.g. " +
                          $"`if (x is {first} y) ... y.{member}`.");
            }
            else if (via != null)
                parts.Add($"'{member}' is a member of {shown(via.Value.Owner)}, not of {shown(type)}: use x.{via.Value.Property}.{member}.");
            else if (owners.Length > 0 && owners.Length <= MaxOwners)
                parts.Add($"'{member}' is a member of {string.Join(", ", owners.Select(shown))}, not of {shown(type)}.");
            else if (owners.Length == 0)
                parts.Add($"No Revit API type in this Revit version has a member '{member}'.");

            var near = Nearest(mine.Where(n => !n.Equals(member, StringComparison.Ordinal)), member, MaxNear);
            if (near.Length > 0) parts.Add($"{shown(type)} members closest to '{member}': {string.Join(", ", near)}.");
            return parts.Count == 0 ? null : string.Join(" ", parts);
        }

        /// <summary>A property of <paramref name="type"/> (own or inherited) whose value has the member: UIApplication.Application.</summary>
        private static (string Property, INamedTypeSymbol Owner)? PathTo(INamedTypeSymbol type, INamedTypeSymbol[] owners)
        {
            var best = AllTypes(type).SelectMany(t => t.GetMembers()).OfType<IPropertySymbol>()
                .Where(p => p.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Public && !p.IsIndexer && !p.IsStatic && p.Parameters.Length == 0)
                .Select(p => (p, owner: owners.FirstOrDefault(o => p.Type is INamedTypeSymbol t && (SymbolEqualityComparer.Default.Equals(t, o) || InheritsFrom(t, o)))))
                .Where(x => x.owner != null)
                .OrderBy(x => SymbolEqualityComparer.Default.Equals(x.p.Type, x.owner) ? 0 : 1)
                .ThenBy(x => x.p.Name == x.owner.Name ? 0 : 1).ThenBy(x => x.p.Name, StringComparer.Ordinal)
                .FirstOrDefault();
            return best.p == null ? ((string, INamedTypeSymbol)?)null : (best.p.Name, best.owner);
        }

        // ---- unknown type or name (CS0246 / CS0103) ----

        private static string MissingType(SyntaxNode node, string id, Compilation compilation)
        {
            var name = node as SimpleNameSyntax ?? node.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().FirstOrDefault();
            if (name == null) return null;
            var text = name.Identifier.ValueText;
            var index = Index(compilation);
            if (index.TypesByName.TryGetValue(text, out var full))
            {
                var names = full.Take(3).ToArray();
                if (names.Length == 1)
                {
                    var ns = names[0].Substring(0, names[0].Length - text.Length - 1);
                    return $"'{text}' is {names[0]}: add `using {ns};` or write {names[0]}.";
                }
                return $"'{text}' is one of {string.Join(", ", names)}: write the full name or add its using.";
            }
            // StringBuilder, Regex, Path, JArray: a public type of another referenced assembly needs its using, not a Revit name.
            var elsewhere = OtherNamespaces(compilation, text);
            if (elsewhere.Length == 1) return $"'{text}' is {elsewhere[0]}.{text}: add `using {elsewhere[0]};` or write {elsewhere[0]}.{text}.";
            if (elsewhere.Length > 1)
                return $"'{text}' is one of {string.Join(", ", elsewhere.Take(3).Select(ns => ns + "." + text))}: write the full name or add its using.";
            // An unknown plain name is usually the script's own variable; only a name used as a type gets suggestions.
            if (id != "CS0246") return null;
            var near = Nearest(index.TypesByName.Keys, text, 5, minShared: 4);
            return near.Length == 0 ? null : $"No Revit API type '{text}'. Closest: {string.Join(", ", near)}.";
        }

        /// <summary>
        /// Namespaces (outside the Revit API) of the referenced assemblies that have a public top-level type of this name,
        /// System ones first. Walks namespaces only, on the failure path, so nothing is indexed or kept.
        /// </summary>
        private static string[] OtherNamespaces(Compilation compilation, string typeName)
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var assembly in compilation.References.Select(compilation.GetAssemblyOrModuleSymbol).OfType<IAssemblySymbol>())
                Collect(assembly.GlobalNamespace, typeName, found);
            return found.OrderBy(ns => ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(ns => ns.Length).ThenBy(ns => ns, StringComparer.Ordinal).ToArray();
        }

        private static void Collect(INamespaceSymbol ns, string typeName, HashSet<string> found)
        {
            foreach (var child in ns.GetNamespaceMembers())
            {
                var name = child.ToDisplayString();
                if (name == ApiNamespace || name.StartsWith(ApiNamespace + ".", StringComparison.Ordinal) || name.Contains("Internal")) continue;
                if (child.GetTypeMembers(typeName).Any(t => t.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Public)) found.Add(name);
                Collect(child, typeName, found);
            }
        }

        // ---- wrong call (argument count, type, ref/out, constructor) ----

        private static string Signatures(SyntaxNode node, Diagnostic d, SemanticModel model)
        {
            var onArgument = d.Id == "CS1620" || d.Id == "CS1503" || d.Id == "CS1615";
            var call = CalledExpression(node, d.Location.SourceSpan, onArgument);
            if (call == null) return null;

            var info = model.GetSymbolInfo(call);
            IEnumerable<ISymbol> symbols = info.Symbol != null ? new[] { info.Symbol } : (IEnumerable<ISymbol>)info.CandidateSymbols;
            var lead = "Signature: ";
            if (!symbols.Any())
            {
                if (call is BaseObjectCreationExpressionSyntax creation && model.GetTypeInfo(creation).Type is INamedTypeSymbol created)
                {
                    symbols = created.InstanceConstructors.Where(c => c.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Public);
                    if (!symbols.Any())
                    {
                        // Revit builds many types through static factories: Plane.CreateByFrame, Arc.Create, Line.CreateBound.
                        symbols = created.GetMembers().OfType<IMethodSymbol>().Where(m => m.IsStatic && m.MethodKind == MethodKind.Ordinary &&
                            m.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Public && SymbolEqualityComparer.Default.Equals(m.ReturnType, created));
                        lead = $"{created.Name} has no public constructor; create it with: ";
                    }
                }
                else if (call is InvocationExpressionSyntax invocation)
                    symbols = model.GetMemberGroup(invocation.Expression);
            }
            var methods = symbols.OfType<IMethodSymbol>().Distinct<IMethodSymbol>(SymbolEqualityComparer.Default).ToArray();
            if (methods.Length == 0 || !methods.All(m => IsApi(m.ContainingType))) return null;
            // The overloads that fit the arguments actually passed come first: Document.Import(string, SATImportOptions, …).
            var arguments = Arguments(call).Select(a => model.GetTypeInfo(a.Expression).Type).ToArray();
            var compilation = model.Compilation as Microsoft.CodeAnalysis.CSharp.CSharpCompilation;
            var named = System.Text.RegularExpressions.Regex.Match(d.GetMessage(), @"^Argument (\d+):");
            var focus = named.Success ? int.Parse(named.Groups[1].Value) - 1 : -1;
            var ranked = methods.Select((m, i) => (m, i, score: Fit(m, arguments, compilation, focus)))
                .OrderByDescending(x => x.score).ThenBy(x => x.i).Select(x => x.m).ToArray();
            var lines = ranked.Select(Render).Distinct().Take(MaxSignatures).ToArray();
            var more = methods.Length > MaxSignatures ? $" (+{methods.Length - MaxSignatures} more)" : "";
            return lead + string.Join(" | ", lines) + more + ".";
        }

        private static IEnumerable<ArgumentSyntax> Arguments(ExpressionSyntax call) =>
            call is InvocationExpressionSyntax invocation ? invocation.ArgumentList.Arguments
            : call is BaseObjectCreationExpressionSyntax creation && creation.ArgumentList != null ? creation.ArgumentList.Arguments
            : Enumerable.Empty<ArgumentSyntax>();

        /// <summary>
        /// An overload that accepts the argument the error names ranks first, then the same argument count, then how
        /// many passed arguments match the parameters.
        /// </summary>
        private static int Fit(IMethodSymbol method, ITypeSymbol[] arguments, Microsoft.CodeAnalysis.CSharp.CSharpCompilation compilation, int focus)
        {
            var score = method.Parameters.Length == arguments.Length ? 20 : 0;
            if (focus >= 0 && focus < arguments.Length && focus < method.Parameters.Length && arguments[focus] != null && compilation != null
                && compilation.ClassifyConversion(arguments[focus], method.Parameters[focus].Type).IsImplicit)
                score += 50;
            for (var i = 0; i < Math.Min(arguments.Length, method.Parameters.Length); i++)
            {
                var argument = arguments[i];
                var parameter = method.Parameters[i].Type;
                if (argument == null || argument.TypeKind == TypeKind.Error) continue;
                if (SymbolEqualityComparer.Default.Equals(argument, parameter)) score += 2;
                else if (compilation != null && compilation.ClassifyConversion(argument, parameter).IsImplicit) score += 1;
            }
            return score;
        }

        /// <summary>
        /// The call the diagnostic belongs to: for an argument error the call whose argument list holds that argument,
        /// otherwise (CS1501/CS7036/CS1729 sit on the callee or the created type) the call around it.
        /// </summary>
        private static ExpressionSyntax CalledExpression(SyntaxNode node, Microsoft.CodeAnalysis.Text.TextSpan span, bool onArgument)
        {
            for (var n = node; n != null; n = n.Parent)
            {
                if (onArgument)
                {
                    // The argument itself may be a call or a `new` (Import(f, new OptionsB(), …)): climb to its argument.
                    if (n is StatementSyntax) return null;
                    if (!(n is ArgumentSyntax arg)) continue;
                    var owner = arg.Parent?.Parent;
                    return owner is InvocationExpressionSyntax || owner is BaseObjectCreationExpressionSyntax ? (ExpressionSyntax)owner : null;
                }
                switch (n)
                {
                    case BaseObjectCreationExpressionSyntax creation:
                        return creation;
                    case InvocationExpressionSyntax invocation when invocation.Expression.Span.Contains(span):
                        return invocation;
                    case StatementSyntax _:
                        return null;
                }
            }
            return null;
        }

        private static string Render(IMethodSymbol m)
        {
            var args = string.Join(", ", m.Parameters.Select(p =>
                (p.RefKind == RefKind.Ref ? "ref " : p.RefKind == RefKind.Out ? "out " : "") +
                p.Type.ToDisplayString(TypeFormat) + " " + p.Name + (p.IsOptional ? " = optional" : "")));
            var owner = m.ContainingType.ToDisplayString(TypeFormat);
            if (m.MethodKind == MethodKind.Constructor) return $"new {owner}({args})";
            return $"{(m.IsStatic ? "static " : "")}{owner}.{m.Name}({args}) -> {m.ReturnType.ToDisplayString(TypeFormat)}";
        }

        // ---- the API index ----

        private sealed class ApiIndex
        {
            internal readonly Dictionary<string, List<string>> MemberOwners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            internal readonly Dictionary<string, List<string>> TypesByName = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            internal INamedTypeSymbol[] Owners(string member, Compilation compilation) =>
                MemberOwners.TryGetValue(member, out var names)
                    ? names.Select(compilation.GetTypeByMetadataName).Where(t => t != null).Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).ToArray()
                    : new INamedTypeSymbol[0];
        }

        /// <summary>Member and type names of the Revit API assemblies the script references, built once per API build.</summary>
        private static ApiIndex Index(Compilation compilation)
        {
            var assemblies = compilation.References.Select(compilation.GetAssemblyOrModuleSymbol).OfType<IAssemblySymbol>()
                .Where(a => Namespace(a.GlobalNamespace, ApiNamespace) != null).ToArray();
            var key = string.Join("|", assemblies.Select(a => a.Identity.GetDisplayName() + "#" +
                a.Modules.FirstOrDefault()?.GetMetadata()?.GetModuleVersionId()).OrderBy(s => s, StringComparer.Ordinal));
            return Indexes.GetOrAdd(key, _ =>
            {
                var index = new ApiIndex();
                foreach (var assembly in assemblies)
                    foreach (var type in Types(Namespace(assembly.GlobalNamespace, ApiNamespace)))
                    {
                        var metadataName = MetadataName(type);
                        // A nested type cannot be imported with a using, so only top-level types answer an unknown name.
                        if (type.ContainingType == null) Add(index.TypesByName, type.Name, type.ToDisplayString());
                        foreach (var member in type.GetMembers())
                            if (IsVisibleMember(member)) Add(index.MemberOwners, member.Name, metadataName);
                    }
                return index;
            });
        }

        private static void Add(Dictionary<string, List<string>> map, string key, string value)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<string>();
            if (!list.Contains(value)) list.Add(value);
        }

        private static INamespaceSymbol Namespace(INamespaceSymbol root, string dotted)
        {
            var current = root;
            foreach (var part in dotted.Split('.'))
            {
                current = current?.GetNamespaceMembers().FirstOrDefault(n => n.Name == part);
                if (current == null) return null;
            }
            return current;
        }

        private static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol ns)
        {
            if (ns == null) yield break;
            foreach (var type in ns.GetTypeMembers())
                foreach (var t in WithNested(type)) yield return t;
            foreach (var child in ns.GetNamespaceMembers())
                foreach (var t in Types(child)) yield return t;
        }

        private static IEnumerable<INamedTypeSymbol> WithNested(INamedTypeSymbol type)
        {
            if (type.DeclaredAccessibility != Microsoft.CodeAnalysis.Accessibility.Public) yield break;
            yield return type;
            foreach (var nested in type.GetTypeMembers())
                foreach (var t in WithNested(nested)) yield return t;
        }

        private static string MetadataName(INamedTypeSymbol type)
        {
            var name = type.MetadataName;
            for (var outer = type.ContainingType; outer != null; outer = outer.ContainingType) name = outer.MetadataName + "+" + name;
            var ns = type.ContainingNamespace?.ToDisplayString();
            return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        }

        private static bool IsVisibleMember(ISymbol member) =>
            member.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Public && !member.IsImplicitlyDeclared &&
            (member is IPropertySymbol || member is IFieldSymbol || member is IEventSymbol ||
             member is IMethodSymbol m && m.MethodKind == MethodKind.Ordinary);

        /// <summary>Public member names of the type and every type it inherits (API types only).</summary>
        private static string[] Members(INamedTypeSymbol type) =>
            AllTypes(type).SelectMany(t => t.GetMembers()).Where(IsVisibleMember).Select(m => m.Name)
                .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        private static IEnumerable<INamedTypeSymbol> AllTypes(INamedTypeSymbol type)
        {
            for (var t = type; t != null && IsApi(t); t = t.BaseType) yield return t;
            foreach (var i in type.AllInterfaces.Where(IsApi)) yield return i;
        }

        private static bool InheritsFrom(INamedTypeSymbol type, INamedTypeSymbol ancestor)
        {
            for (var t = type.BaseType; t != null; t = t.BaseType)
                if (SymbolEqualityComparer.Default.Equals(t, ancestor)) return true;
            return ancestor.TypeKind == TypeKind.Interface && type.AllInterfaces.Contains(ancestor, SymbolEqualityComparer.Default);
        }

        private static int Depth(INamedTypeSymbol type, INamedTypeSymbol ancestor)
        {
            var depth = 0;
            for (var t = type; t != null && !SymbolEqualityComparer.Default.Equals(t, ancestor); t = t.BaseType) depth++;
            return depth;
        }

        private static bool IsApi(ITypeSymbol type)
        {
            var ns = type?.ContainingNamespace?.ToDisplayString();
            return ns != null && (ns == ApiNamespace || ns.StartsWith(ApiNamespace + ".", StringComparison.Ordinal));
        }

        /// <summary>The short name, or the full name when the Revit API has another type of that name.</summary>
        private static Func<INamedTypeSymbol, string> Display(ApiIndex index) => t =>
        {
            var shortName = t.ContainingType != null ? t.ContainingType.Name + "." + t.Name : t.Name;
            return index.TypesByName.TryGetValue(t.Name, out var all) && all.Count > 1 ? t.ToDisplayString() : shortName;
        };

        internal static string[] Nearest(IEnumerable<string> names, string wanted, int count, int minShared = 0) =>
            names.Distinct(StringComparer.Ordinal)
                .Select(n => (n, d: Distance(n.ToLowerInvariant(), wanted.ToLowerInvariant()), s: Shared(n, wanted)))
                .Where(x => x.s >= minShared)
                .OrderByDescending(x => x.s).ThenBy(x => x.d).ThenBy(x => x.n, StringComparer.Ordinal)
                .Take(count).Select(x => x.n).ToArray();

        /// <summary>Length of the longest common substring (case-insensitive), so "IsShownInPlanRCPCut" ranks near "IsShownInPlanRCP".</summary>
        private static int Shared(string a, string b)
        {
            a = a.ToLowerInvariant(); b = b.ToLowerInvariant();
            var best = 0;
            for (var i = 0; i < a.Length; i++)
                for (var j = 0; j < b.Length; j++)
                {
                    var k = 0;
                    while (i + k < a.Length && j + k < b.Length && a[i + k] == b[j + k]) k++;
                    if (k > best) best = k;
                }
            return best < 3 ? 0 : best;
        }

        private static int Distance(string a, string b)
        {
            var prev = Enumerable.Range(0, b.Length + 1).ToArray();
            for (var i = 1; i <= a.Length; i++)
            {
                var cur = new int[b.Length + 1];
                cur[0] = i;
                for (var j = 1; j <= b.Length; j++)
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = cur;
            }
            return prev[b.Length];
        }
    }
}
