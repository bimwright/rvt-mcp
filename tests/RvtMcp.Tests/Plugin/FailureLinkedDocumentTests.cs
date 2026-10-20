using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace RvtMcp.Tests
{
    public class FailureLinkedDocumentTests
    {
        // RefreshContext needs a live Revit session, so the filter is checked in the source.
        [Fact]
        public void RefreshContext_skips_linked_documents_before_resolving_a_ledger_model()
        {
            var source = File.ReadAllText(Path.Combine(GetRepoRoot(), "src", "shared", "Failures", "FailureControl.cs"));
            var loop = source.IndexOf("foreach (Document document in app.Application.Documents)", System.StringComparison.Ordinal);
            Assert.True(loop >= 0, "RefreshContext no longer enumerates open documents");
            var skip = source.IndexOf("|| document.IsLinked) continue;", loop, System.StringComparison.Ordinal);
            var resolve = source.IndexOf("openState = State(document);", loop, System.StringComparison.Ordinal);
            Assert.True(skip > loop && resolve > skip, "linked documents must be skipped before State(document) writes a model row");
        }

        private static string GetRepoRoot([CallerFilePath] string testFile = "")
        {
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", ".."));
        }
    }
}
