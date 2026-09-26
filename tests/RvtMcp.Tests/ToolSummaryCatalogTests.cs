using System;
using System.Reflection;
using RvtMcp.Plugin.Localization;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class ToolSummaryCatalogTests
    {
        [Fact]
        public void AllSupportedLocalesHaveTheSameBoundedSummaryKeys()
        {
            var errors = ToolSummaryCatalog.ValidateCompleteness(
                typeof(ToolSummaryCatalogTests).Assembly,
                LocaleResolver.SupportedLocales);
            Assert.Empty(errors);
        }

        [Fact]
        public void MissingLocaleEntryFallsBackToEnglishThenCatalogDescription()
        {
            var assembly = typeof(ToolSummaryCatalogTests).Assembly;
            var english = ToolSummaryCatalog.Load(assembly, "en");
            Assert.True(english.TryGet("revit_get_current_view_info", out var summary));
            Assert.Equal(summary, ToolSummaryCatalog.Resolve(assembly, "hu", "revit_get_current_view_info", "fallback"));
            Assert.Equal("fallback", ToolSummaryCatalog.Resolve(assembly, "hu", "unknown_tool", "fallback"));
            var resolution = ToolSummaryCatalog.ResolveWithFallback(assembly, "hu", "unknown_tool", "fallback");
            Assert.True(resolution.IsFallback);
            Assert.Equal("fallback", resolution.Text);
        }

        [Fact]
        public void SummaryScalarLengthCountsAstralCharactersOnce()
        {
            Assert.Equal(2, ToolSummaryCatalog.ScalarLength("A😀"));
        }
    }
}
