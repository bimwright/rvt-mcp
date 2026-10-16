using System;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using RvtMcp.Plugin;

namespace RvtMcp.Server
{
    internal static class PromptRequestLogging
    {
        internal static IMcpServerBuilder Register(IMcpServerBuilder mcp)
            => mcp.WithRequestFilters(filters => filters.AddGetPromptFilter(next => async (request, ct) =>
            {
                // Log the request once, including failed lookups. Never inspect prompt arguments.
                DiagnosticLog.Write("Prompts", Format(request.Params?.Name,
                    request.Server.SessionId ?? "stdio", DateTimeOffset.UtcNow));
                return await next(request, ct);
            }));

        internal static string Format(string name, string sessionId, DateTimeOffset utc)
            => "prompts/get name=" + name + " utc=" + utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                + " session=" + sessionId;
    }
}
