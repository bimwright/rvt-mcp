#nullable enable
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server;

internal sealed class ReleaseUpdateCheck
{
    internal const int MaxResponseBytes = 256 * 1024;
    private const string RepositoryApi = "https://api.github.com/repos/bimwright/rvt-mcp";
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly HttpClient _client;

    internal ReleaseUpdateCheck() : this(SharedClient) { }
    internal ReleaseUpdateCheck(HttpClient client) => _client = client;

    internal static JObject NotChecked(JObject gateway, JObject? plugin) => new()
    {
        ["status"] = "not_checked", ["comparison_basis"] = "semantic_version_and_release_source_revision",
        ["latest_version"] = null, ["release_revision"] = null, ["update_available"] = null, ["release_url"] = null,
        ["artifact_verification"] = "not_performed",
        ["notification"] = null,
        ["components"] = new JObject { ["server"] = Component(gateway), ["plugin"] = Component(plugin) },
    };

    internal async Task<JObject> CheckAsync(JObject gateway, JObject? plugin, CancellationToken ct)
    {
        var result = NotChecked(gateway, plugin);
        result["status"] = "unknown";
        result["checked_at_utc"] = DateTimeOffset.UtcNow.ToString("o");
        foreach (var component in ((JObject)result["components"]!).Properties())
        {
            component.Value["status"] = "unknown";
            component.Value["comparison_reason"] = "release_lookup_unavailable";
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // One shared budget covers latest release, tag ref and (at most one) annotated tag.
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var release = await GetMetadataAsync("/releases/latest", timeout.Token);
            if (release["draft"]?.Type != JTokenType.Boolean || release["prerelease"]?.Type != JTokenType.Boolean
                || release.Value<bool>("draft") || release.Value<bool>("prerelease")
                || release["tag_name"]?.Type != JTokenType.String)
                throw new InvalidDataException("The API did not return a published stable release.");
            var tag = release.Value<string>("tag_name")!;
            var latest = tag.StartsWith('v') ? tag.Substring(1) : tag;
            if (!TryVersion(latest, out var latestVersion))
                throw new InvalidDataException("Release version is not a supported semantic version.");
            result["status"] = "checked";
            result["latest_version"] = latest;
            // Never forward API-provided URLs, release notes or target_commitish (which may be a branch).
            result["release_url"] = "https://github.com/bimwright/rvt-mcp/releases/tag/" + tag;
            string? revision = null;
            try { revision = await ReleaseRevisionAsync(tag, timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { result["build_reason"] = "timeout"; }
            catch (Exception ex) when (IsMetadataFailure(ex)) { result["build_reason"] = FailureReason(ex); }
            result["release_revision"] = revision;
            Compare((JObject)result["components"]!["server"]!, gateway, latestVersion!, revision);
            Compare((JObject)result["components"]!["plugin"]!, plugin, latestVersion!, revision);
            var server = (JObject)result["components"]!["server"]!;
            var addin = (JObject)result["components"]!["plugin"]!;
            // A missing plugin or an unmatched development build cannot yield a blanket "up to date".
            result["update_available"] = server.Value<bool?>("update_available") == true || addin.Value<bool?>("update_available") == true
                ? true : server.Value<string>("status") == "release_match" && addin.Value<string>("status") == "release_match" ? false : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { result["reason"] = "timeout"; }
        catch (Exception ex) when (IsMetadataFailure(ex)) { result["reason"] = FailureReason(ex); }
        result["notification"] = Notification(result);
        return result;
    }

    private static JObject? Notification(JObject result)
    {
        var older = new JArray();
        var review = new JArray();
        var ahead = new JArray();
        var unobserved = new JArray();
        var protectedComponents = new JArray();
        var protectionReasons = new JObject();
        var details = new System.Collections.Generic.List<string>();
        var lookupReason = result.Value<string>("reason") ?? result.Value<string>("build_reason");
        var lookupUnavailable = result.Value<string>("status") == "unknown";
        var mismatch = false;
        foreach (var entry in ((JObject)result["components"]!).Properties())
        {
            var component = (JObject)entry.Value;
            var status = component.Value<string>("status");
            if (component.Value<bool?>("update_available") == true) older.Add(entry.Name);
            if (lookupUnavailable) continue; // A failed lookup makes no assertion about local runtime health.
            if (!component.Value<bool>("identity_observed"))
            {
                unobserved.Add(entry.Name);
                review.Add(entry.Name);
                details.Add(entry.Name + " runtime identity was not observed; verify the intended Revit connection and obtain runtime metadata.");
                continue;
            }
            if (status is "release_mismatch" or "unknown") review.Add(entry.Name);
            if (status == "ahead_of_release")
            {
                ahead.Add(entry.Name);
                details.Add(entry.Name + " version " + component.Value<string>("current_version")
                    + " is newer than stable release " + result.Value<string>("latest_version") + "; preserve this local component.");
            }
            if (status is "ahead_of_release" or "release_mismatch" or "unknown")
            {
                var reason = status == "unknown" ? component.Value<string>("comparison_reason") : status;
                if (reason == "release_revision_unavailable")
                    details.Add("Release revision comparison is unavailable for " + entry.Name + ".");
                else
                {
                    protectedComponents.Add(entry.Name);
                    protectionReasons[entry.Name] = reason;
                    if (status == "release_mismatch")
                        details.Add(entry.Name + " has the same release version but a different source revision; review the observed build before replacement.");
                    if (status == "unknown")
                        details.Add(entry.Name + " was observed, but " + reason + "; obtain the missing runtime evidence before replacement.");
                }
            }
            mismatch |= status == "release_mismatch";
        }
        if (!lookupUnavailable && older.Count == 0 && review.Count == 0 && ahead.Count == 0) return null;
        var proposalAllowed = !lookupUnavailable && lookupReason is null && older.Count > 0
            && protectedComponents.Count == 0 && unobserved.Count == 0;
        var affected = lookupUnavailable ? new JArray() : older.Count > 0 ? older : review.Count > 0 ? review : ahead;
        var code = lookupUnavailable ? "UPDATE_CHECK_UNAVAILABLE" : older.Count > 0 ? "NEW_RELEASE_AVAILABLE"
            : mismatch ? "RELEASE_REVIEW_REQUIRED" : review.Count > 0 ? "RELEASE_IDENTITY_UNVERIFIED" : "LOCAL_VERSION_AHEAD";
        var message = lookupUnavailable
            ? "Official release lookup is unavailable (" + lookupReason + "). Reported runtime identities are unchanged; no release comparison or installation recommendation is available."
            : older.Count > 0
                ? "rvt-mcp " + result.Value<string>("latest_version") + " is available for: " + string.Join(", ", older.Values<string>()) + "."
                : code == "LOCAL_VERSION_AHEAD" ? "Local version is ahead of the published stable release."
                : "Review the runtime/release comparison.";
        if (!lookupUnavailable && lookupReason is not null)
            message += " Release revision lookup is unavailable (" + lookupReason + "); reported runtime identities are unchanged.";
        if (details.Count > 0) message += " " + string.Join(" ", details);
        if (!proposalAllowed && !lookupUnavailable) message += " No full Setup ZIP installation is proposed.";
        return new JObject
        {
            ["code"] = code, ["components"] = affected, ["message"] = message,
            ["lookup_reason"] = lookupReason,
            ["protected_components"] = protectedComponents, ["protected_component_reasons"] = protectionReasons,
            ["unobserved_components"] = unobserved,
            ["bundle_update_proposal_allowed"] = proposalAllowed,
            ["agent_action"] = lookupUnavailable ? "report_release_check_unavailable"
                : proposalAllowed ? "notify_user_and_propose_update" : "notify_user_and_review_update_state",
            ["requires_user_authorization"] = proposalAllowed,
        };
    }

    private static JObject Component(JObject? identity)
    {
        var informational = HealthRuntimeInfo.Text(identity, "assembly_informational_version");
        return new JObject
        {
            ["status"] = "not_checked", ["current_version"] = HealthRuntimeInfo.VersionOf(informational),
            ["identity_observed"] = identity is not null, ["comparison_reason"] = "not_checked",
            ["current_build_id"] = HealthRuntimeInfo.BuildId(informational, identity),
            ["version_status"] = "unknown", ["build_status"] = "unknown", ["update_available"] = null,
            ["update_available_basis"] = "semantic_version",
        };
    }

    private static void Compare(JObject component, JObject? identity, Version latest, string? releaseRevision)
    {
        component["status"] = "unknown";
        var informational = HealthRuntimeInfo.Text(identity, "assembly_informational_version");
        var version = HealthRuntimeInfo.VersionOf(informational);
        component["comparison_reason"] = identity is null ? "runtime_identity_not_observed" : "runtime_version_unavailable";
        if (version is null || !TryVersion(version.Split('-')[0], out var current)) return;
        var older = latest > current || latest == current && version.Contains('-');
        component["version_status"] = older ? "older" : latest == current ? "match" : "newer";
        component["update_available"] = older; // A newer local version never recommends a downgrade.
        var revision = HealthRuntimeInfo.RevisionOf(informational);
        var build = revision is null || releaseRevision is null ? "unknown"
            : string.Equals(revision, releaseRevision, StringComparison.OrdinalIgnoreCase) ? "match" : "mismatch";
        component["build_status"] = build;
        component["comparison_reason"] = releaseRevision is null ? "release_revision_unavailable"
            : revision is null ? "runtime_revision_unavailable" : null;
        component["status"] = older ? "update_available" : latest < current ? "ahead_of_release"
            : build == "match" ? "release_match" : build == "mismatch" ? "release_mismatch" : "unknown";
    }

    private async Task<string> ReleaseRevisionAsync(string tag, CancellationToken ct)
    {
        var reference = await GetMetadataAsync("/git/ref/tags/" + tag, ct);
        if (HealthRuntimeInfo.Text(reference, "ref") != "refs/tags/" + tag) throw new InvalidDataException("Unexpected tag reference.");
        var target = reference["object"] as JObject;
        if (HealthRuntimeInfo.Text(target, "type") == "tag")
        {
            var tagObject = await GetMetadataAsync("/git/tags/" + ValidRevision(target), ct);
            if (HealthRuntimeInfo.Text(tagObject, "tag") != tag) throw new InvalidDataException("Unexpected annotated tag.");
            target = tagObject["object"] as JObject;
        }
        if (HealthRuntimeInfo.Text(target, "type") != "commit") throw new InvalidDataException("Tag does not resolve to a commit.");
        return ValidRevision(target);
    }

    private static string ValidRevision(JObject? value)
    {
        var sha = HealthRuntimeInfo.Text(value, "sha");
        return sha is not null && Regex.IsMatch(sha, "\\A[0-9a-fA-F]{40}([0-9a-fA-F]{24})?\\z")
            ? sha.ToLowerInvariant() : throw new InvalidDataException("Invalid tag revision.");
    }

    private async Task<JObject> GetMetadataAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RepositoryApi + path);
        request.Headers.UserAgent.ParseAdd("rvt-mcp/" + Program.ServerVersion);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Release API failed.", null, response.StatusCode);
        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new InvalidDataException("Metadata exceeds the response limit.");
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (bytes.Length + count > MaxResponseBytes) throw new InvalidDataException("Metadata exceeds the response limit.");
            bytes.Write(buffer, 0, count);
        }
        using var reader = new JsonTextReader(new StringReader(Encoding.UTF8.GetString(bytes.ToArray())))
            { MaxDepth = 32, DateParseHandling = DateParseHandling.None };
        var metadata = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read()) throw new InvalidDataException("Trailing metadata.");
        return metadata;
    }

    private static bool IsMetadataFailure(Exception ex) => ex is HttpRequestException or IOException or InvalidDataException or JsonException;
    private static string FailureReason(Exception ex) => ex is HttpRequestException http
        ? http.StatusCode is { } code ? "http_" + (int)code : "network_unavailable" : "invalid_release_metadata";

    private static bool TryVersion(string value, out Version? version)
    {
        version = null;
        return Regex.IsMatch(value, "\\A(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\z")
            && Version.TryParse(value, out version);
    }
}
