using RvtMcp.Plugin;
using RvtMcp.ToolCatalog;
using Newtonsoft.Json;

namespace RvtMcp.Server
{
    internal static class ServerState
    {
        public static RvtMcpConfig Config { get; set; }

        public static RvtMcp.ToolCatalog.ToolCatalog ToolCatalog { get; set; }

        public static bool IsReadOnly => Config?.ReadOnlyOrDefault ?? false;

        public static string BlockIfReadOnly(string toolName)
        {
            if (!IsReadOnly || ToolReadPolicy.IsReadOnly(toolName)) return null;
            return JsonConvert.SerializeObject(new
            {
                success = false,
                error = "read_only_mode",
                tool = toolName,
                message = $"Tool '{toolName}' is disabled because the server is running with --read-only."
            }, Formatting.Indented);
        }
    }
}
