using System;
using System.Globalization;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using RvtMcp.Plugin;
using RvtMcp.Server.Prompts;

namespace RvtMcp.Server
{
    internal static class PromptRequestLogging
    {
        private static readonly AsyncLocal<string> SessionIdSlot = new AsyncLocal<string>();

        internal static string CurrentSessionId
        {
            get => SessionIdSlot.Value;
            set => SessionIdSlot.Value = value;
        }

        internal static IMcpServerBuilder Register(IMcpServerBuilder mcp, McpSessionObserver sessionObserver = null)
            => mcp.WithRequestFilters(filters => filters.AddGetPromptFilter(next => async (request, ct) =>
            {
                sessionObserver?.Observe(request.Server, request.JsonRpcRequest);
                // Log the request once, including failed lookups. Never inspect prompt arguments.
                DiagnosticLog.Write("Prompts", Format(request.Params?.Name,
                    request.Server?.SessionId ?? "stdio", DateTimeOffset.UtcNow, request.Server?.ClientInfo?.Name));
                return await next(request, ct);
            }));

        internal static string Format(string name, string sessionId, DateTimeOffset utc)
            => FormatEntry("prompts/get name=", name, sessionId, utc, includeClient: false, clientName: null);

        internal static string Format(string name, string sessionId, DateTimeOffset utc, string clientName)
            => FormatEntry("prompts/get name=", name, sessionId, utc, includeClient: true, clientName: clientName);

        internal static string FormatSkillList(int count, string sessionId, DateTimeOffset utc, string clientName = null)
            => FormatEntry("skills/list count=", count.ToString(CultureInfo.InvariantCulture), sessionId, utc, includeClient: true, clientName: clientName);

        internal static string FormatSkillGet(string uri, string sessionId, DateTimeOffset utc, string clientName = null)
            => FormatEntry("skills/get uri=", uri, sessionId, utc, includeClient: true, clientName: clientName);

        internal static string FormatSkillRead(string uri, string sessionId, DateTimeOffset utc, string clientName = null)
            => FormatEntry("skills/read uri=", uri, sessionId, utc, includeClient: true, clientName: clientName);

        internal static string FormatWorkflowTool(string name, string sessionId, DateTimeOffset utc, string clientName = null)
            => FormatEntry("tools/revit_get_workflow name=", name, sessionId, utc, includeClient: true, clientName: clientName);

        internal static void LogSkillList(int count, string sessionId, string clientName = null)
            => DiagnosticLog.Write("Skills", FormatSkillList(count, sessionId ?? "stdio", DateTimeOffset.UtcNow, clientName));

        internal static void LogSkillGet(string uri, string sessionId, string clientName = null)
            => LogSkill("skills/get uri=", uri, sessionId, clientName);

        internal static void LogSkillRead(string uri, string sessionId, string clientName = null)
        {
            if (uri == null || !uri.StartsWith("skill://", StringComparison.OrdinalIgnoreCase))
                return;
            LogSkill("skills/read uri=", uri, sessionId, clientName);
        }

        internal static void LogWorkflowTool(string rawName, WorkflowDefinition matched, string sessionId, string clientName = null)
        {
            var label = string.IsNullOrWhiteSpace(rawName)
                ? "(list)"
                : (matched != null ? matched.Id : "(unknown)");
            DiagnosticLog.Write("Workflows", FormatWorkflowTool(label, sessionId ?? "stdio", DateTimeOffset.UtcNow, clientName));
        }

        private static string FormatEntry(string prefix, string value, string sessionId, DateTimeOffset utc, bool includeClient, string clientName)
        {
            var entry = prefix + value + " utc=" + utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                + " session=" + sessionId;
            return includeClient ? entry + " client=" + McpFeatureLogging.FormatToken(clientName) : entry;
        }

        private static void LogSkill(string prefix, string uri, string sessionId, string clientName)
        {
            var label = WorkflowCatalog.TryFindBySkillUri(uri, out var def) ? def.SkillUri : "(unknown)";
            DiagnosticLog.Write("Skills", FormatEntry(prefix, label, sessionId ?? "stdio", DateTimeOffset.UtcNow, includeClient: true, clientName: clientName));
        }
    }
}
