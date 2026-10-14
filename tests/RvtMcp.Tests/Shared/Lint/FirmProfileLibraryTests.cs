using System.IO;
using RvtMcp.Plugin.Lint;
using Xunit;

namespace RvtMcp.Tests.Lint
{
    public class FirmProfileLibraryTests
    {
        [Fact]
        public void Empty_library_folder_returns_no_profiles()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "bimwright-test-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var lib = FirmProfileLibrary.LoadFrom(new[] { tempDir });
                Assert.Empty(lib.Profiles);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void Missing_library_folder_returns_no_profiles()
        {
            var nonExistent = Path.Combine(Path.GetTempPath(), "bimwright-does-not-exist-" + System.Guid.NewGuid().ToString("N"));
            var lib = FirmProfileLibrary.LoadFrom(new[] { nonExistent });
            Assert.Empty(lib.Profiles);
        }

        [Fact]
        public void Finds_profile_when_json_present()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "bimwright-test-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var json = @"{""id"":""test-profile"",""name"":""Test"",""description"":""x"",""matchHints"":[],""rules"":{""viewName"":""{NN}""}}";
                File.WriteAllText(Path.Combine(tempDir, "test.json"), json);
                var lib = FirmProfileLibrary.LoadFrom(new[] { tempDir });
                Assert.Single(lib.Profiles);
                Assert.Equal("test-profile", lib.Profiles[0].Id);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void Match_returns_null_when_library_empty()
        {
            var lib = new FirmProfileLibrary();
            var evidence = new FirmMatchEvidence
            {
                SheetPrefix = "ABC",
                ViewDominant = "L{NN}-{Name}",
                LevelPattern = "L{NN}"
            };
            Assert.Null(lib.Match(evidence));
        }
    }
}
