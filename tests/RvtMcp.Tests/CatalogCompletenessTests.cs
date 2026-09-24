using System.Collections.Generic;
using System.IO;
using System.Linq;
using RvtMcp.Plugin.Localization;
using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>Strict catalog checks: every shipped strings.&lt;locale&gt;.json
    /// embedded in this assembly has exactly the English key set, non-empty
    /// values, and identical placeholder tokens (name + :n flag).</summary>
    public class CatalogCompletenessTests
    {
        private static readonly string[] ExpectedLocales =
            LocaleResolver.SupportedLocales.Where(c => c != "en" && c != "auto").ToArray();

        private static IReadOnlyDictionary<string, string> En() =>
            EmbeddedCatalog.Load(typeof(CatalogCompletenessTests).Assembly, "en");

        [Fact]
        public void Every_supported_locale_has_an_embedded_catalog()
        {
            foreach (var code in LocaleResolver.SupportedLocales)
            {
                if (code == "auto") continue;
                var catalog = EmbeddedCatalog.Load(typeof(CatalogCompletenessTests).Assembly, code);
                Assert.True(catalog != null, $"missing embedded catalog for {code}");
                Assert.True(catalog.Count > 0, $"empty embedded catalog for {code}");
            }
        }

        [Fact]
        public void Catalogs_have_exactly_the_english_key_set()
        {
            var en = En();
            foreach (var code in ExpectedLocales)
            {
                var cat = EmbeddedCatalog.Load(typeof(CatalogCompletenessTests).Assembly, code);
                Assert.True(cat != null, $"missing catalog {code}");
                var missing = en.Keys.Where(k => !cat.ContainsKey(k)).OrderBy(k => k).ToList();
                var extra = cat.Keys.Where(k => !en.ContainsKey(k)).OrderBy(k => k).ToList();
                Assert.True(missing.Count == 0, $"{code} missing keys: {string.Join(", ", missing)}");
                Assert.True(extra.Count == 0, $"{code} extra keys: {string.Join(", ", extra)}");
            }
        }

        [Fact]
        public void Catalog_values_are_non_empty_and_preserve_placeholders()
        {
            var en = En();
            foreach (var code in ExpectedLocales)
            {
                var cat = EmbeddedCatalog.Load(typeof(CatalogCompletenessTests).Assembly, code);
                foreach (var kv in en)
                {
                    if (!cat.TryGetValue(kv.Key, out var value))
                        continue; // covered by the key-set test
                    Assert.False(string.IsNullOrWhiteSpace(value), $"{code}:{kv.Key} is empty");
                    var enTokens = StringTable.ExtractTokens(kv.Value);
                    var tokens = StringTable.ExtractTokens(value);
                    Assert.True(enTokens.SetEquals(tokens),
                        $"{code}:{kv.Key} placeholders {string.Join(",", tokens)} != en {string.Join(",", enTokens)}");
                }
            }
        }

        [Fact]
        public void Catalog_meta_locale_matches_filename()
        {
            var asm = typeof(CatalogCompletenessTests).Assembly;
            foreach (var code in LocaleResolver.SupportedLocales)
            {
                if (code == "auto") continue;
                using (var stream = asm.GetManifestResourceStream(EmbeddedCatalog.ResourceName(code)))
                {
                    Assert.True(stream != null, $"missing resource for {code}");
                    var root = Newtonsoft.Json.Linq.JObject.Parse(new StreamReader(stream).ReadToEnd());
                    Assert.Equal(code, (string)root["_meta"]?["locale"]);
                }
            }
        }

        [Fact]
        public void Security_keys_exist_and_stay_recognizable_in_every_catalog()
        {
            var en = En();
            var securityKeys = en.Keys.Where(k => k.StartsWith("security.", System.StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(securityKeys);
            foreach (var code in ExpectedLocales)
            {
                var cat = EmbeddedCatalog.Load(typeof(CatalogCompletenessTests).Assembly, code);
                foreach (var key in securityKeys)
                {
                    Assert.True(cat.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value),
                        $"{code}:{key} missing or empty");
                    // send_code stays an ASCII identifier; redacted params carry code_hash.
                    if (key == "security.send_code.redacted_summary")
                    {
                        Assert.Contains("code_hash", value);
                        Assert.Contains("code_length", value);
                    }
                }
            }
        }
    }
}
