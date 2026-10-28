using RvtMcp.Plugin.Reports.Model;
using RvtMcp.Plugin.Reports.Presentation;
using Xunit;

namespace RvtMcp.Tests.Shared.Reports
{
    public class ReportDocumentMatchTests
    {
        [Fact]
        public void Matches_by_token_or_non_null_model_key_using_ordinal_comparison()
        {
            var doc = new ReportDocument { Token = "token-a", ModelKey = "model-a" };
            Assert.True(ReportDocumentMatch.Matches(doc, new ActiveDocumentIdentity { Token = "token-a", ModelKey = "different" }));
            Assert.True(ReportDocumentMatch.Matches(doc, new ActiveDocumentIdentity { Token = "other", ModelKey = "model-a" }));
            Assert.False(ReportDocumentMatch.Matches(doc, new ActiveDocumentIdentity { Token = "TOKEN-A", ModelKey = "MODEL-A" }));
        }

        [Fact]
        public void Missing_identity_does_not_match()
        {
            Assert.False(ReportDocumentMatch.Matches(null, new ActiveDocumentIdentity()));
            Assert.False(ReportDocumentMatch.Matches(new ReportDocument(), null));
            Assert.False(ReportDocumentMatch.Matches(new ReportDocument(), new ActiveDocumentIdentity()));
        }
    }
}
