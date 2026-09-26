using System;
using System.Reflection;

namespace RvtMcp.Plugin.Views.Settings
{
    public sealed class AboutInfo
    {
        public string ProductName { get; set; }
        public string PluginInformationalVersion { get; set; }
        public string RevitYear { get; set; }
        public string LicenseId { get; set; }
        public string LicenseSourceUrl { get; set; }
        public string RepositoryUrl { get; set; }
        public string DocumentationUrl { get; set; }
        public string IssuesUrl { get; set; }
        public string Author { get; set; }
        public string Copyright { get; set; }
    }

    public static class AboutInfoProvider
    {
        public static AboutInfo Create(Assembly assembly, string revitYear)
        {
            assembly = assembly ?? typeof(AboutInfoProvider).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(version))
                version = assembly.GetName().Version?.ToString() ?? "0.0.0";

            var repository = "https://github.com/bimwright/rvt-mcp";
            return new AboutInfo
            {
                ProductName = "rvt-mcp",
                PluginInformationalVersion = version,
                RevitYear = revitYear ?? "—",
                LicenseId = "Apache-2.0",
                LicenseSourceUrl = CreateLicenseSourceUrl(version),
                RepositoryUrl = repository,
                DocumentationUrl = repository + "/tree/" + CreateReference(version) + "/docs",
                IssuesUrl = repository + "/issues",
                Author = "Khoa Le",
                Copyright = "Copyright 2026 Khoa Le",
            };
        }

        public static string CreateLicenseSourceUrl(string informationalVersion)
        {
            var version = string.IsNullOrWhiteSpace(informationalVersion) ? "0.0.0" : informationalVersion.Trim();
            return "https://github.com/bimwright/rvt-mcp/blob/" + CreateReference(version) + "/LICENSE";
        }

        private static string CreateReference(string version)
        {
            var plus = version.IndexOf('+');
            return plus > 0 && plus + 1 < version.Length
                ? version.Substring(plus + 1)
                : "v" + version;
        }
    }

    public static class LicenseProvider
    {
        public const string ResourceName = "RvtMcp.LICENSE";

        public static string Load(Assembly assembly)
        {
            assembly = assembly ?? typeof(LicenseProvider).Assembly;
            using (var stream = assembly.GetManifestResourceStream(ResourceName))
            {
                if (stream == null) return null;
                using (var reader = new System.IO.StreamReader(stream))
                    return reader.ReadToEnd();
            }
        }
    }
}
