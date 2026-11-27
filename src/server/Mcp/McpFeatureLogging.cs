using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RvtMcp.Failures;
using RvtMcp.Plugin;

namespace RvtMcp.Server
{
    /// <summary>
    /// Deduplicates session/client capability observations per server instance so <c>rvt-mcp.log</c>
    /// captures protocol version, client identity, transport, and advertised MCP capabilities
    /// without spamming repeated tool calls or leaking memory on stateless HTTP.
    /// </summary>
    internal sealed class McpSessionObserver
    {
        private const int MaxFingerprints = 256;
        private readonly string _transport;
        private readonly ConcurrentDictionary<string, byte> _seen = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        internal McpSessionObserver(string transport = "stdio")
        {
            _transport = McpFeatureLogging.FormatToken(transport);
        }

        internal void Observe(McpServer server, JsonRpcRequest jsonRpcRequest)
        {
            var protocol = McpFeatureLogging.ResolveProtocolVersion(server, jsonRpcRequest);
            var client = McpFeatureLogging.ResolveClientName(server, jsonRpcRequest);
            var session = McpFeatureLogging.FormatToken(server?.SessionId ?? _transport);
            var hasTasks = McpFeatureLogging.HasExtension(server, jsonRpcRequest, McpFeatureLogging.TasksExtensionId);
            var hasSkills = McpFeatureLogging.HasExtension(server, jsonRpcRequest, McpFeatureLogging.SkillsExtensionId);
            var hasElicitation = McpFeatureLogging.HasElicitation(server, jsonRpcRequest);

            var key = string.Concat(
                session, "|", _transport, "|", protocol, "|", client, "|",
                hasTasks ? "1" : "0", hasSkills ? "1" : "0", hasElicitation ? "1" : "0");

            if (_seen.Count >= MaxFingerprints || !_seen.TryAdd(key, 0))
                return;

            DiagnosticLog.Write("McpSession", McpFeatureLogging.FormatSessionCapabilities(
                _transport, protocol, client, hasTasks, hasSkills, hasElicitation, session, DateTimeOffset.UtcNow));
        }
    }

    /// <summary>
    /// Passive server-side feature instrumentation for MCP 2026-07-28 adoption measurement
    /// (BW-MCP-UPGRADE-001 §12): session capabilities, Tasks opt-in/lifecycle, and client attribution.
    /// Writes bounded, redacted key=value lines to <see cref="DiagnosticLog"/>.
    /// </summary>
    internal static class McpFeatureLogging
    {
        internal const string TasksExtensionId = "io.modelcontextprotocol/tasks";
        internal const string SkillsExtensionId = "io.modelcontextprotocol/skills";
        private const int MaxTokenLength = 64;

        internal static IMcpServerBuilder Register(IMcpServerBuilder mcp, McpSessionObserver observer)
            => mcp.WithRequestFilters(filters =>
            {
                filters.AddListToolsFilter(next => async (request, ct) =>
                {
                    observer.Observe(request.Server, request.JsonRpcRequest);
                    return await next(request, ct);
                });
                filters.AddCallToolFilter(next => async (request, ct) =>
                {
                    observer.Observe(request.Server, request.JsonRpcRequest);
                    return await next(request, ct);
                });
            });

        internal static string FormatToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "(unknown)";

            var trimmed = value.Trim();
            var sb = new StringBuilder(Math.Min(trimmed.Length, MaxTokenLength));
            foreach (var ch in trimmed)
            {
                if (sb.Length >= MaxTokenLength)
                    break;
                sb.Append(char.IsWhiteSpace(ch) || char.IsControl(ch) ? '_' : ch);
            }
            return sb.Length == 0 ? "(unknown)" : sb.ToString();
        }

        internal static string FormatTaskId(string taskId)
            => FailureCapture.IsKey(taskId) ? taskId : "(unknown)";

        internal static string ResolveProtocolVersion(McpServer server, JsonRpcRequest jsonRpcRequest)
            => FormatToken(jsonRpcRequest?.Context?.ProtocolVersion ?? server?.NegotiatedProtocolVersion);

        internal static string ResolveClientName(McpServer server, JsonRpcRequest jsonRpcRequest)
            => FormatToken(server?.ClientInfo?.Name ?? jsonRpcRequest?.Context?.ClientInfo?.Name);

        internal static bool IsJuly2026OrLater(string protocolVersion)
            => !string.IsNullOrEmpty(protocolVersion)
                && protocolVersion.Length == 10
                && protocolVersion[4] == '-'
                && protocolVersion[7] == '-'
                && string.CompareOrdinal(protocolVersion, "2026-07-28") >= 0;

        internal static bool HasExtension(McpServer server, JsonRpcRequest jsonRpcRequest, string extensionId)
        {
            if (server?.ClientCapabilities?.Extensions?.ContainsKey(extensionId) == true)
                return true;
            if (jsonRpcRequest?.Context?.ClientCapabilities?.Extensions?.ContainsKey(extensionId) == true)
                return true;
            return jsonRpcRequest?.Params?["_meta"]?["io.modelcontextprotocol/clientCapabilities"]?["extensions"] is JsonObject ext
                && ext.ContainsKey(extensionId);
        }

        internal static bool HasElicitation(McpServer server, JsonRpcRequest jsonRpcRequest)
        {
            if (server?.ClientCapabilities?.Elicitation != null)
                return true;
            if (jsonRpcRequest?.Context?.ClientCapabilities?.Elicitation != null)
                return true;
            return jsonRpcRequest?.Params?["_meta"]?["io.modelcontextprotocol/clientCapabilities"]?["elicitation"] != null;
        }

        internal static bool HasTaskOptIn(McpServer server, JsonRpcRequest jsonRpcRequest)
            => IsJuly2026OrLater(jsonRpcRequest?.Context?.ProtocolVersion ?? server?.NegotiatedProtocolVersion)
                && HasExtension(server, jsonRpcRequest, TasksExtensionId);

        internal static string FormatSessionCapabilities(
            string transport, string protocol, string client,
            bool hasTasks, bool hasSkills, bool hasElicitation,
            string sessionId, DateTimeOffset utc)
            => "session/capabilities transport=" + FormatToken(transport)
                + " protocol=" + FormatToken(protocol)
                + " client=" + FormatToken(client)
                + " tasks=" + (hasTasks ? "1" : "0")
                + " skills=" + (hasSkills ? "1" : "0")
                + " elicitation=" + (hasElicitation ? "1" : "0")
                + " utc=" + utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                + " session=" + FormatToken(sessionId);

        internal static void LogTaskEligible(string toolName, bool asTask, McpServer server, JsonRpcRequest jsonRpcRequest)
        {
            var protocol = ResolveProtocolVersion(server, jsonRpcRequest);
            var client = ResolveClientName(server, jsonRpcRequest);
            var session = FormatToken(server?.SessionId ?? "stdio");
            DiagnosticLog.Write("Tasks",
                "tasks/eligible tool=" + FormatToken(toolName)
                + " mode=" + (asTask ? "task" : "sync")
                + " protocol=" + protocol
                + " client=" + client
                + " utc=" + DateTimeOffset.UtcNow.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                + " session=" + session);
        }

        internal static void LogTaskCreate(string taskId)
            => DiagnosticLog.Write("Tasks",
                "tasks/create task=" + FormatTaskId(taskId)
                + " utc=" + DateTimeOffset.UtcNow.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

        internal static void LogTaskDispatch(string taskId, string command, bool isReadOnly)
            => DiagnosticLog.Write("Tasks",
                "tasks/dispatch task=" + FormatTaskId(taskId)
                + " tool=" + FormatToken(command)
                + " readonly=" + (isReadOnly ? "1" : "0")
                + " utc=" + DateTimeOffset.UtcNow.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

        internal static void LogTaskGet(string taskId, string status, string source)
            => DiagnosticLog.Write("Tasks",
                "tasks/get task=" + FormatTaskId(taskId)
                + " status=" + FormatToken(status)
                + " source=" + FormatToken(source)
                + " utc=" + DateTimeOffset.UtcNow.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

        internal static void LogTaskCancel(string taskId, string stage)
            => DiagnosticLog.Write("Tasks",
                "tasks/cancel task=" + FormatTaskId(taskId)
                + " stage=" + FormatToken(stage)
                + " utc=" + DateTimeOffset.UtcNow.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

        internal static void LogTaskComplete(string taskId, bool isError, bool afterCancel)
            => DiagnosticLog.Write("Tasks",
                "tasks/complete task=" + FormatTaskId(taskId)
                + " is_error=" + (isError ? "1" : "0")
                + " after_cancel=" + (afterCancel ? "1" : "0")
                + " utc=" + DateTimeOffset.UtcNow.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

        internal static void LogTaskFail(string taskId, bool afterCancel)
            => DiagnosticLog.Write("Tasks",
                "tasks/fail task=" + FormatTaskId(taskId)
                + " after_cancel=" + (afterCancel ? "1" : "0")
                + " utc=" + DateTimeOffset.UtcNow.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }
}
