#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("meta")]
    public class SendCodeTools
    {
        [McpServerTool(Name = "revit_send_code_to_revit"), System.ComponentModel.Description(
            "Full-trust C#; confirm edits, prefer typed tools. app, nullable doc/uidoc. " +
            "Body with return (helper types allowed), or public static McpDynamicScript.Run(UIApplication[, JObject]). " +
            "Use data for literals/coordinates/lists, modules for repeated helpers, compile_only to validate; result_contract=task_v1 uses McpTaskResult.Pass/Fail. " +
            "Imports: System/Linq/Collections.Generic, Autodesk.Revit.DB/UI. " +
            "Use RvtMcp.Plugin.SafeFailuresPreprocessor for transactions/StairsEditScope; unapproved failures roll back. " +
            "Inspect _task/_operation before retry. Large output auto-spills to a local same-machine file.")]
        public static async Task<string> SendCodeToRevit(string code, Dictionary<string, JsonElement>? data = null,
            string[]? modules = null, bool compile_only = false, string result_contract = "legacy", string usage_context = "unknown")
        {
            try { return await SendCodeCore.SendAsync(new RvtMcp.Scripting.ScriptInput(code,
                RvtMcp.Scripting.ScriptData.FromJson(data == null ? null : JsonSerializer.Serialize(data)), modules, compile_only, result_contract, usage_context)); }
            catch (RvtMcp.Scripting.ScriptContractException ex) { return "Error: " + ex.ErrorCode + ": " + ex.Message; }
        }
    }
}
