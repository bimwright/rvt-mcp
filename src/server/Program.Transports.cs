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

namespace RvtMcp.Server
{
    partial class Program
    {
        private static async Task RunStdio(RvtMcpConfig config)
        {
            var enabled = ToolsetFilter.Resolve(config);
            var builder = Host.CreateApplicationBuilder();
            RegisterHistoryRecovery(builder.Services, config);
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

        private static async Task RunHttpSse(RvtMcpConfig config, int port)
        {
            var enabled = ToolsetFilter.Resolve(config);
            var builder = WebApplication.CreateBuilder();
            RegisterHistoryRecovery(builder.Services, config);
            var mcp = builder.Services
                .AddMcpServer(ConfigureMcpServerOptions)
                .WithHttpTransport();
            mcp = RegisterMcpSurface(mcp, enabled, config);

            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

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

            Console.Error.WriteLine($"[RvtMcp] SSE server listening on http://127.0.0.1:{port}");
            Console.Error.WriteLine($"[RvtMcp] Toolsets enabled: {string.Join(",", enabled.OrderBy(n => n))}");
            await app.RunAsync();
        }
    }
}
