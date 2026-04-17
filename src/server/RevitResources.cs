using System.ComponentModel;
using ModelContextProtocol.Server;
using Bimwright.Rvt.Server.Memory;

namespace Bimwright.Rvt.Server
{
    [McpServerResourceType]
    public class RevitResources
    {
        internal static SessionContext Session { get; set; }

        [McpServerResource(UriTemplate = "revit://session/context", Name = "Session Context", MimeType = "application/json")]
        [Description("Current MCP session context: tool call history, success rates, usage patterns, and flags. Use this to understand what has been done in this session.")]
        public static string GetSessionContext()
        {
            return Session?.GetSummary() ?? "{\"status\": \"no session\"}";
        }
    }
}
