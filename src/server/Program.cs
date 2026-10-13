// Usage:
//   stdio (default):  RvtMcp.Server.exe              — spawned by Claude/GPT/Cursor
//   HTTP SSE:          RvtMcp.Server.exe --http 8200  — for Ollama/LM Studio/custom
using System;
using System.Linq;
using System.Threading.Tasks;
using RvtMcp.Plugin;

namespace RvtMcp.Server
{
    partial class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Any(a => a == "--help" || a == "-h"))
            {
                PrintHelp();
                return;
            }

            // A9 3-layer config precedence (JSON < env < CLI). The --target selector and
            // transport mode (--http) stay as separate CLI parses for now; A3 toolsets
            // gating uses RvtMcpConfig.
            if (!LegacyDataMigration.TryMigrateOnce(Console.Error.WriteLine))
            { Environment.ExitCode = 1; return; }
            AuthToken.CleanupLegacyDiscoveryFiles();
            var config = RvtMcpConfig.Load(args);
            config.ValidateResponseLimits();
            ServerState.Config = config;
            ServerState.ToolCatalog = ServerToolCatalogBuilder.Build(
                ToolsetFilter.Resolve(config), config);
            if (!string.IsNullOrWhiteSpace(config.Target))
            {
                if (!TryResolveTargetSelector(config.Target.Trim(), out var selector, out var targetError))
                {
                    Console.Error.WriteLine(
                        "[RvtMcp] Invalid --target value '" + config.Target + "'. " + targetError);
                    Environment.Exit(1);
                    return;
                }
                RevitTargetBinding.Initialize(selector);
            }

            InitializeRuntime(config);

            int httpIndex = Array.IndexOf(args, "--http");
            if (httpIndex >= 0)
            {
                if (httpIndex + 1 >= args.Length || !int.TryParse(args[httpIndex + 1], out var port)
                    || port < 1 || port > 65535)
                {
                    Console.Error.WriteLine("[RvtMcp] Invalid --http argument. Expected: --http <port> (1-65535)");
                    Environment.Exit(1);
                    return;
                }
                await RunHttpSse(config, port);
            }
            else
            {
                await RunStdio(config);
            }
        }
    }
}
