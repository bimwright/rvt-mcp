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
        // InformationalVersion carries "+githash"; report clean semver to MCP clients.
        internal static readonly string ServerVersion =
            (Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "0.0.0").Split('+')[0];

        private static void ConfigureMcpServerOptions(ModelContextProtocol.Server.McpServerOptions opts)
        {
            opts.ServerInfo = new ModelContextProtocol.Protocol.Implementation
            {
                Name = "rvt-mcp",
                Title = "Revit MCP",
                Version = ServerVersion,
                Description = "Model Context Protocol gateway for Autodesk Revit 2022-2027",
                WebsiteUrl = "https://github.com/bimwright/rvt-mcp"
            };
            // A newer release known from the add-in's cached daily check (no network here).
            opts.ServerInstructions = RvtMcp.Plugin.Update.UpdateNoticeText.ComposeInstructions(
                ServerInstructionsText, RvtMcp.Plugin.Update.UpdateNoticeText.ForAgent(UpdateStatus.Current()));
        }
    }
}
