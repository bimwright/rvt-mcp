using System;
using System.Reflection;
using System.Linq;
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

        [Fact]
        public void IfcSummaryMatchesGatewayDefaultWait()
        {
            var method = typeof(RvtMcp.Server.ExportTools).GetMethod("ExportIfc");
            var timeout = Assert.IsType<int>(method.GetParameters().Single(p => p.Name == "timeout_seconds").DefaultValue);
            foreach (var locale in LocaleResolver.SupportedLocales)
            {
                var catalog = ToolSummaryCatalog.Load(typeof(ToolSummaryCatalogTests).Assembly, locale);
                Assert.True(catalog.TryGet("revit_export_ifc", out var summary), locale);
                Assert.Contains($"default {timeout}s", summary);
                Assert.DoesNotContain("60s timeout", summary);
            }
        }
    }
}
