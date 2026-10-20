using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RvtMcp.Plugin
{
    // Bind real Revit methods, never text-match arbitrary .Close calls or string literals.
    internal sealed class SendCodeDocumentRewriter : CSharpSyntaxRewriter
    {
        private readonly SemanticModel _model;
        private SendCodeDocumentRewriter(SemanticModel model) { _model = model; }

        internal static CSharpCompilation Apply(CSharpCompilation compilation, SyntaxTree tree)
        {
            var root = new SendCodeDocumentRewriter(compilation.GetSemanticModel(tree)).Visit(tree.GetRoot());
            return compilation.ReplaceSyntaxTree(tree, tree.WithRootAndOptions(root, tree.Options));
        }

        public override SyntaxNode VisitConditionalAccessExpression(ConditionalAccessExpressionSyntax node)
        {
            // Conditional member bindings cannot be detached from their receiver. Leave that
            // arm intact; callers can explicitly TrackOpened around the whole expression.
            return node.WithExpression((ExpressionSyntax)Visit(node.Expression));
        }

        public override SyntaxNode VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var method = _model.GetSymbolInfo(node).Symbol as IMethodSymbol;
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node);
            var owner = method?.ContainingType.ToDisplayString();
            if (owner == "Autodesk.Revit.DB.Document" && method.Name == "Close"
                && method.Parameters.Length == 1 && method.Parameters[0].Type.SpecialType == SpecialType.System_Boolean
                && visited.Expression is MemberAccessExpressionSyntax member)
            {
                var arguments = visited.ArgumentList.Arguments.Insert(0, SyntaxFactory.Argument(member.Expression));
                return SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression("global::RvtMcp.Plugin.McpDocument.Close"),
                    SyntaxFactory.ArgumentList(arguments)).WithTriviaFrom(node);
            }
            if (method?.ReturnType.ToDisplayString() == "Autodesk.Revit.DB.Document"
                && ((owner == "Autodesk.Revit.ApplicationServices.Application"
                    && (method.Name == "OpenDocumentFile" || method.Name == "NewFamilyDocument" || method.Name == "NewProjectDocument"))
                    || (owner == "Autodesk.Revit.DB.Document" && method.Name == "EditFamily")))
                return SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression("global::RvtMcp.Plugin.McpDocument.TrackOpened"),
                    SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(visited.WithoutTrivia()))))
                    .WithTriviaFrom(node);
            return visited;
        }
    }
}
