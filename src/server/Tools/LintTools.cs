using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Bimwright.Targeting;
using RvtMcp.Plugin; // RvtMcpConfig
using RvtMcp.Server.Bake;
using RvtMcp.Server.Handlers;
using RvtMcp.Server.Prompts;
using RvtMcp.ToolCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("lint")]
    public class LintTools
    {
        [McpServerTool(Name = "revit_analyze_view_naming_patterns", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Infer naming patterns with bounded detail. Page outliers with startOutlier + maxOutliers; maxPatterns hard max 500.")]
        public static async Task<string> AnalyzeViewNamingPatterns(int maxPatterns = 50, int startOutlier = 0, int maxOutliers = 20)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("analyze_view_naming_patterns", new { max_patterns = maxPatterns, start_outlier = startOutlier, max_outliers = maxOutliers });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_suggest_view_name_corrections", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Propose corrected view names for outliers. Optional profile=<id> uses firm-profile library rule; omit to use project-inferred dominant pattern. Returns suggestions array with id/current/suggested/reason.")]
        public static async Task<string> SuggestViewNameCorrections(string profile = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("suggest_view_name_corrections", new { profile });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_detect_firm_profile", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Fingerprint project naming (views + sheets + levels), match against firm-profile library. Returns project_pattern (always) + library_match (null if library empty or no match).")]
        public static async Task<string> DetectFirmProfile()
        {
            try
            {
                var result = await ToolGateway.SendToRevit("detect_firm_profile");
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_model_warnings_summary", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description("Group warnings by description with bounded types/examples. Use max_warning_types (hard max 1000) and max_examples_per_type (hard max 100).")]
        public static async Task<string> GetModelWarningsSummary(
            bool include_examples = true, int max_examples_per_type = 5, int max_warning_types = 200)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_model_warnings_summary", new { include_examples, max_examples_per_type, max_warning_types });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }
}
