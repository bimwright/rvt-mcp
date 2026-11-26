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
            "Full-trust C# in Revit's UI thread (disabled by --read-only or --disable-send-code); confirm edits and prefer typed tools. " +
            "Call revit_survey_change_impact before modifying existing elements and read back the result. " +
            "In scope for body scripts: UIApplication app, nullable UIDocument uidoc and Document doc (null when no document is open, e.g. Revit Home), and JObject data when data is supplied. " +
            "Code shape: method body with return (no closing }) plus optional helper types, or a full public static McpDynamicScript.Run(UIApplication[, JObject]); return JSON-safe DTOs, never raw Revit API objects. " +
            "Default imports (body scripts; full source should declare its own usings): System, System.Linq, System.Collections.Generic, Autodesk.Revit.DB (+ Architecture, Electrical, Mechanical, Plumbing, Structure), Autodesk.Revit.UI; plus Newtonsoft.Json.Linq with data and RvtMcp.Plugin with result_contract=task_v1. " +
            "Wrap writes in Transaction/StairsEditScope using RvtMcp.Plugin.SafeFailuresPreprocessor; unapproved failures roll back that transaction (earlier committed transactions or file writes are not undone). " +
            "Use data for literals/coordinates/lists, modules for repeated helpers, compile_only to validate, and result_contract=task_v1 for McpTaskResult.Pass/Fail. " +
            "Inspect _task/_operation/_failure before retrying. Output over the ~700 KiB budget auto-spills to a local same-machine file (kept 36 h by default); never re-run a write to recover spilled output.")]
        public static async Task<string> SendCodeToRevit(
            [System.ComponentModel.Description("C# body with return (no closing }) or full McpDynamicScript class; use revit_describe_api to inspect Revit API types first.")] string code,
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
