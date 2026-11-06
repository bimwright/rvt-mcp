using System;
using RvtMcp.Plugin.Documents;
using Xunit;

namespace RvtMcp.Tests.Shared.Documents
{
    public class DocumentModeClassifierTests
    {
        [Theory]
        [InlineData(false, false, false, @"C:\Models\tower.rvt", null, "local_file")]
        [InlineData(false, false, false, @"C:\Models\template.rte", null, "template")]
        [InlineData(false, false, false, @"C:\Models\Template.RTE", null, "template")]
        [InlineData(false, true, false, @"C:\Models\chair.rfa", null, "family")]
        [InlineData(false, false, true, @"C:\Models\tower-local.rvt", @"C:\Models\tower.rvt", "workshared_local")]
        [InlineData(false, false, true, @"C:\Models\Tower.rvt", @"c:\models\tower.rvt", "workshared_central")]
        [InlineData(true, false, true, "", null, "cloud")]
        [InlineData(false, true, false, "", null, "unsaved")]
        public void Classifies_modes_from_document_flags(bool cloud, bool family, bool workshared,
            string path, string centralPath, string expected)
        {
            Assert.Equal(expected, DocumentModeClassifier.Classify(cloud, family, workshared, path, centralPath));
        }

        [Fact]
        public void Workshared_document_with_unknown_central_path_is_not_assumed_local()
        {
            Assert.Throws<ArgumentException>(() =>
                DocumentModeClassifier.Classify(false, false, true, @"C:\Models\tower.rvt", null));
        }

        [Theory]
        // A local fresh from CreateNewLocal: header still says central, stored central path is elsewhere.
        [InlineData(true, true, @"C:\Work\tower_user.rvt", @"\\server\proj\tower.rvt", false)]
        [InlineData(true, false, @"C:\Work\tower_user.rvt", @"\\server\proj\tower.rvt", false)]
        // The central itself, path compared case-insensitively.
        [InlineData(true, true, @"C:\Models\Tower.rvt", @"c:\models\tower.rvt", true)]
        // No central path known: keep the header's answer.
        [InlineData(true, true, @"C:\Models\tower.rvt", null, true)]
        [InlineData(true, false, @"C:\Models\tower.rvt", "", false)]
        // Not workshared: never a central, whatever the header flag says.
        [InlineData(false, true, @"C:\Models\tower.rvt", @"C:\Models\tower.rvt", false)]
        public void Central_file_is_decided_by_stored_central_path_not_header_flag(bool workshared, bool headerIsCentral,
            string path, string centralPath, bool expected)
        {
            Assert.Equal(expected, DocumentModeClassifier.IsCentralFile(workshared, headerIsCentral, path, centralPath));
        }
    }
}
