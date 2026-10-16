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
            "At session start select the intended Revit target, then call revit_health. The default stays offline; " +
            "check_updates=true explicitly checks the official stable release with a bounded deadline. " +
            "Reports loaded gateway/plugin versions, build ids, alignment and host state without requiring a model. " +
            "The listener can reply while Revit is modal. Document fields are the last UI observation, with " +
            "document_observed_utc and state_probe; they do not confirm the current document or readiness for writes. " +
            "Use revit_switch_target verification before model changes. No target retains gateway identity with " +
            "ok:false and host_connected:false. Missing runtime/release evidence stays unknown, never up to date. " +
            "Inspect update.components, notification.message, lookup_reason, protected_component_reasons and " +
            "unobserved_components. Follow update_guidance: only prepare_update with " +
            "bundle_update_proposal_allowed=true permits an update proposal; reuse existing installation permission. " +
            "Never downgrade or automatically replace a development build. Matching revisions do not verify artifact " +
            "bytes, compatibility or publisher trust. After reconnect compare fresh initialize serverInfo and " +
            "tools/list runtime metadata, schema and annotations with client_verification; the server cannot prove " +
            "client cache refresh. Read-only; never downloads packages, installs, saves/closes models, changes " +
            "client configuration or restarts processes.")]
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
