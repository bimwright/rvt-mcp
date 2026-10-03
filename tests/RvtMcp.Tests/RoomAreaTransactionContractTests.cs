using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace RvtMcp.Tests
{
    // Native failure processing still requires Revit. These guard its opt-in at
    // the two handlers that opened modal dialogs in the release benchmark.
    public class RoomAreaTransactionContractTests
    {
        [Theory]
        [InlineData("CreateRoomHandler.cs")]
        [InlineData("CreateAreaHandler.cs")]
        public void Failure_preprocessor_is_attached_before_creating_elements(string file)
        {
            var root = CSharpSyntaxTree.ParseText(ReadHandler(file)).GetRoot();
            var attach = Assert.Single(root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(node => node.Expression.ToString().EndsWith("SetFailuresPreprocessor")));
            Assert.Contains("SetClearAfterRollback(true)", attach.Parent.ToString());
            var create = Assert.Single(root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(node => node.Expression.ToString() == (file == "CreateRoomHandler.cs" ? "doc.Create.NewRoom" : "doc.Create.NewArea")));
            Assert.True(attach.SpanStart < create.SpanStart);
            Assert.Contains(root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>(),
                node => node.Type.ToString() == "SafeFailuresPreprocessor");
        }

        [Fact]
        public void Room_rollback_is_reported_before_accessing_created_room_after_commit()
        {
            var root = CSharpSyntaxTree.ParseText(ReadHandler("CreateRoomHandler.cs")).GetRoot();
            var guard = Assert.Single(root.DescendantNodes().OfType<IfStatementSyntax>()
                .Where(node => node.Condition.ToString() == "commitStatus != TransactionStatus.Committed"));
            Assert.Contains(guard.DescendantNodes().OfType<ReturnStatementSyntax>(),
                node => node.Expression.ToString().StartsWith("CommandResult.Fail("));
            var success = Assert.Single(root.DescendantNodes().OfType<ReturnStatementSyntax>()
                .Where(node => node.Expression.ToString().StartsWith("CommandResult.Ok(")));
            Assert.True(guard.SpanStart < success.SpanStart);
            Assert.Contains("failures.Messages", success.ToString());
        }

        private static string ReadHandler(string name, [CallerFilePath] string testFile = "")
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));
            return File.ReadAllText(Path.Combine(root, "src", "shared", "Handlers", name));
        }
    }
}
