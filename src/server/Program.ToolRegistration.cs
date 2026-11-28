using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RvtMcp.Plugin; // RvtMcpConfig
using RvtMcp.Server.Prompts;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace RvtMcp.Server
{
    // =====================================================================
    // Toolset classes — one per aspect #3 §A3 group, in src/server/Tools/.
    // Registration happens in Program.RegisterToolsets() driven by config.Toolsets.
    // Each method wraps ToolGateway.SendToRevit with a catch-all that surfaces the
    // error to the MCP client as plain text instead of throwing: ToolGateway.CallAsync
    // for plain arguments, ToolGateway.CallWithParamsAsync when input must be parsed.
    // =====================================================================

    partial class Program
    {
        internal const string AlwaysLoadMetadataKey = "anthropic/alwaysLoad";
        internal const string AntigravityAlwaysLoadMetadataKey = "antigravity/alwaysLoad";

        // TOOL-ALWAYSLOAD (BW-MCP-UPGRADE-001): startup routing, health, and task-report tools
        // stay preloaded when registered; revit_send_code_to_revit and revit_get_report_history never do.
        internal static readonly string[] AlwaysLoadToolNames =
        {
            "revit_health",
            "revit_list_available_targets",
            "revit_switch_target",
            "revit_report_task_result"
        };

        private static IMcpServerBuilder RegisterMcpSurface(
            IMcpServerBuilder mcp, HashSet<string> enabled, RvtMcpConfig config)
            => RegisterMcpSurfaceWithTransport(mcp, enabled, config, "stdio");

        internal static IMcpServerBuilder RegisterMcpSurfaceWithTransport(
            IMcpServerBuilder mcp, HashSet<string> enabled, RvtMcpConfig config, string transport)
        {
            var sessionObserver = new McpSessionObserver(transport);
            mcp = McpFeatureLogging.Register(mcp, sessionObserver);
            // The report reminder points at revit_report_task_result, which only the meta toolset registers.
            mcp = RuntimeToolFilter.Register(mcp, config, ToolGateway.Session, null,
                enabled.Contains("meta") ? ToolGateway.ReportReminder : null);
            mcp = RevitTaskSupport.Register(mcp);
            mcp = PromptRequestLogging.Register(mcp, sessionObserver);
            mcp = RegisterToolsets(mcp, enabled, config);
            mcp.WithResources<RevitResources>();
            mcp.WithPrompts<RevitPrompts>();
            mcp = RevitSkills.Register(mcp, enabled, config, sessionObserver);
            return mcp;
        }

        internal static bool IncludeSendCode(HashSet<string> enabled, RvtMcpConfig config)
        {
            var sendCodeOn = config == null || config.EnableSendCodeOrDefault;
            return sendCodeOn
                && (enabled.Contains("meta") || enabled.Contains("toolbaker"));
        }

        internal static IEnumerable<MethodInfo> ResolveRegisteredToolMethods(HashSet<string> enabled, RvtMcpConfig config)
        {
            return ResolveRegisteredToolTypes(enabled, config)
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() != null)
                .Where(method => config?.ReadOnlyOrDefault != true
                    || method.GetCustomAttribute<McpServerToolAttribute>().ReadOnly);
        }

        internal static IMcpServerBuilder RegisterToolsets(IMcpServerBuilder mcp, HashSet<string> enabled, RvtMcpConfig config)
            => mcp.WithTools(CreateRegisteredTools(enabled, config));

        internal static McpServerTool[] CreateRegisteredTools(HashSet<string> enabled, RvtMcpConfig config)
        {
            return ResolveRegisteredToolMethods(enabled, config)
                .Select(method =>
                {
                    var tool = McpServerTool.Create(method, (object)null);
                    if (tool.ProtocolTool.Name == "revit_send_code_to_revit")
                    {
                        // Keep optional arrays nullable, but never advertise null module names or unsupported modes.
                        var schema = System.Text.Json.Nodes.JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText());
                        schema["properties"]["modules"]["items"]["type"] = "string";
                        schema["properties"]["result_contract"]["enum"] = new System.Text.Json.Nodes.JsonArray("legacy", "task_v1");
                        schema["properties"]["usage_context"]["enum"] = new System.Text.Json.Nodes.JsonArray("project", "test", "unknown");
                        tool.ProtocolTool.InputSchema = System.Text.Json.JsonSerializer.SerializeToElement(schema);
                    }
                    if (tool.ProtocolTool.Name == "revit_health")
                        tool.ProtocolTool.Meta = new System.Text.Json.Nodes.JsonObject
                        {
                            [HealthRuntimeInfo.ClientRuntimeMetadataKey] = System.Text.Json.Nodes.JsonNode.Parse(
                                HealthRuntimeInfo.ClientRuntime(GatewayRuntime.Identity).ToString())
                        };
                    if (AlwaysLoadToolNames.Contains(tool.ProtocolTool.Name, StringComparer.Ordinal))
                    {
                        tool.ProtocolTool.Meta ??= new System.Text.Json.Nodes.JsonObject();
                        tool.ProtocolTool.Meta[AlwaysLoadMetadataKey] = true;
                        tool.ProtocolTool.Meta[AntigravityAlwaysLoadMetadataKey] = true;
                    }
                    return tool;
                }).ToArray();
        }

        internal static Type[] ResolveRegisteredToolTypes(HashSet<string> enabled, RvtMcpConfig config)
        {
            var types = new List<Type>();
            types.AddRange(CodeModuleToolRegistration.Resolve(enabled, config, RvtMcp.Scripting.ScriptProtocol.IsReady));
            if (enabled.Contains("query"))      types.Add(typeof(QueryTools));
            if (enabled.Contains("create"))     types.Add(typeof(CreateTools));
            if (enabled.Contains("modify"))     types.Add(typeof(ModifyTools));
            if (enabled.Contains("delete"))     types.Add(typeof(DeleteTools));
            if (enabled.Contains("view"))       types.Add(typeof(ViewTools));
            if (enabled.Contains("schedule"))   types.Add(typeof(ScheduleTools));
            if (enabled.Contains("families"))   types.Add(typeof(FamiliesTools));
            if (enabled.Contains("graphics"))   types.Add(typeof(GraphicsTools));
            if (enabled.Contains("export"))     types.Add(typeof(ExportTools));
            if (enabled.Contains("annotation")) types.Add(typeof(AnnotationTools));
            if (enabled.Contains("mep"))        types.Add(typeof(MepTools));
            if (enabled.Contains("sheets"))      types.Add(typeof(SheetsTools));
            if (enabled.Contains("materials"))   types.Add(typeof(MaterialsTools));
            if (enabled.Contains("geometry"))    types.Add(typeof(GeometryTools));
            if (enabled.Contains("rooms"))       types.Add(typeof(RoomsTools));
            if (enabled.Contains("links"))       types.Add(typeof(LinksTools));
            if (enabled.Contains("parameters"))   types.Add(typeof(ParametersTools));
            if (enabled.Contains("organization")) types.Add(typeof(OrganizationTools));
            if (enabled.Contains("workflows"))    types.Add(typeof(WorkflowsTools));
            if (enabled.Contains("documents"))    types.Add(typeof(DocumentTools));
            if (enabled.Contains("toolbaker"))  types.Add(typeof(ToolbakerTools));
            if (enabled.Contains("toolbaker") && config?.EnableAdaptiveBakeOrDefault == true)
                types.Add(typeof(AdaptiveBakeTools));
            if (IncludeSendCode(enabled, config)) types.Add(typeof(SendCodeTools));
            if (enabled.Contains("meta"))       types.Add(typeof(MetaTools));
            if (enabled.Contains("meta"))       types.Add(typeof(ReportTools));
            if (enabled.Contains("meta"))       types.Add(typeof(ApiTools));
            if (enabled.Contains("meta"))       types.Add(typeof(HealthTools));
            if (enabled.Contains("meta"))       types.Add(typeof(ChangeHistoryTools));
            if (enabled.Contains("meta"))       types.Add(typeof(FailureTools));
            if (enabled.Contains("meta"))       types.Add(typeof(WorkflowGuideTools));
            if (enabled.Contains("lint"))       types.Add(typeof(LintTools));
            if (enabled.Contains("structural")) types.Add(typeof(StructuralTools));
            return types.ToArray();
        }
    }
}
