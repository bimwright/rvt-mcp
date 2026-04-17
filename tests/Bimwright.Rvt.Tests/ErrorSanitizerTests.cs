using Bimwright.Rvt.Plugin;
using Xunit;

namespace Bimwright.Rvt.Tests
{
    // Path-mask regression cases:
    //   Input                                                              → Output
    //   Could not find 'D:\Workspace\my-project\config.json'               → Could not find 'config.json'
    //   in D:\...\CreateLevelHandler.cs:line 45                            → in CreateLevelHandler.cs:line 45
    //   Document 'C:\Users\alice\Building-X.rvt' is null                   → Document 'Building-X.rvt' is null
    //   Could not open 'D:\client-projects\XYZ\project.db'                 → Could not open 'project.db'
    public class ErrorSanitizerTests
    {
        [Theory]
        [InlineData(
            @"Could not find 'D:\Workspace\my-project\config.json'",
            "Could not find 'config.json'")]
        [InlineData(
            @"in D:\Projects\bimwright\src\shared\Handlers\CreateLevelHandler.cs:line 45",
            "in CreateLevelHandler.cs:line 45")]
        [InlineData(
            @"Document 'C:\Users\alice\Building-X.rvt' is null",
            "Document 'Building-X.rvt' is null")]
        [InlineData(
            @"Could not open 'D:\client-projects\XYZ\project.db'",
            "Could not open 'project.db'")]
        public void Sanitize_StripsWindowsAbsolutePaths_KeepsFilename(string input, string expected)
        {
            var actual = ErrorSanitizer.Sanitize(input);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void Sanitize_NullOrEmpty_ReturnsSameInstance()
        {
            Assert.Null(ErrorSanitizer.Sanitize(null));
            Assert.Equal(string.Empty, ErrorSanitizer.Sanitize(string.Empty));
        }

        [Fact]
        public void Sanitize_UncPath_StripsToFilename()
        {
            var input = @"Access denied on \\buildshare\release\2026\plugin.dll";
            var actual = ErrorSanitizer.Sanitize(input);
            Assert.Equal("Access denied on plugin.dll", actual);
        }

        [Fact]
        public void Sanitize_UnixHomePath_StripsToFilename()
        {
            var input = "Could not read /home/alice/src/config.yaml";
            var actual = ErrorSanitizer.Sanitize(input);
            Assert.Equal("Could not read config.yaml", actual);
        }

        [Fact]
        public void Sanitize_NoPath_ReturnsUnchanged()
        {
            var input = "Element with id 12345 not found in document.";
            var actual = ErrorSanitizer.Sanitize(input);
            Assert.Equal(input, actual);
        }
    }
}
