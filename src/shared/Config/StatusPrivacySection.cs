using System.Text;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// Formats privacy / bake flag lines for the ribbon Status dialog (Wave A5).
    /// API-free so unit tests can cover copy without Revit.
    /// </summary>
    public static class StatusPrivacySection
    {
        public static string Build(RvtMcpConfig config, bool toastEnabled)
        {
            if (config == null)
                config = new RvtMcpConfig();

            var sb = new StringBuilder();
            sb.AppendLine("Privacy & bake (plugin-visible config)");
            sb.AppendLine($"  Toast notifications: {OnOff(toastEnabled)}  (ribbon toggle)");
            sb.AppendLine($"  ToolBaker tools: {OnOff(config.EnableToolbakerOrDefault)}  (list/run baked; send_code follows this flag too)");
            sb.AppendLine($"  Adaptive bake suggestions: {OnOff(config.EnableAdaptiveBakeOrDefault)}");
            sb.AppendLine($"  Cache send_code bodies (for bake clusters): {OnOff(config.CacheSendCodeBodiesOrDefault)}");

            if (config.IsPersistSendCodeBodiesActive())
            {
                var until = config.PersistSendCodeBodiesUntil ?? "?";
                sb.AppendLine($"  Persist send_code journal (TTL): ON until {until} (UTC)");
            }
            else
            {
                sb.AppendLine("  Persist send_code journal (TTL): OFF");
            }

            sb.AppendLine();
            sb.Append("Note: change adaptive/cache/persist via env, CLI, or %LOCALAPPDATA%\\Bimwright\\rvt-mcp\\rvtmcp.config.json; restart MCP client after server flags change. Default privacy keeps body cache and journal OFF.");
            return sb.ToString();
        }

        private static string OnOff(bool value) => value ? "ON" : "OFF";
    }
}
