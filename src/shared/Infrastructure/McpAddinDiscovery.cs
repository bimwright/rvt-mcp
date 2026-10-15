using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace RvtMcp.Plugin
{
    /// <summary>Read installed manifests only; never load another vendor's assembly.</summary>
    public static class McpAddinDiscovery
    {
        private const string OwnId = "d50b0e16-5a4e-47e6-87ea-fa2890c758e3";
        private sealed class RegistrationGroup
        {
            public string Name;
            public readonly HashSet<Guid> Ids = new HashSet<Guid>();
            public readonly HashSet<string> Assemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public static IReadOnlyList<string> FindOtherMcpAddins(string revitYear, Action<string> log = null)
        {
            if (!int.TryParse(revitYear, out var year)) return new string[0];
            var user = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var shared = Environment.GetFolderPath(year >= 2027 ? Environment.SpecialFolder.ProgramFiles : Environment.SpecialFolder.CommonApplicationData);
            return Scan(year,
                new[] { Path.Combine(user, "Autodesk", "Revit", "Addins", revitYear), Path.Combine(shared, "Autodesk", "Revit", "Addins", revitYear) },
                new[] { Path.Combine(user, "Autodesk", "ApplicationPlugins"), Path.Combine(shared, "Autodesk", "ApplicationPlugins") }, log);
        }

        public static IReadOnlyList<string> Scan(int year, IEnumerable<string> manifestDirectories,
            IEnumerable<string> bundleDirectories, Action<string> log = null)
        {
            var manifests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in manifestDirectories)
                foreach (var file in Enumerate(directory, "*.addin", false, log)) manifests.Add(file);
            foreach (var directory in bundleDirectories)
                foreach (var bundle in Enumerate(directory, "*.bundle", true, log))
                {
                    var package = Read(Path.Combine(bundle, "PackageContents.xml"), log)?.Root;
                    if (package == null || !Compatible(package, year)) continue;
                    foreach (var components in package.Elements("Components").Where(c => Compatible(c, year)))
                        foreach (var entry in components.Elements("ComponentEntry").Where(c => Compatible(c, year)))
                        {
                            var module = (string)entry.Attribute("ModuleName");
                            if (string.IsNullOrWhiteSpace(module) || !module.EndsWith(".addin", StringComparison.OrdinalIgnoreCase)) continue;
                            try { manifests.Add(Path.GetFullPath(Path.Combine(bundle, module))); }
                            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { log?.Invoke("MCP manifest path skipped: " + ex.Message); }
                        }
                }

            var groups = new List<RegistrationGroup>();
            foreach (var manifest in manifests.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var root = Read(manifest, log)?.Root;
                if (root?.Name.LocalName != "RevitAddIns") continue;
                foreach (var addin in root.Elements("AddIn"))
                {
                    var name = ((string)addin.Element("Name") ?? (string)addin.Element("Text") ?? "").Trim();
                    var className = ((string)addin.Element("FullClassName") ?? "").Trim();
                    var assembly = ((string)addin.Element("Assembly") ?? "").Trim();
                    var idText = (string)addin.Element("AddInId") ?? (string)addin.Element("ClientId");
                    Guid.TryParse(idText, out var id);
                    if (id == Guid.Parse(OwnId) || className.StartsWith("RvtMcp.Plugin.", StringComparison.Ordinal)) continue;
                    // Autodesk's own MCP is deliberately exempt, including read/write preview variants.
                    if (string.Equals(((string)addin.Element("VendorId"))?.Trim(), "ADSK", StringComparison.OrdinalIgnoreCase)
                        && className.StartsWith("Autodesk.RevitMcpServer.", StringComparison.Ordinal)) continue;
                    try
                    {
                        if (string.IsNullOrWhiteSpace(assembly)) continue;
                        assembly = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifest), assembly));
                        if (!HasMcpName(name, className, Path.GetFileNameWithoutExtension(assembly)) || !File.Exists(assembly)) continue;
                        var matches = groups.Where(g => (id != Guid.Empty && g.Ids.Contains(id)) || g.Assemblies.Contains(assembly)).ToArray();
                        var group = matches.FirstOrDefault();
                        if (group == null) { group = new RegistrationGroup { Name = string.IsNullOrWhiteSpace(name) ? className : name }; groups.Add(group); }
                        if (id != Guid.Empty) group.Ids.Add(id);
                        group.Assemblies.Add(assembly);
                        foreach (var duplicate in matches.Skip(1))
                        {
                            group.Ids.UnionWith(duplicate.Ids);
                            group.Assemblies.UnionWith(duplicate.Assemblies);
                            groups.Remove(duplicate);
                        }
                    }
                    catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { log?.Invoke("MCP add-in entry skipped: " + ex.Message); }
                }
            }
            return groups.Select(g => g.Name).ToArray();
        }

        private static bool HasMcpName(params string[] values)
        {
            foreach (var value in values)
            {
                if (value.IndexOf("ModelContextProtocol", StringComparison.OrdinalIgnoreCase) >= 0
                    || value.IndexOf("revitmcp", StringComparison.OrdinalIgnoreCase) >= 0
                    || value.IndexOf("mcprevit", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                // Split CamelCase as well as punctuation: McpServer matches, McPherson does not.
                var words = Regex.Replace(value, @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
                if (Regex.Split(words, @"[^a-zA-Z0-9]+").Any(w => string.Equals(w, "mcp", StringComparison.OrdinalIgnoreCase))) return true;
            }
            return false;
        }

        private static bool Compatible(XElement element, int year)
        {
            foreach (var requirement in element.Elements("RuntimeRequirements"))
            {
                var platform = (string)requirement.Attribute("Platform");
                if (!string.IsNullOrEmpty(platform) && platform.IndexOf("Revit", StringComparison.OrdinalIgnoreCase) < 0) return false;
                var os = (string)requirement.Attribute("OS");
                if (!string.IsNullOrEmpty(os) && os.IndexOf("Win", StringComparison.OrdinalIgnoreCase) < 0) return false;
                foreach (var bound in new[] { "SeriesMin", "SeriesMax" })
                {
                    var text = (string)requirement.Attribute(bound);
                    if (string.IsNullOrEmpty(text)) continue;
                    if (!int.TryParse(text.TrimStart('R', 'r'), out var limit)) return false;
                    if (bound == "SeriesMin" ? year < limit : year > limit) return false;
                }
            }
            return true;
        }

        private static IEnumerable<string> Enumerate(string directory, string pattern, bool directories, Action<string> log)
        {
            try { return !Directory.Exists(directory) ? new string[0] : directories ? Directory.GetDirectories(directory, pattern) : Directory.GetFiles(directory, pattern); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { log?.Invoke("MCP add-in directory skipped: " + ex.Message); return new string[0]; }
        }

        private static XDocument Read(string path, Action<string> log)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 }))
                    return XDocument.Load(reader);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is XmlException) { log?.Invoke("MCP manifest skipped: " + ex.Message); return null; }
        }
    }
}
