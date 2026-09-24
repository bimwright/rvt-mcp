using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace RvtMcp.Plugin.Localization
{
    /// <summary>
    /// Loads the shipped catalog embedded in the plugin assembly. Each shell embeds
    /// <c>src/shared/Localization/Catalog/strings.&lt;locale&gt;.json</c> with
    /// LogicalName <c>RvtMcp.Localization.%(Filename)%(Extension)</c>, so the resource
    /// name varies by filename (spec §5.2). Never throws — a missing/broken catalog
    /// returns null and the table falls back to English (spec §4.1).
    /// </summary>
    public static class EmbeddedCatalog
    {
        public const string ResourcePrefix = "RvtMcp.Localization.strings.";

        public static string ResourceName(string locale) => ResourcePrefix + locale + ".json";

        /// <summary>Returns null when the locale catalog is not embedded or malformed.</summary>
        public static IReadOnlyDictionary<string, string> Load(Assembly assembly, string locale)
        {
            if (assembly == null || string.IsNullOrEmpty(locale)) return null;
            try
            {
                using (var stream = assembly.GetManifestResourceStream(ResourceName(locale)))
                {
                    if (stream == null) return null;
                    using (var reader = new StreamReader(stream))
                        return StringTable.ParseCatalog(reader.ReadToEnd());
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
