using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("meta")]
    public static class HealthTools
    {
        [McpServerTool(Name = "revit_health", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true), Description(
            "Read gateway/plugin identity and last observed host state after selecting a target; defaults offline, with check_updates=true opting into a release lookup. " +
            "Observations do not authorize writes: verify writable sessions with revit_switch_target; in --read-only select --target at startup. Follow update_guidance/client_verification, treat missing evidence as unknown, and never auto-install or downgrade.")]
        public static Task<CallToolResult> Health(bool check_updates = false, CancellationToken cancellationToken = default)
            => Probe(check_updates, cancellationToken, new ReleaseUpdateCheck(), GatewayRuntime.Identity);

        internal static async Task<CallToolResult> Probe(bool checkUpdates, CancellationToken ct,
            ReleaseUpdateCheck updates, JObject gateway)
        {
            ct.ThrowIfCancellationRequested();
            JObject health;
            var connected = false;
            try
            {
                health = await ToolGateway.SendToRevit("health", new { }, timeoutSeconds: 10);
                if (health == null || health["ok"]?.Type != JTokenType.Boolean)
                    health = Failure("INVALID_HEALTH_RESPONSE", "The plugin returned invalid health data.");
                else
                {
                    connected = true;
                    health["health_supported"] = true;
                }
            }
            catch (TargetException ex)
            {
                health = (JObject)MetaTools.TargetEnvelope(ex.Payload, ex.Operation).DeepClone();
                health["ok"] = false;
                // The health reply carries this refusal; keep its gateway identity through the tool filter.
                if (TargetCallContext.Current is { } context)
                { context.Payload = null; context.Operation = null; }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.IO.IOException || ex is JsonException)
            {
                ct.ThrowIfCancellationRequested();
                var legacy = ex is InvalidOperationException
                    && (ex.Message.StartsWith("Unknown command: health", StringComparison.Ordinal)
                        || ex.Message.Contains("does not know the command 'health'", StringComparison.Ordinal));
                connected = legacy;
                health = Failure(legacy ? "HEALTH_UNSUPPORTED" : "TARGET_UNAVAILABLE",
                    legacy ? "The responding add-in does not support health; its loaded identity is unverified."
                        : "Health probe failed. Verify the intended Revit connection; no model operation was started.");
                if (legacy) health["health_supported"] = false;
            }
            ct.ThrowIfCancellationRequested();
            HealthRuntimeInfo.Enrich(health, gateway, connected);
            var plugin = connected ? health["setup_plugin"] as JObject : null;
            health["update"] = checkUpdates ? await updates.CheckAsync(gateway, plugin, ct)
                : ReleaseUpdateCheck.NotChecked(gateway, plugin);
            health["update_guidance"] = HealthRuntimeInfo.UpdateGuidance((JObject)health["update"]);
            var result = new CallToolResult { IsError = health.Value<bool?>("ok") != true };
            result.Content.Add(new TextContentBlock { Text = health.ToString(Formatting.None) });
            return result;
        }

        private static JObject Failure(string code, string message) => new JObject
        { ["ok"] = false, ["error"] = new JObject { ["code"] = code, ["message"] = message } };
    }
}
