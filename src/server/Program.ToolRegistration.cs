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
        private static IMcpServerBuilder RegisterMcpSurface(
            IMcpServerBuilder mcp, HashSet<string> enabled, RvtMcpConfig config)
        {
            mcp = RuntimeToolFilter.Register(mcp, config, ToolGateway.Session);
            mcp = PromptRequestLogging.Register(mcp);
            mcp = RegisterToolsets(mcp, enabled, config);
            mcp.WithResources<RevitResources>();
            mcp.WithPrompts<RevitPrompts>();
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
                    || method.GetCustomAttribute<McpServerToolAttribute>().ReadOnly
                    // revit_switch_target is annotated ReadOnly=false (it mutates session
                    // routing) but must stay exposed in read-only sessions so the agent
                    // can always recover the target binding (spec §6.8/§6.11).
                    || method.GetCustomAttribute<McpServerToolAttribute>().Name == "revit_switch_target");
        }

        internal static IMcpServerBuilder RegisterToolsets(IMcpServerBuilder mcp, HashSet<string> enabled, RvtMcpConfig config)
            => mcp.WithTools(CreateRegisteredTools(enabled, config));

        internal static McpServerTool[] CreateRegisteredTools(HashSet<string> enabled, RvtMcpConfig config)
        {
            return ResolveRegisteredToolMethods(enabled, config)
                .Select(method =>
                {
                    var tool = McpServerTool.Create(method, (object)null);
                    if (tool.ProtocolTool.Name == "revit_health")
                        tool.ProtocolTool.Meta = new System.Text.Json.Nodes.JsonObject
                        {
                            [HealthRuntimeInfo.ClientRuntimeMetadataKey] = System.Text.Json.Nodes.JsonNode.Parse(
                                HealthRuntimeInfo.ClientRuntime(GatewayRuntime.Identity).ToString())
                        };
                    return tool;
                }).ToArray();
        }

        internal static Type[] ResolveRegisteredToolTypes(HashSet<string> enabled, RvtMcpConfig config)
        {
            var types = new List<Type>();
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
            if (enabled.Contains("toolbaker"))  types.Add(typeof(ToolbakerTools));
            if (enabled.Contains("toolbaker") && config?.EnableAdaptiveBakeOrDefault == true)
                types.Add(typeof(AdaptiveBakeTools));
            if (IncludeSendCode(enabled, config)) types.Add(typeof(SendCodeTools));
            if (enabled.Contains("meta"))       types.Add(typeof(MetaTools));
            if (enabled.Contains("meta"))       types.Add(typeof(HealthTools));
            if (enabled.Contains("meta"))       types.Add(typeof(ChangeHistoryTools));
            if (enabled.Contains("meta"))       types.Add(typeof(FailureTools));
            if (enabled.Contains("lint"))       types.Add(typeof(LintTools));
            if (enabled.Contains("structural")) types.Add(typeof(StructuralTools));
            return types.ToArray();
        }
    }
}
