using System.Reflection;

namespace RvtMcp.Server
{
    partial class Program
    {
        // MCP InitializeResult metadata. ServerInstructions is the single most important
        // signal for Tool Search discoverability — Claude Code/Desktop's Tool Search ranks
        // servers by (1) instructions, (2) literal tool name. Without this populated, an
        // agent asking "list Revit tools" returns nothing even though 224 tools are exposed.
        // Anthropic truncates this field at 2KB; the keyword-dense first paragraph carries
        // the discoverability load if the SDK or proxy truncates later.
        // Preserve the revision so a fresh client can compare the loaded gateway build.
        internal static readonly string ServerInformationalVersion =
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "0.0.0";
        internal static readonly string ServerVersion =
            ServerInformationalVersion.Split('+')[0];

        internal static void ConfigureMcpServerOptions(ModelContextProtocol.Server.McpServerOptions opts)
        {
            opts.ServerInfo = new ModelContextProtocol.Protocol.Implementation
            {
                Name = "rvt-mcp",
                Title = "Revit MCP",
                Version = ServerInformationalVersion,
                Description = "Model Context Protocol gateway for Autodesk Revit 2022-2027",
                WebsiteUrl = "https://github.com/bimwright/rvt-mcp"
            };
            // Health owns component-aware update guidance. A cached semantic-version notice cannot
            // establish the loaded plugin identity or authorize replacement of a development pair.
            opts.ServerInstructions = ServerInstructionsText;
        }
    }
}
