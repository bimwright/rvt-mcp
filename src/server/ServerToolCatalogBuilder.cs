using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using ModelContextProtocol.Server;
using RvtMcp.Plugin;
using RvtMcp.ToolCatalog;

namespace RvtMcp.Server
{
    /// <summary>
    /// Builds the Settings Tools catalog from the exact tool classes passed to the
    /// MCP registration pipeline. Reflection is intentionally limited to names,
    /// toolset markers, and short descriptions; parameter schemas and method bodies
    /// never enter the catalog.
    /// </summary>
    internal static class ServerToolCatalogBuilder
    {
        private const int TransportGraceSeconds = 5;
        private const int FixedTimeoutSeconds = 60;
        private const int LongRunDefaultSeconds = 600;
        private const int LongRunMinimumSeconds = 1;
        private const int LongRunMaximumSeconds = 900;

        private static readonly HashSet<string> LongRunTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "load_family_from_path",
            "open_model",
            "link_revit_model",
            "reload_link"
        };

        private static readonly HashSet<string> ServerLocalTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "list_available_targets",
            "get_current_target",
            "switch_target",
            "list_bake_suggestions",
            "dismiss_bake_suggestion",
            "analyze_usage_patterns"
        };

        internal static RvtMcp.ToolCatalog.ToolCatalog Build(HashSet<string> enabled, RvtMcpConfig config)
        {
            if (enabled == null) throw new ArgumentNullException(nameof(enabled));

            var activeTypes = Program.ResolveRegisteredToolTypes(enabled, config);
            var allTypes = ResolveFullRegistryTypes();
            var entries = Describe(activeTypes);
            var duplicate = entries
                .GroupBy(entry => entry.McpName, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate != null)
                throw new InvalidOperationException("Duplicate canonical MCP tool name: " + duplicate.Key);

            var cataloguedCount = CountToolMethods(allTypes);

            return new RvtMcp.ToolCatalog.ToolCatalog(
                RvtMcp.ToolCatalog.ToolCatalog.SupportedSchemaVersion,
                GetServerVersion(),
                cataloguedCount,
                entries);
        }

        internal static IReadOnlyList<ToolCatalogEntry> Describe(IEnumerable<Type> toolTypes)
        {
            if (toolTypes == null) return Array.Empty<ToolCatalogEntry>();

            var entries = new List<ToolCatalogEntry>();
            foreach (var type in toolTypes.Where(t => t != null).Distinct())
            {
                var toolset = type.GetCustomAttribute<ToolsetAttribute>()?.Name;
                if (string.IsNullOrWhiteSpace(toolset))
                    continue;

                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                              BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                foreach (var method in methods)
                {
                    var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
                    if (attribute == null) continue;

                    var name = string.IsNullOrWhiteSpace(attribute.Name) ? method.Name : attribute.Name;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
                    description = ToShortDescription(description);
                    var timeout = BuildTimeout(name);

                    entries.Add(new ToolCatalogEntry(
                        name,
                        toolset,
                        description,
                        summaryKey: null,
                        timeout));
                }
            }

            return entries
                .OrderBy(e => e.McpName, StringComparer.Ordinal)
                .ToArray();
        }

        private static int CountToolMethods(IEnumerable<Type> toolTypes)
        {
            return (toolTypes ?? Array.Empty<Type>())
                .Where(type => type != null)
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                .Count(method => method.GetCustomAttribute<McpServerToolAttribute>() != null);
        }

        private static IReadOnlyList<Type> ResolveFullRegistryTypes()
        {
            var config = new RvtMcpConfig
            {
                Toolsets = ToolsetFilter.KnownToolsets.ToList(),
                ReadOnly = false,
                EnableToolbaker = true,
                EnableAdaptiveBake = true
            };
            return Program.ResolveRegisteredToolTypes(
                new HashSet<string>(ToolsetFilter.KnownToolsets, StringComparer.OrdinalIgnoreCase), config);
        }

        private static TimeoutPolicy BuildTimeout(string mcpName)
        {
            var command = mcpName.StartsWith("revit_", StringComparison.Ordinal)
                ? mcpName.Substring("revit_".Length)
                : mcpName;

            if (ServerLocalTools.Contains(command))
            {
                return new TimeoutPolicy(
                    defaultSeconds: null,
                    parameterDefaultSeconds: null,
                    parameterMinimumSeconds: null,
                    parameterMaximumSeconds: null,
                    transportGraceSeconds: TransportGraceSeconds,
                    policyKind: "server_local");
            }

            if (LongRunTools.Contains(command))
            {
                return new TimeoutPolicy(
                    defaultSeconds: null,
                    parameterDefaultSeconds: LongRunDefaultSeconds,
                    parameterMinimumSeconds: LongRunMinimumSeconds,
                    parameterMaximumSeconds: LongRunMaximumSeconds,
                    transportGraceSeconds: TransportGraceSeconds,
                    policyKind: "long_run_parameter");
            }

            return new TimeoutPolicy(
                defaultSeconds: FixedTimeoutSeconds,
                parameterDefaultSeconds: null,
                parameterMinimumSeconds: null,
                parameterMaximumSeconds: null,
                transportGraceSeconds: TransportGraceSeconds,
                policyKind: "fixed");
        }

        private static string ToShortDescription(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "Revit MCP tool.";

            var normalized = string.Join(" ", value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            return ToolCatalogCodec.TruncateScalars(normalized, 160);
        }

        private static string GetServerVersion()
        {
            var assembly = typeof(Program).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational)) return informational;
            return assembly.GetName().Version?.ToString() ?? "unknown";
        }
    }
}
