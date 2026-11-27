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

        internal static IMcpServerBuilder Register(IMcpServerBuilder mcp)
            => mcp.WithRequestFilters(filters => filters.AddGetPromptFilter(next => async (request, ct) =>
            {
                // Log the request once, including failed lookups. Never inspect prompt arguments.
                DiagnosticLog.Write("Prompts", Format(request.Params?.Name,
                    request.Server?.SessionId ?? "stdio", DateTimeOffset.UtcNow));
                return await next(request, ct);
            }));

        internal static string Format(string name, string sessionId, DateTimeOffset utc)
            => "prompts/get name=" + name + " utc=" + utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                + " session=" + sessionId;

        internal static string FormatSkillGet(string uri, string sessionId, DateTimeOffset utc)
            => "skills/get uri=" + uri + " utc=" + utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                + " session=" + sessionId;

        internal static string FormatSkillRead(string uri, string sessionId, DateTimeOffset utc)
            => "skills/read uri=" + uri + " utc=" + utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                + " session=" + sessionId;

        internal static string FormatWorkflowTool(string name, string sessionId, DateTimeOffset utc)
            => "tools/revit_get_workflow name=" + name + " utc=" + utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                + " session=" + sessionId;

        internal static void LogSkillGet(string uri, string sessionId)
        {
            var label = WorkflowCatalog.TryFindBySkillUri(uri, out var def) ? def.SkillUri : "(unknown)";
            DiagnosticLog.Write("Skills", FormatSkillGet(label, sessionId ?? "stdio", DateTimeOffset.UtcNow));
        }

        internal static void LogSkillRead(string uri, string sessionId)
        {
            if (uri == null || !uri.StartsWith("skill://", StringComparison.OrdinalIgnoreCase))
                return;
            var label = WorkflowCatalog.TryFindBySkillUri(uri, out var def) ? def.SkillUri : "(unknown)";
            DiagnosticLog.Write("Skills", FormatSkillRead(label, sessionId ?? "stdio", DateTimeOffset.UtcNow));
        }

        internal static void LogWorkflowTool(string rawName, WorkflowDefinition matched, string sessionId)
        {
            var label = string.IsNullOrWhiteSpace(rawName)
                ? "(list)"
                : (matched != null ? matched.Id : "(unknown)");
            DiagnosticLog.Write("Workflows", FormatWorkflowTool(label, sessionId ?? "stdio", DateTimeOffset.UtcNow));
        }
    }
}
