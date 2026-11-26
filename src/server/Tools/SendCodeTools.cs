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
            "Full-trust C# on Revit's UI thread; prefer typed tools and confirm edits. Hidden by --read-only or --disable-send-code. " +
            "Call revit_survey_change_impact before editing existing elements and read back the result. " +
            "Use SafeFailuresPreprocessor for writes; see code for context. Use data, modules and compile_only. " +
            "Inspect _task/_operation/_failure before retrying. Output over ~700 KiB auto-spills to a local file (36 h default); never rerun writes to recover it.")]
        public static async Task<string> SendCodeToRevit(
            [System.ComponentModel.Description(
                "C# body with return (no closing }) and optional helper types, or full public static McpDynamicScript.Run(UIApplication[, JObject]). " +
                "Body globals: UIApplication app, nullable UIDocument uidoc and Document doc (null at Revit Home), plus JObject data when supplied. Return JSON-safe DTOs, never raw Revit API objects. " +
                "Body imports: System, System.Linq, System.Collections.Generic, Autodesk.Revit.DB (+ Architecture, Electrical, Mechanical, Plumbing, Structure), Autodesk.Revit.UI; plus Newtonsoft.Json.Linq with data and RvtMcp.Plugin with result_contract=task_v1. Full source should declare its own usings. " +
                "Wrap writes in Transaction/StairsEditScope with RvtMcp.Plugin.SafeFailuresPreprocessor: unapproved failures roll back that transaction, not earlier commits or file writes. Use revit_describe_api to inspect Revit API types.")] string code,
            [System.ComponentModel.Description("Optional JSON object bound as JObject data for literals, coordinates, and lists so code stays reusable.")] Dictionary<string, JsonElement>? data = null,
            [System.ComponentModel.Description("Optional saved code-module names (from revit_list_code_modules) compiled alongside the script for shared helpers.")] string[]? modules = null,
            [System.ComponentModel.Description("Validate syntax and compilation without executing against Revit (default false).")] bool compile_only = false,
            [System.ComponentModel.Description("legacy (default) or task_v1 (uses McpTaskResult.Pass/Fail structured contract).")] string result_contract = "legacy",
            [System.ComponentModel.Description("Execution context label for diagnostics: project, test, or unknown (default unknown).")] string usage_context = "unknown")
        {
            try { return await SendCodeCore.SendAsync(new RvtMcp.Scripting.ScriptInput(code,
                RvtMcp.Scripting.ScriptData.FromJson(data == null ? null : JsonSerializer.Serialize(data)), modules, compile_only, result_contract, usage_context)); }
            catch (RvtMcp.Scripting.ScriptContractException ex) { return "Error: " + ex.ErrorCode + ": " + ex.Message; }
        }
    }
}
