using RvtMcp.Plugin.Localization;
using Xunit;

namespace RvtMcp.Tests
{
    public class LocaleResolverTests
    {
        [Theory]
        [InlineData(null, "auto")]
        [InlineData("", "auto")]
        [InlineData("  ", "auto")]
        [InlineData("vi", "auto")]        // not offered by Revit — invalid, not a locale
        [InlineData("pt-br", "auto")]     // case-sensitive
        [InlineData("en-US", "auto")]
        [InlineData("auto", "auto")]
        [InlineData("pt-BR", "pt-BR")]
        [InlineData("zh-CN", "zh-CN")]
        [InlineData("ja", "ja")]
        [InlineData("en", "en")]
        public void NormalizeCode_ClosedSet(string raw, string expected)
        {
            Assert.Equal(expected, LocaleResolver.NormalizeCode(raw));
        }

        [Theory]
        [InlineData("English_USA", "en")]
        [InlineData("English_GB", "en")]
        [InlineData("German", "de")]
        [InlineData("Spanish", "es")]
        [InlineData("French", "fr")]
        [InlineData("Italian", "it")]
        [InlineData("Dutch", "nl")]
        [InlineData("Chinese_Simplified", "zh-CN")]
        [InlineData("Chinese_Traditional", "zh-TW")]
        [InlineData("Japanese", "ja")]
        [InlineData("Korean", "ko")]
        [InlineData("Russian", "ru")]
        [InlineData("Czech", "cs")]
        [InlineData("Polish", "pl")]
        [InlineData("Hungarian", "hu")]
        [InlineData("Brazilian_Portuguese", "pt-BR")]
        [InlineData("Unknown", "en")]
        [InlineData("Garbage", "en")]
        [InlineData(null, "en")]
        public void ResolveLocale_AutoMapsEnumNames(string revitName, string expected)
        {
            Assert.Equal(expected, LocaleResolver.ResolveLocale(revitName, "auto"));
        }

        [Fact]
        public void ResolveLocale_ExplicitCodeWinsOverRevit()
        {
            Assert.Equal("ja", LocaleResolver.ResolveLocale("German", "ja"));
        }

        [Fact]
        public void ResolveLocale_UnnormalizedCodeFallsBackToAutoMapping()
        {
            Assert.Equal("de", LocaleResolver.ResolveLocale("German", "pt-br"));
        }

        [Fact]
        public void SupportedLocales_Has15ShippedCatalogs()
        {
            Assert.Equal(15, LocaleResolver.SupportedLocales.Count);
        }

        [Fact]
        public void EverySupportedLocale_HasNativeName()
        {
            foreach (var locale in LocaleResolver.SupportedLocales)
            {
                var name = LocaleResolver.NativeName(locale);
                Assert.False(string.IsNullOrEmpty(name));
                Assert.NotEqual(locale, name);   // resolved to a real display name
            }
            Assert.Equal(15, LocaleResolver.NativeNames.Count);
        }
    }
}
