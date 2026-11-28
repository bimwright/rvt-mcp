using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace RvtMcp.Server
{
    /// <summary>
    /// Opt-in MCP Tasks (SEP-2663) support for long-running Revit tools
    /// (BW-MCP-UPGRADE-001: TASK-OPT-IN, TASK-ON-OPERATION, TASK-CANCEL-HONEST, TASK-RECOVER, TASK-TIMING).
    /// Only the four designated long-run tools accept task creation when the client opts in via
    /// <c>io.modelcontextprotocol/tasks</c>; all other tools remain strictly synchronous.
    /// </summary>
    internal static class RevitTaskSupport
    {
        internal static readonly HashSet<string> TaskEnabledTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "revit_clash_detection",
            "revit_batch_export_sheets",
            "revit_export_ifc",
            "revit_export_nwc"
        };

        internal static McpTaskExecutionMode SelectExecutionMode(RequestContext<CallToolRequestParams> request)
        {
            if (request?.Params?.Name is { Length: > 0 } name && TaskEnabledTools.Contains(name))
            {
                McpFeatureLogging.LogTaskEligible(
                    name,
                    McpFeatureLogging.HasTaskOptIn(request.Server, request.JsonRpcRequest),
                    request.Server,
                    request.JsonRpcRequest);
                return McpTaskExecutionMode.Optional;
            }
            return McpTaskExecutionMode.Synchronous;
        }

        internal static IMcpServerBuilder Register(IMcpServerBuilder mcp, OperationBackedTaskStore store = null)
            => mcp.WithTasks(store ?? new OperationBackedTaskStore(), opts => opts.ExecutionModeSelector = SelectExecutionMode);
    }
}
