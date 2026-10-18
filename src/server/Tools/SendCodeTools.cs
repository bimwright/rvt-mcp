using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("meta")]
    public class SendCodeTools
    {
        [McpServerTool(Name = "revit_send_code_to_revit"), System.ComponentModel.Description(
            "Full-trust C# with app, doc/uidoc (nullable); confirm edits; prefer typed tools. " +
            "Use a C# body ending in return (helper types allowed), or public class McpDynamicScript with public static object Run(UIApplication app). " +
            "Imports: System, System.Linq, System.Collections.Generic, Autodesk.Revit.DB, Autodesk.Revit.UI. " +
            "Use RvtMcp.Plugin.SafeFailuresPreprocessor for transactions/StairsEditScope: unapproved warnings/errors roll back; only model-specific Revit decisions accept supported warnings. " +
            "Check commit/failure/operation status before retry. Large output auto-spills to a local same-machine file.")]
        public static async Task<string> SendCodeToRevit(string code)
            => await ToolGateway.CallAsync("send_code_to_revit", new { code });
    }
}
