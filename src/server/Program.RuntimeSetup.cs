using System;
using System.Collections.Generic;
using System.Threading;
using Bimwright.Targeting;
using RvtMcp.Plugin;
using RvtMcp.Server.Bake;
using Microsoft.Extensions.DependencyInjection;

namespace RvtMcp.Server
{
    partial class Program
    {
        private static void InitializeRuntime(RvtMcpConfig config)
        {
            _ = GatewayRuntime.Identity;
            // Warm up the target-binding code paths on a background thread so the
            // first tool call's connect+handshake isn't penalized by one-time JIT.
            // The scan may delete proven-dead descriptor files (same as a list call)
            // but must not bind, connect, or change binding state.
            new Thread(WarmUpTargetBinding) { IsBackground = true, Name = "RvtMcp.TargetWarmup" }.Start();

            var bakePaths = new BakePaths();
            TryInitializeBakeStorage(bakePaths, out _);

            // Initialize memory system (shared across tool classes + resources)
            var session = new Memory.SessionContext(config.EnableCallLogOrDefault);
            ToolGateway.Session = session;
            ToolGateway.History = new Memory.ChangeHistoryStore();
            ToolGateway.UsageLogger = new UsageEventLogger(bakePaths, config);
            RevitResources.Session = session;
        }

        /// <summary>JIT warm-up: exercise selector parse, descriptor scan/probe,
        /// binding plan and the §6.10 envelope paths once, off the tool-call path.</summary>
        private static void WarmUpTargetBinding()
        {
            try
            {
                SelectorParser.TryParse(HostProduct.Revit, "auto", out _, out _, out _);
                var scan = RevitTargetBinding.Scan();
                var binding = RevitTargetBinding.Binding;
                binding.CaptureGeneration();
                binding.PlanCall(0, scan.Live);
                MetaTools.TargetEnvelope(new Bimwright.Targeting.TargetPayload
                {
                    Code = Bimwright.Targeting.TargetCode.NoTarget,
                    Message = "warmup",
                    NextStep = "warmup",
                    Candidates = scan.Live.Count == 0
                        ? new List<Bimwright.Targeting.CandidateInfo>
                            { new Bimwright.Targeting.CandidateInfo("warmup", 2024, 0, false, null) }
                        : Array.Empty<Bimwright.Targeting.CandidateInfo>()
                });
                ToolGateway.WarmUpWire();
            }
            catch { /* warm-up is best-effort */ }
        }

        internal static LegacyBakedToolImportResult InitializeBakeStorage(BakePaths paths)
        {
            if (paths == null)
                throw new ArgumentNullException(nameof(paths));

            using var db = new BakeDb(paths);
            db.Migrate();
            var importer = new LegacyBakedToolImporter(paths, db, new ToolBakerAuditLog(paths.AuditJsonl));
            return importer.ImportIfNeeded();
        }

        internal static bool TryInitializeBakeStorage(BakePaths paths, out LegacyBakedToolImportResult result)
        {
            try
            {
                result = InitializeBakeStorage(paths);
                return true;
            }
            catch (Exception ex)
            {
                result = null;
                var pathHint = paths?.Root ?? "(unknown path)";
                Console.Error.WriteLine(
                    $"[RvtMcp] Warning: ToolBaker bake storage initialization failed for {pathHint}. " +
                    "The MCP server will continue; baked-tool migration/import can be retried on next startup. " +
                    $"{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        internal static void RegisterRuntimeMaintenance(IServiceCollection services, RvtMcpConfig config)
        {
            var spill = ResponseSpillWriter.ForConfig(config);
            services.AddHostedService(_ => new SpillCleanupService(() => spill.Cleanup(DateTime.UtcNow)));
            if (config.EnableChangeHistoryOrDefault && !config.ReadOnlyOrDefault)
                services.AddHostedService(_ => new Memory.HistoryRecoveryService(ToolGateway.History));
        }

        /// <summary>Parse a --target/BIMWRIGHT_TARGET/config selector (§6.1); R-codes get the educational error.</summary>
        internal static bool TryResolveTargetSelector(string raw, out TargetSelector selector, out string error)
        {
            if (SelectorParser.TryParse(HostProduct.Revit, raw, out selector, out var selError, out var selMessage))
            {
                error = null;
                return true;
            }
            error = "Expected: auto | a 4-digit Revit calendar year 2022..2027 | pid:<n> | id:revit-<year>-<pid>. "
                + (selError == SelectorError.RCode
                    ? "Note: legacy R-codes (R22..R27) are no longer accepted in v0.5; use the year directly."
                    : selMessage ?? "Unrecognized selector.");
            return false;
        }
    }
}
