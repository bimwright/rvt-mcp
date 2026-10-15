using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using RvtMcp.Plugin;
using RvtMcp.Plugin.Localization;
using Xunit;

namespace RvtMcp.Tests;

public sealed class McpAddinDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rvtmcp-coexistence-" + Guid.NewGuid().ToString("N"));
    private string Direct => Path.Combine(_root, "2027");
    private string Bundles => Path.Combine(_root, "ApplicationPlugins");

    public McpAddinDiscoveryTests() { Directory.CreateDirectory(Direct); Directory.CreateDirectory(Bundles); }

    private string Manifest(string name, string className, string id = null, string vendor = "OTHER", string directory = null, string assembly = null, bool clientId = false)
    {
        directory ??= Direct;
        Directory.CreateDirectory(directory);
        assembly ??= Path.Combine(directory, Guid.NewGuid().ToString("N") + ".dll");
        if (!File.Exists(assembly)) File.WriteAllText(assembly, "manifest-only fixture, never loaded");
        var file = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".addin");
        new XDocument(new XElement("RevitAddIns", new XElement("AddIn", new XAttribute("Type", "Application"),
            new XElement("Name", name), new XElement("FullClassName", className), new XElement("Assembly", assembly),
            new XElement(clientId ? "ClientId" : "AddInId", id ?? Guid.NewGuid().ToString()), new XElement("VendorId", vendor)))).Save(file);
        return file;
    }

    private System.Collections.Generic.IReadOnlyList<string> Scan(int year = 2027) => McpAddinDiscovery.Scan(year, new[] { Direct }, new[] { Bundles });

    [Fact]
    public void Own_plugin_and_official_Autodesk_MCP_do_not_trigger_coexistence()
    {
        Manifest("RvtMcp", "RvtMcp.Plugin.App", "{d50b0e16-5a4e-47e6-87ea-fa2890c758e3}");
        Manifest("Autodesk Revit Public MCP Server", "Autodesk.RevitMcpServer.Addin.Application", "92B7CFF4-4B9A-4F84-85EB-4024F8BA97F9", "ADSK", clientId: true);
        Assert.Empty(Scan());
    }

    [Fact]
    public void Exemption_is_specific_to_official_MCP_and_does_not_hide_another_MCP()
    {
        Manifest("Autodesk Revit Public MCP Server", "Autodesk.RevitMcpServer.Addin.Application", vendor: "ADSK");
        Manifest("Other MCP", "AnotherVendor.McpServer.Application", vendor: "ADSK");
        Assert.Equal(new[] { "Other MCP" }, Scan());
    }

    [Theory]
    [InlineData("Example MCP", "Vendor.Application")]
    [InlineData("Example", "Vendor.RevitMCPServer.Application")]
    [InlineData("Example", "Vendor.McpServer.Application")]
    [InlineData("Example", "Vendor.ModelContextProtocol.Application")]
    public void Recognizes_explicit_MCP_metadata(string name, string className)
    {
        Manifest(name, className);
        Assert.Single(Scan());
    }

    [Fact]
    public void Ordinary_names_and_missing_disabled_or_malformed_manifests_are_ignored()
    {
        Manifest("McPherson Tools", "McPherson.Application");
        var missing = Manifest("Missing MCP", "Vendor.McpServer.Application");
        var xml = XDocument.Load(missing);
        File.Delete(xml.Root!.Element("AddIn")!.Element("Assembly")!.Value);
        var disabled = Manifest("Disabled MCP", "Vendor.McpServer.Application");
        File.Move(disabled, disabled + ".disabled");
        File.WriteAllText(Path.Combine(Direct, "broken.addin"), "<broken");
        Assert.Empty(Scan());
    }

    [Fact]
    public void Duplicate_ids_and_multiple_commands_from_one_assembly_count_once()
    {
        var id = Guid.NewGuid().ToString();
        var first = Manifest("Other MCP", "Vendor.McpServer.Application", id);
        var assembly = XDocument.Load(first).Descendants("Assembly").Single().Value;
        Manifest("Other MCP", "Vendor.McpServer.Application", id);
        Manifest("MCP Command", "Vendor.McpServer.Command", assembly: assembly);
        Assert.Single(Scan());
    }

    [Fact]
    public void Bundles_respect_the_product_and_both_package_and_component_year_ranges()
    {
        var bundle = Path.Combine(Bundles, "OtherMcp.bundle");
        var manifest = Manifest("Bundled MCP", "Vendor.McpServer.Application", directory: Path.Combine(bundle, "Contents"));
        var package = new XDocument(new XElement("ApplicationPackage",
            new XElement("RuntimeRequirements", new XAttribute("Platform", "Revit"), new XAttribute("SeriesMin", "R2024"), new XAttribute("SeriesMax", "R2027")),
            new XElement("Components", new XElement("RuntimeRequirements", new XAttribute("SeriesMin", "R2027"), new XAttribute("SeriesMax", "R2027")),
                new XElement("ComponentEntry", new XAttribute("ModuleName", "./Contents/" + Path.GetFileName(manifest))))));
        var path = Path.Combine(bundle, "PackageContents.xml");
        package.Save(path);
        Assert.Single(Scan(2027));
        Assert.Empty(Scan(2024));
        Assert.Empty(Scan(2028));
        package.Root!.Element("RuntimeRequirements")!.SetAttributeValue("Platform", "AutoCAD");
        package.Save(path);
        Assert.Empty(Scan(2027));
    }

    [Fact]
    public void Relative_assembly_paths_and_duplicates_across_registration_roots_are_supported()
    {
        var id = Guid.NewGuid().ToString();
        var manifest = Manifest("Other MCP", "Vendor.McpServer.Application", id);
        var xml = XDocument.Load(manifest);
        var assembly = xml.Descendants("Assembly").Single();
        assembly.Value = Path.GetFileName(assembly.Value);
        xml.Save(manifest);
        var shared = Path.Combine(_root, "shared");
        Manifest("Other MCP", "Vendor.McpServer.Application", id, directory: shared);
        Assert.Single(McpAddinDiscovery.Scan(2027, new[] { Direct, shared }, new[] { Bundles }));
    }

    [Fact]
    public void External_XML_entities_are_not_resolved()
    {
        File.WriteAllText(Path.Combine(Direct, "entity.addin"), "<!DOCTYPE RevitAddIns [<!ENTITY x SYSTEM 'file:///missing'>]><RevitAddIns>&x;</RevitAddIns>");
        Assert.Empty(Scan());
    }

    [Fact]
    public void Every_ui_language_has_a_localized_notice_with_the_same_count_placeholder()
    {
        var english = EmbeddedCatalog.Load(typeof(McpAddinDiscoveryTests).Assembly, "en");
        foreach (var locale in LocaleResolver.SupportedLocales)
        {
            var catalog = EmbeddedCatalog.Load(typeof(McpAddinDiscoveryTests).Assembly, locale);
            foreach (var key in new[] { "mcp.coexistence.title", "mcp.coexistence.notice" })
            {
                Assert.True(catalog.ContainsKey(key) && !string.IsNullOrWhiteSpace(catalog[key]), locale + ":" + key);
                Assert.True(StringTable.ExtractTokens(english[key]).SetEquals(StringTable.ExtractTokens(catalog[key])));
            }
            var table = StringTable.Build(locale, english, catalog, null);
            Assert.DoesNotContain("{count", table.T("mcp.coexistence.notice", ("count", 2)));
        }
    }

    public void Dispose() => Directory.Delete(_root, true);
}
