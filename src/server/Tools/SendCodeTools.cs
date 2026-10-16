using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("meta")]
    public class SendCodeTools
    {
        [McpServerTool(Name = "revit_send_code_to_revit"), System.ComponentModel.Description(
            "Run full-trust C# in Revit with doc, uidoc and app (doc/uidoc may be null); confirm the operation and prefer typed tools. " +
            "Use RvtMcp.Plugin.SafeFailuresPreprocessor for transactions/StairsEditScope commits: unapproved warnings/errors default to rollback and only model-specific Revit decisions accept supported warnings; inspect commit/failure outcomes and operation status before any retry, with large output auto-spilling to a local same-machine file. Example: return doc?.Title;")]
        public static async Task<string> SendCodeToRevit(string code)
            => await ToolGateway.CallAsync("send_code_to_revit", new { code });
    }
}
