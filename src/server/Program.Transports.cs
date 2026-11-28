using System;
using System.Linq;
using System.Threading.Tasks;
using RvtMcp.Plugin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

namespace RvtMcp.Server
{
    partial class Program
    {
        private static async Task RunStdio(RvtMcpConfig config)
        {
            var enabled = ToolsetFilter.Resolve(config);
            var builder = Host.CreateApplicationBuilder();
            RegisterRuntimeMaintenance(builder.Services, config);
            // stdio MCP stdout must contain JSON-RPC only. ClearProviders() removes default
            // host logging, but the MCP SDK (and any transitive package) may re-add a Console
            // provider that writes to stdout. AddConsole with LogToStandardErrorThreshold=Trace
            // forces every console log line — from any provider re-added downstream — to stderr.
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(opts => opts.LogToStandardErrorThreshold = LogLevel.Trace);
            var mcp = builder.Services
                .AddMcpServer(ConfigureMcpServerOptions)
                .WithStdioServerTransport();
            mcp = RegisterMcpSurface(mcp, enabled, config);
            var app = builder.Build();
            await app.RunAsync();
        }

        // SDK-RVT-HTTP (BW-MCP-UPGRADE-001): SDK 2.x Streamable HTTP defaults to Stateless for MCP 2026-07-28 (SEP-2567).
        // Set it explicitly here; legacy /sse, Mcp-Session-Id, and server-to-client session requests remain disabled,
        // and external HTTP clients (Ollama, LM Studio, custom tools) need live verification separately.
        internal static void ConfigureHttpTransport(HttpServerTransportOptions opts)
        {
            opts.SessionMode = HttpServerSessionMode.Stateless;
        }

        internal static string FormatHttpStartupMessage(string listenUrl)
            => $"[RvtMcp] Streamable HTTP (stateless) MCP server listening on {listenUrl}";

        internal static WebApplication BuildHttpApp(RvtMcpConfig config, string listenUrl, bool registerMaintenance = true)
        {
            var enabled = ToolsetFilter.Resolve(config);
            var builder = WebApplication.CreateBuilder();
            if (registerMaintenance)
                RegisterRuntimeMaintenance(builder.Services, config);
            var mcp = builder.Services
                .AddMcpServer(ConfigureMcpServerOptions)
                .WithHttpTransport(ConfigureHttpTransport);
            mcp = RegisterMcpSurfaceWithTransport(mcp, enabled, config, "http");

            builder.WebHost.UseUrls(listenUrl);

            var app = builder.Build();

            app.Use(async (context, next) =>
            {
                var host = context.Request.Host.Host;
                if (host != "127.0.0.1" && host != "localhost")
                {
                    context.Response.StatusCode = 403;
                    context.Response.ContentType = "text/plain";
                    await context.Response.WriteAsync("Forbidden: non-localhost host");
                    return;
                }
                await next();
            });

            app.MapMcp();
            return app;
        }

        private static async Task RunHttp(RvtMcpConfig config, int port)
        {
            var enabled = ToolsetFilter.Resolve(config);
            var listenUrl = $"http://127.0.0.1:{port}";
            var app = BuildHttpApp(config, listenUrl);

            Console.Error.WriteLine(FormatHttpStartupMessage(listenUrl));
            Console.Error.WriteLine($"[RvtMcp] Toolsets enabled: {string.Join(",", enabled.OrderBy(n => n))}");
            await app.RunAsync();
        }
    }
}
