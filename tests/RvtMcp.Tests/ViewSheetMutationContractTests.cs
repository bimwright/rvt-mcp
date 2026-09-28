using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace RvtMcp.Tests
{
    // Source contracts only: exercising Autodesk transaction/parameter behavior still
    // requires a live Revit smoke test. These prevent the reviewed guards regressing.
    public class ViewSheetMutationContractTests
    {
        [Fact]
        public void Sheet_copy_skips_identity_parameters_before_copying_values()
        {
            var source = ReadHandler("DuplicateSheetHandler.cs");
            var root = CSharpSyntaxTree.ParseText(source).GetRoot();
            var loop = Assert.Single(root.DescendantNodes().OfType<ForEachStatementSyntax>()
                .Where(node => node.Identifier.ValueText == "param"));
            var guard = Assert.Single(loop.DescendantNodes().OfType<IfStatementSyntax>()
                .Where(node => node.Condition.ToString().Contains("BuiltInParameter.SHEET_NUMBER")));

            Assert.Equal(
                "builtIn == BuiltInParameter.SHEET_NUMBER || builtIn == BuiltInParameter.SHEET_NAME",
                guard.Condition.ToString());
            Assert.IsType<ContinueStatementSyntax>(guard.Statement);
            var copy = Assert.Single(loop.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(node => node.Expression.ToString() == "CopyParameterValue"));
            Assert.True(guard.SpanStart < copy.SpanStart);
        }

        [Fact]
        public void Scale_uses_the_view_property_not_the_readonly_parameter()
        {
            var root = CSharpSyntaxTree.ParseText(ReadHandler("SetViewScaleHandler.cs")).GetRoot();
            Assert.Contains(root.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
                node => node.Left.ToString() == "view.Scale" && node.Right.ToString() == "scale.Value");
            Assert.DoesNotContain(root.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                node => node.Expression.ToString() == "view.get_Parameter");
        }

        [Theory]
        [InlineData("DuplicateSheetHandler.cs", "commitStatus")]
        [InlineData("SetViewScaleHandler.cs", "status")]
        public void Uncommitted_transactions_and_exceptions_do_not_report_success(string file, string status)
        {
            var root = CSharpSyntaxTree.ParseText(ReadHandler(file)).GetRoot();
            var commit = Assert.Single(root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Where(node => node.Initializer?.Value.ToString() == "tx.Commit()"));
            Assert.Equal(status, commit.Identifier.ValueText);
            var guard = Assert.Single(root.DescendantNodes().OfType<IfStatementSyntax>()
                .Where(node => node.Condition.ToString() == $"{status} != TransactionStatus.Committed"));
            var failure = Assert.Single(guard.DescendantNodes().OfType<ReturnStatementSyntax>());
            Assert.StartsWith("CommandResult.Fail(", failure.Expression.ToString());

            var success = Assert.Single(root.DescendantNodes().OfType<ReturnStatementSyntax>()
                .Where(node => node.Expression?.ToString().StartsWith("CommandResult.Ok(", StringComparison.Ordinal) == true));
            Assert.True(commit.SpanStart < guard.SpanStart && guard.SpanStart < success.SpanStart);
            Assert.DoesNotContain(success.Ancestors(), node => node is CatchClauseSyntax);
        }

        private static string ReadHandler(string name, [CallerFilePath] string testFile = "")
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));
            return File.ReadAllText(Path.Combine(root, "src", "shared", "Handlers", name));
        }
    }
}
