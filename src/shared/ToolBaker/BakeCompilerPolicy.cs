using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RvtMcp.Plugin.ToolBaker
{
    public sealed class BakeCompilerPolicyResult
    {
        private BakeCompilerPolicyResult(bool allowed, string error)
        {
            Allowed = allowed;
            Error = error;
        }

        public bool Allowed { get; }
        public string Error { get; }

        public static BakeCompilerPolicyResult Pass()
        {
            return new BakeCompilerPolicyResult(true, null);
        }

        public static BakeCompilerPolicyResult Fail(string error)
        {
            if (string.IsNullOrWhiteSpace(error))
                error = "ToolBaker compiler policy rejected the source.";

            if (!error.StartsWith("SecurityViolation:", StringComparison.Ordinal))
                error = "SecurityViolation: " + error;

            return new BakeCompilerPolicyResult(false, error);
        }
    }

    public static class BakeCompilerPolicy
    {
        private static readonly string[] BannedNamePrefixes =
        {
            "System.Diagnostics.ProcessStartInfo",
            "System.Diagnostics.Process",
            "System.Net.Http",
            "System.Net",
            "System.IO.DirectoryInfo",
            "System.IO.FileInfo",
            "System.IO.Directory",
            "System.IO.File",
            "Microsoft.Win32.RegistryKey",
            "Microsoft.Win32.Registry",
            "System.Reflection.Emit",
            "System.Runtime.InteropServices.DllImportAttribute",
        };

        private static readonly string[] BannedStringPatterns =
        {
            "System.Diagnostics.ProcessStartInfo",
            "System.Diagnostics.Process",
            "System.Net.Http.HttpClient",
            "System.IO.DirectoryInfo",
            "System.IO.FileInfo",
            "System.IO.Directory",
            "System.IO.File",
            "Microsoft.Win32.RegistryKey",
            "Microsoft.Win32.Registry",
            "System.Reflection.Assembly",
            "System.Reflection.Emit",
            "System.AppDomain",
            "System.Runtime.InteropServices.DllImportAttribute",
            "System.Runtime.InteropServices.DllImport",
        };

        private static readonly string[] AllowedUsingNames =
        {
            "System",
            "System.Linq",
            "System.Collections.Generic",
            "Autodesk.Revit.DB",
            "Autodesk.Revit.UI",
            "RvtMcp.Plugin",
            "Newtonsoft.Json.Linq",
        };

        private static readonly string[] AllowedSymbolPrefixes =
        {
            "Autodesk.Revit.DB",
            "Autodesk.Revit.UI",
            "Newtonsoft.Json.Linq",
            "System.Linq",
            "System.Collections.Generic",
            "System.Math",
            "System.String",
            "System.DateTime",
            "System.Guid",
            "System.Exception",
            "System.Object",
            "System.ValueType",
            "System.Boolean",
            "System.Byte",
            "System.SByte",
            "System.Char",
            "System.Decimal",
            "System.Double",
            "System.Single",
            "System.Int16",
            "System.Int32",
            "System.Int64",
            "System.UInt16",
            "System.UInt32",
            "System.UInt64",
            "System.Void",
            "System.Nullable",
        };

        private static readonly string[] AllowedExactSymbolNames =
        {
            "RvtMcp.Plugin.IRevitCommand",
            "RvtMcp.Plugin.CommandResult",
        };

        private static readonly SymbolDisplayFormat FullNameFormat = new SymbolDisplayFormat(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.None,
            memberOptions: SymbolDisplayMemberOptions.None,
            delegateStyle: SymbolDisplayDelegateStyle.NameOnly,
            extensionMethodStyle: SymbolDisplayExtensionMethodStyle.Default,
            parameterOptions: SymbolDisplayParameterOptions.None,
            propertyStyle: SymbolDisplayPropertyStyle.NameOnly,
            localOptions: SymbolDisplayLocalOptions.None,
            kindOptions: SymbolDisplayKindOptions.None,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.None);

        public static BakeCompilerPolicyResult Validate(string sourceCode, IEnumerable<MetadataReference> references)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode ?? string.Empty);
            var root = syntaxTree.GetRoot();

            var syntaxResult = ValidateSyntax(root);
            if (!syntaxResult.Allowed)
                return syntaxResult;

            var referenceArray = references == null
                ? Array.Empty<MetadataReference>()
                : references.Where(r => r != null).ToArray();

            var compilation = CSharpCompilation.Create(
                "BakedToolPolicy_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                new[] { syntaxTree },
                referenceArray,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithAllowUnsafe(true));

            // Match the compiler's binding: Revit has internal global names that shadow its public API.
            var model = compilation.GetSemanticModel(syntaxTree, ignoreAccessibility: false);

            var usingResult = ValidateUsingDirectives(root, model, compilation);
            if (!usingResult.Allowed)
                return usingResult;

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var result = ValidateSymbols(model, compilation, invocation);
                if (!result.Allowed)
                    return result;
            }

            foreach (var memberAccess in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                var result = ValidateSyntaxName(memberAccess.ToString());
                if (!result.Allowed)
                    return result;

                result = ValidateSymbols(model, compilation, memberAccess);
                if (!result.Allowed)
                    return result;
            }

            foreach (var objectCreation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                var result = ValidateSyntaxName(objectCreation.Type.ToString());
                if (!result.Allowed)
                    return result;

                result = ValidateSymbols(model, compilation, objectCreation);
                if (!result.Allowed)
                    return result;

                var typeResult = ValidateType(model.GetTypeInfo(objectCreation).Type, compilation);
                if (!typeResult.Allowed)
                    return typeResult;
            }

            foreach (var typeOf in root.DescendantNodes().OfType<TypeOfExpressionSyntax>())
            {
                var result = ValidateSyntaxName(typeOf.Type.ToString());
                if (!result.Allowed)
                    return result;

                result = ValidateType(model.GetTypeInfo(typeOf.Type).Type, compilation);
                if (!result.Allowed)
                    return result;
            }

            foreach (var identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                var result = ValidateSymbols(model, compilation, identifier);
                if (!result.Allowed)
                    return result;
            }

            foreach (var qualifiedName in root.DescendantNodes().OfType<QualifiedNameSyntax>())
            {
                var result = ValidateSyntaxName(qualifiedName.ToString());
                if (!result.Allowed)
                    return result;

                result = ValidateSymbols(model, compilation, qualifiedName);
                if (!result.Allowed)
                    return result;
            }

            return BakeCompilerPolicyResult.Pass();
        }

        private static BakeCompilerPolicyResult ValidateSyntax(SyntaxNode root)
        {
            foreach (var token in root.DescendantTokens())
            {
                if (token.IsKind(SyntaxKind.UnsafeKeyword))
                    return BakeCompilerPolicyResult.Fail("unsafe code is not allowed in baked tools.");

                if (token.IsKind(SyntaxKind.ExternKeyword))
                    return BakeCompilerPolicyResult.Fail("extern declarations are not allowed in baked tools.");
            }

            foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>())
            {
                if (IsDllImportSyntax(attribute.Name.ToString()))
                    return BakeCompilerPolicyResult.Fail("DllImport is not allowed in baked tools.");
            }

            foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
            {
                if (!literal.IsKind(SyntaxKind.StringLiteralExpression))
                    continue;

                var value = literal.Token.ValueText ?? string.Empty;
                foreach (var pattern in BannedStringPatterns)
                {
                    if (value.IndexOf(pattern, StringComparison.Ordinal) >= 0)
                        return BakeCompilerPolicyResult.Fail("reflection string pattern is not allowed: " + pattern);
                }
            }

            return BakeCompilerPolicyResult.Pass();
        }

        private static BakeCompilerPolicyResult ValidateUsingDirectives(
            SyntaxNode root,
            SemanticModel model,
            CSharpCompilation compilation)
        {
            foreach (var usingDirective in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
            {
                if (usingDirective.Name == null)
                    continue;

                var name = NormalizeName(usingDirective.Name.ToString());
                var syntaxResult = ValidateSyntaxName(name);
                if (!syntaxResult.Allowed)
                    return syntaxResult;

                if (!IsAllowedUsingName(name))
                    return BakeCompilerPolicyResult.Fail("using directive is outside the ToolBaker allow list: " + name);

                if (usingDirective.Alias == null)
                    continue;

                var alias = model.GetAliasInfo(usingDirective.Name);
                if (alias == null)
                    continue;

                var aliasResult = ValidateSymbol(alias.Target, compilation);
                if (!aliasResult.Allowed)
                    return aliasResult;
            }

            return BakeCompilerPolicyResult.Pass();
        }

        private static BakeCompilerPolicyResult ValidateSymbols(
            SemanticModel model,
            CSharpCompilation compilation,
            SyntaxNode node)
        {
            var symbolInfo = model.GetSymbolInfo(node);
            var symbols = new List<ISymbol>();
            if (symbolInfo.Symbol != null)
                symbols.Add(symbolInfo.Symbol);
            symbols.AddRange(symbolInfo.CandidateSymbols);

            var typeInfo = model.GetTypeInfo(node);
            if (typeInfo.Type != null)
                symbols.Add(typeInfo.Type);
            if (typeInfo.ConvertedType != null)
                symbols.Add(typeInfo.ConvertedType);

            foreach (var symbol in symbols.Distinct(SymbolEqualityComparer.Default))
            {
                var result = ValidateSymbol(symbol, compilation);
                if (!result.Allowed)
                    return result;
            }

            return BakeCompilerPolicyResult.Pass();
        }

        private static BakeCompilerPolicyResult ValidateSymbol(ISymbol symbol, CSharpCompilation compilation)
        {
            if (symbol == null || IsLocalSymbol(symbol))
                return BakeCompilerPolicyResult.Pass();

            if (symbol is IAliasSymbol alias)
                symbol = alias.Target;

            var banned = MatchBannedSymbol(symbol);
            if (banned != null)
                return BakeCompilerPolicyResult.Fail("banned API is not allowed: " + banned);

            if (IsSourceDefined(symbol, compilation))
                return BakeCompilerPolicyResult.Pass();

            var policyName = GetPolicyName(symbol);
            if (string.IsNullOrWhiteSpace(policyName))
                return BakeCompilerPolicyResult.Pass();

            if (symbol is INamespaceSymbol && IsAllowedNamespaceName(policyName))
                return BakeCompilerPolicyResult.Pass();

            if (IsAllowedSymbolName(policyName))
                return BakeCompilerPolicyResult.Pass();

            return BakeCompilerPolicyResult.Fail("API is outside the ToolBaker allow list: " + policyName);
        }

        private static BakeCompilerPolicyResult ValidateType(ITypeSymbol type, CSharpCompilation compilation)
        {
            if (type == null)
                return BakeCompilerPolicyResult.Pass();

            if (type is IArrayTypeSymbol arrayType)
                return ValidateType(arrayType.ElementType, compilation);

            return ValidateSymbol(type, compilation);
        }

        private static string MatchBannedSymbol(ISymbol symbol)
        {
            if (symbol is IMethodSymbol method)
            {
                var reduced = method.ReducedFrom;
                if (reduced != null)
                {
                    var reducedBanned = MatchBannedSymbol(reduced);
                    if (reducedBanned != null)
                        return reducedBanned;
                }

                var containingTypeName = GetTypeName(method.ContainingType);
                if (containingTypeName == "System.Reflection.Assembly" &&
                    method.Name.StartsWith("Load", StringComparison.Ordinal))
                    return "System.Reflection.Assembly.Load*";

                if (containingTypeName == "System.AppDomain" &&
                    method.Name == "CreateDomain")
                    return "System.AppDomain.CreateDomain";

                return MatchBannedName(containingTypeName);
            }

            if (symbol is IPropertySymbol property)
                return MatchBannedName(GetTypeName(property.ContainingType));

            if (symbol is IFieldSymbol field)
                return MatchBannedName(GetTypeName(field.ContainingType));

            if (symbol is IEventSymbol eventSymbol)
                return MatchBannedName(GetTypeName(eventSymbol.ContainingType));

            if (symbol is INamedTypeSymbol namedType)
                return MatchBannedName(GetTypeName(namedType));

            if (symbol is INamespaceSymbol namespaceSymbol)
                return MatchBannedName(GetNamespaceName(namespaceSymbol));

            return MatchBannedName(GetPolicyName(symbol));
        }

        private static BakeCompilerPolicyResult ValidateSyntaxName(string rawName)
        {
            var banned = MatchBannedName(NormalizeName(rawName));
            return banned == null
                ? BakeCompilerPolicyResult.Pass()
                : BakeCompilerPolicyResult.Fail("banned API is not allowed: " + banned);
        }

        private static string MatchBannedName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var normalized = NormalizeName(name);
            foreach (var banned in BannedNamePrefixes)
            {
                if (NameEqualsOrIsBelow(normalized, banned))
                    return banned;
            }

            return null;
        }

        private static bool IsDllImportSyntax(string rawName)
        {
            var name = NormalizeName(rawName);
            return name == "DllImport" ||
                   name == "DllImportAttribute" ||
                   name.EndsWith(".DllImport", StringComparison.Ordinal) ||
                   name.EndsWith(".DllImportAttribute", StringComparison.Ordinal);
        }

        private static bool IsAllowedUsingName(string name)
        {
            var normalized = NormalizeName(name);
            if (AllowedUsingNames.Any(allowed => string.Equals(normalized, allowed, StringComparison.Ordinal)))
                return true;

            return normalized.StartsWith("Autodesk.Revit.DB.", StringComparison.Ordinal) ||
                   normalized.StartsWith("Autodesk.Revit.UI.", StringComparison.Ordinal) ||
                   normalized.StartsWith("Newtonsoft.Json.Linq.", StringComparison.Ordinal);
        }

        private static bool IsAllowedNamespaceName(string name)
        {
            var normalized = NormalizeName(name);
            if (string.Equals(normalized, "System", StringComparison.Ordinal))
                return true;

            foreach (var allowed in AllowedUsingNames.Concat(AllowedSymbolPrefixes))
            {
                if (string.Equals(allowed, "System", StringComparison.Ordinal))
                    continue;

                if (NameEqualsOrIsBelow(normalized, allowed) ||
                    allowed.StartsWith(normalized + ".", StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static bool IsAllowedSymbolName(string name)
        {
            var normalized = NormalizeName(name);
            if (AllowedExactSymbolNames.Any(allowed => string.Equals(normalized, allowed, StringComparison.Ordinal)))
                return true;

            return AllowedSymbolPrefixes.Any(allowed => NameEqualsOrIsBelow(normalized, allowed));
        }

        private static bool NameEqualsOrIsBelow(string name, string prefix)
        {
            if (string.Equals(name, prefix, StringComparison.Ordinal))
                return true;

            return name.StartsWith(prefix + ".", StringComparison.Ordinal) ||
                   name.StartsWith(prefix + "<", StringComparison.Ordinal);
        }

        private static bool IsLocalSymbol(ISymbol symbol)
        {
            return symbol.Kind == SymbolKind.Local ||
                   symbol.Kind == SymbolKind.Parameter ||
                   symbol.Kind == SymbolKind.RangeVariable ||
                   symbol.Kind == SymbolKind.Label;
        }

        private static bool IsSourceDefined(ISymbol symbol, CSharpCompilation compilation)
        {
            if (symbol == null)
                return false;

            if (symbol is INamedTypeSymbol namedType && namedType.IsAnonymousType)
                return true;

            var assembly = symbol.ContainingAssembly;
            if (assembly != null && SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly))
                return true;

            var containingType = GetContainingType(symbol);
            if (containingType == null)
                return false;

            var containingAssembly = containingType.ContainingAssembly;
            return containingAssembly != null &&
                   SymbolEqualityComparer.Default.Equals(containingAssembly, compilation.Assembly);
        }

        private static string GetPolicyName(ISymbol symbol)
        {
            if (symbol == null)
                return null;

            if (symbol is IMethodSymbol method)
                return GetTypeName(method.ContainingType);

            if (symbol is IPropertySymbol property)
                return GetTypeName(property.ContainingType);

            if (symbol is IFieldSymbol field)
                return GetTypeName(field.ContainingType);

            if (symbol is IEventSymbol eventSymbol)
                return GetTypeName(eventSymbol.ContainingType);

            if (symbol is INamedTypeSymbol namedType)
                return GetTypeName(namedType);

            if (symbol is INamespaceSymbol namespaceSymbol)
                return GetNamespaceName(namespaceSymbol);

            return NormalizeName(symbol.ToDisplayString(FullNameFormat));
        }

        private static INamedTypeSymbol GetContainingType(ISymbol symbol)
        {
            if (symbol is INamedTypeSymbol namedType)
                return namedType;

            if (symbol is IMethodSymbol method)
                return method.ContainingType;

            if (symbol is IPropertySymbol property)
                return property.ContainingType;

            if (symbol is IFieldSymbol field)
                return field.ContainingType;

            if (symbol is IEventSymbol eventSymbol)
                return eventSymbol.ContainingType;

            return symbol.ContainingType;
        }

        private static string GetTypeName(ITypeSymbol type)
        {
            if (type == null)
                return null;

            if (type is IArrayTypeSymbol arrayType)
                return GetTypeName(arrayType.ElementType) + "[]";

            if (type is INamedTypeSymbol namedType)
                return NormalizeName(namedType.OriginalDefinition.ToDisplayString(FullNameFormat));

            return NormalizeName(type.ToDisplayString(FullNameFormat));
        }

        private static string GetNamespaceName(INamespaceSymbol namespaceSymbol)
        {
            return namespaceSymbol == null
                ? null
                : NormalizeName(namespaceSymbol.ToDisplayString(FullNameFormat));
        }

        private static string NormalizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            var compact = new string(name.Where(c => !char.IsWhiteSpace(c)).ToArray());
            return compact.Replace("global::", string.Empty);
        }
    }
}
