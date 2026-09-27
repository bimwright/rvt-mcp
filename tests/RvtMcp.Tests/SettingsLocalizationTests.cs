using System.Collections.Generic;
using System.Linq;
using RvtMcp.Plugin.Localization;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class SettingsLocalizationTests
    {
        private static readonly string[] RequiredKeys =
        {
            "ribbon.settings.text", "ribbon.settings.tooltip", "ribbon.language.tooltip",
            "settings.window.title", "settings.tab.general", "settings.tab.toast", "settings.tab.about",
            "settings.general.connection", "settings.general.revit", "settings.general.transport",
            "settings.general.port", "settings.general.state", "settings.general.privacy", "settings.general.copy",
            "settings.general.state.listenerStopped", "settings.general.state.connected",
            "settings.general.state.waiting", "settings.general.notApplicable",
            "settings.general.cacheBodies", "settings.general.keepJournal", "settings.general.journalDuration",
            "settings.toast.heading", "settings.toast.enabled", "settings.toast.idle", "settings.toast.help",
            "settings.toast.brand", "settings.toast.brand.help",
            "settings.about.description", "settings.about.hint", "settings.about.product", "settings.about.version",
            "settings.about.revit", "settings.about.author", "settings.about.license", "settings.about.copyright",
            "settings.about.viewLicense", "settings.about.github", "settings.about.docs", "settings.about.issues",
            "settings.license.title", "settings.license.summary", "settings.license.disclaimer",
            "settings.license.openGithub", "settings.license.close", "settings.license.loadError",
            "settings.tools.refresh", "settings.tools.no", "settings.tools.name", "settings.tools.description",
            "settings.tools.source", "settings.tools.timeout", "settings.tools.help", "settings.tools.counts",
            "settings.tools.fallback", "settings.tools.source.builtIn", "settings.tools.source.baked",
            "settings.tools.status.notConnected", "settings.tools.status.noCatalog", "settings.tools.status.invalid",
            "settings.tools.status.empty", "settings.tools.status.label", "settings.tools.timeout.unavailable",
            "settings.tools.timeout.serverLocal", "settings.tools.timeout.budget", "settings.apply", "settings.cancel", "settings.close",
            "settings.footer.copied", "settings.footer.portUnavailable", "settings.footer.applied",
            "settings.footer.journalExpired", "settings.footer.saveFailed",
            "settings.footer.discardPrompt", "settings.header.subtitle", "settings.discard", "settings.footer.unsaved",
            "settings.general.cacheBodies.help", "settings.general.keepJournal.help", "settings.toast.description",
            "settings.toast.enabled.help", "settings.toast.idle.help", "settings.general.listener",
            "settings.general.restart", "settings.general.listenerFailed"
        };

        [Fact]
        public void Every_supported_locale_contains_all_settings_and_ribbon_keys()
        {
            foreach (var locale in LocaleResolver.SupportedLocales)
            {
                var catalog = EmbeddedCatalog.Load(typeof(SettingsLocalizationTests).Assembly, locale);
                Assert.NotNull(catalog);
                foreach (var key in RequiredKeys)
                    Assert.True(catalog.ContainsKey(key) && !string.IsNullOrWhiteSpace(catalog[key]), locale + ":" + key);
            }
        }

        [Fact]
        public void New_ui_keys_preserve_placeholders_and_bounded_scalar_length()
        {
            var english = EmbeddedCatalog.Load(typeof(SettingsLocalizationTests).Assembly, "en");
            foreach (var locale in LocaleResolver.SupportedLocales)
            {
                var catalog = EmbeddedCatalog.Load(typeof(SettingsLocalizationTests).Assembly, locale);
                foreach (var key in RequiredKeys)
                {
                    var value = catalog[key];
                    Assert.True(ToolSummaryCatalog.ScalarLength(value) <= 160, locale + ":" + key + " exceeds 160 scalars");
                    Assert.True(StringTable.ExtractTokens(english[key]).SetEquals(StringTable.ExtractTokens(value)),
                        locale + ":" + key + " placeholder mismatch");
                }
            }
        }
    }
}
