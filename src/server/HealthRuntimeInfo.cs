#nullable enable
using System;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server;

internal static class HealthRuntimeInfo
{
    internal const string ClientRuntimeMetadataKey = "bimwright/rvt-mcp/runtime";

    internal static JObject ClientRuntime(JObject gateway) => new()
    {
        ["server_informational_version"] = gateway["assembly_informational_version"]?.DeepClone(),
        ["module_version_id"] = gateway["module_version_id"]?.DeepClone(),
        ["process_id"] = gateway["process_id"]?.DeepClone(),
        ["process_started_utc"] = gateway["process_started_utc"]?.DeepClone(),
    };

    internal static void Enrich(JObject health, JObject gateway, bool connected)
    {
        var plugin = connected ? health["setup_plugin"] as JObject : null;
        var serverInfo = Text(gateway, "assembly_informational_version");
        var pluginInfo = Text(plugin, "assembly_informational_version");
        var serverVersion = VersionOf(serverInfo) ?? Program.ServerVersion;
        var pluginVersion = VersionOf(pluginInfo);
        var serverRevision = RevisionOf(serverInfo);
        var pluginRevision = RevisionOf(pluginInfo);
        health["host_connected"] = connected;
        health["setup_gateway"] = gateway.DeepClone();
        health["mcp"] = new JObject
        {
            ["name"] = "rvt-mcp",
            ["server_version"] = serverVersion,
            ["server_informational_version"] = serverInfo,
            ["plugin_version"] = pluginVersion,
            ["server_build_id"] = BuildId(serverInfo, gateway),
            ["plugin_build_id"] = BuildId(pluginInfo, plugin),
            ["version_alignment"] = pluginVersion is null ? "unknown"
                : serverVersion == pluginVersion ? "match" : "mismatch",
            ["revision_alignment"] = serverRevision is null || pluginRevision is null ? "unknown"
                : string.Equals(serverRevision, pluginRevision, StringComparison.OrdinalIgnoreCase) ? "match" : "mismatch",
        };
        health["client_verification"] = new JObject
        {
            ["status"] = "not_observable_by_server",
            ["expected_server_info"] = new JObject { ["name"] = "rvt-mcp", ["version"] = serverInfo },
            ["tools_list_metadata_key"] = ClientRuntimeMetadataKey,
            ["expected_runtime"] = ClientRuntime(gateway),
        };
    }

    internal static JObject UpdateGuidance(JObject update)
    {
        var notification = update["notification"] as JObject;
        var proposalAllowed = notification?.Value<bool?>("bundle_update_proposal_allowed") == true;
        var mode = update.Value<string>("status") == "not_checked" ? "check_only"
            : update.Value<string>("status") == "unknown" ? "retry_check"
            : proposalAllowed ? "prepare_update" : notification is not null ? "review" : "none";
        JArray steps;
        if (mode == "prepare_update")
            steps = new JArray(
                "Keep the exact confirmed release tag, download its official Setup ZIP and companion .zip.sha256 asset, and compare the ZIP SHA-256 before extraction. Require manifest.json; the installer preview validates extracted file checksums. Health provides no ZIP digest and performs no package verification. Never automatically replace a development build.",
                "Preview install.ps1 -WhatIf and obtain or reuse user authorization. Close every Revit instance manually; never save, close documents or kill processes automatically to enable an update.",
                "Run the installer with rollback protection and preserve client configuration and personal data. Restart Revit and reconnect/restart the MCP client; record a new initialize serverInfo and fresh tools/list on the same connection.",
                "Call revit_health on that connection and compare client_verification.expected_server_info/expected_runtime with the fresh handshake and catalog, including schema and annotations. Verify the loaded plugin; unavailable client evidence remains unverified.");
        else if (mode == "check_only")
            steps = new JArray(
                "The release check was not requested. Report the current runtime observations without claiming the release is current.",
                "If network access is permitted and release comparison is needed, call revit_health(check_updates=true). No installation is proposed and no installation authorization is needed for this check.");
        else if (mode == "retry_check")
            steps = new JArray(
                notification?.Value<string>("message") ?? "The official release lookup is unavailable; reported runtime identities are unchanged.",
                "Report the lookup reason and retry the check when access is available. Do not infer a local runtime fault or request installation authorization.");
        else if (mode == "review")
            steps = new JArray(
                notification!.Value<string>("message") ?? "Review the available runtime and release comparison evidence.",
                "Read both update.components, lookup_reason, protected_component_reasons, unobserved_components and the full notification message; notification.components is only the primary affected set. Preserve known newer or unresolved observed builds and report unobserved identities separately. No installation is proposed.",
                "If comparison_reason=release_revision_unavailable, report the external lookup_reason and obtain missing release evidence without inferring a runtime fault. Stop the update workflow; otherwise healthy Revit work can continue.");
        else
            steps = new JArray("Report the completed component comparison and its evidence limits. No update action or installation authorization is requested.");
        return new JObject
        {
            ["mode"] = mode,
            ["automatic_update_supported"] = false,
            ["requires_user_authorization"] = mode == "prepare_update",
            ["documentation_url"] = "https://github.com/bimwright/rvt-mcp/blob/master/AGENTS.md",
            ["steps"] = steps,
        };
    }

    internal static string? Text(JObject? identity, string key)
        => identity?[key] is { Type: JTokenType.String } value ? (string?)value : null;

    internal static string? VersionOf(string? informational)
        => string.IsNullOrWhiteSpace(informational) ? null : informational.Split('+')[0];

    internal static string? BuildId(string? informational, JObject? identity)
        => informational?.Split('+', 2) is { Length: 2 } parts && parts[1].Length > 0 ? parts[1]
            : Text(identity, "module_version_id") is { } mvid ? "mvid:" + mvid : null;

    internal static string? RevisionOf(string? informational)
        => informational?.Split('+', 2) is { Length: 2 } parts
            && Regex.IsMatch(parts[1], "\\A[0-9a-fA-F]{40}([0-9a-fA-F]{24})?\\z") ? parts[1] : null;
}
