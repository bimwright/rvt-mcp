#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.Net;
using System.Text;
using RvtMcp.Server;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Tests;

public sealed class ReleaseUpdateCheckTests
{
    internal sealed class Handler : HttpMessageHandler
    {
        internal int Calls;
        internal HttpStatusCode Status = HttpStatusCode.OK;
        internal string Body = """{"tag_name":"v0.2.2","draft":false,"prerelease":false}""";
        internal string Revision = new('a', 40);
        internal string? RefBody;
        internal string? TagBody;
        internal HttpStatusCode RefStatus = HttpStatusCode.OK;
        internal Func<CancellationToken, Task>? BeforeResponse;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Null(request.Headers.Authorization);
            if (BeforeResponse is not null) await BeforeResponse(ct);
            var path = request.RequestUri.AbsolutePath;
            string body;
            var status = RefStatus;
            if (path == "/repos/bimwright/rvt-mcp/releases/latest") { body = Body; status = Status; }
            else if (path.StartsWith("/repos/bimwright/rvt-mcp/git/ref/tags/", StringComparison.Ordinal))
            {
                var tag = path.Split('/').Last();
                body = RefBody ?? new JObject { ["ref"] = "refs/tags/" + tag,
                    ["object"] = new JObject { ["type"] = "commit", ["sha"] = Revision } }.ToString();
            }
            else
            {
                Assert.Equal("/repos/bimwright/rvt-mcp/git/tags/" + new string('b', 40), path);
                body = TagBody!;
            }
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Theory]
    [InlineData("0.2.1", "v0.2.2", true)]
    [InlineData("0.2.9", "v0.2.10", true)]
    [InlineData("0.2.2", "v0.2.2", false)]
    [InlineData("0.3.0", "v0.2.2", false)]
    [InlineData("0.2.2-beta.1", "v0.2.2", true)]
    [InlineData("0.2.2+build", "v0.2.2", false)]
    public async Task Stable_release_comparison_uses_numeric_semver_and_never_recommends_a_downgrade(
        string current, string tag, bool expected)
    {
        using var client = new HttpClient(new Handler { Body = new JObject
        {
            ["tag_name"] = tag, ["draft"] = false, ["prerelease"] = false,
            ["html_url"] = "https://untrusted.invalid/installer", ["body"] = "untrusted release instructions",
        }.ToString() });
        var result = await Check(client, current);
        Assert.Equal("checked", result.Value<string>("status"));
        Assert.Equal("semantic_version_and_release_source_revision", result.Value<string>("comparison_basis"));
        Assert.Equal(expected, result["components"]!["server"]!.Value<bool>("update_available"));
        Assert.Equal(expected, result["components"]!["plugin"]!.Value<bool>("update_available"));
        if (!expected) Assert.Null(result.Value<bool?>("update_available"));
        Assert.Equal("https://github.com/bimwright/rvt-mcp/releases/tag/" + tag, result.Value<string>("release_url"));
        Assert.DoesNotContain("untrusted", result.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Found)]
    public async Task API_errors_and_redirects_report_unknown_instead_of_up_to_date(HttpStatusCode status)
    {
        using var client = new HttpClient(new Handler { Status = status });
        var result = await Check(client);
        Assert.Equal("unknown", result.Value<string>("status"));
        Assert.Null(result["update_available"]!.Value<bool?>());
        Assert.Null(result["latest_version"]!.Value<string>());
        Assert.Equal("http_" + (int)status, result.Value<string>("reason"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"tag_name\":\"v0.2.2\",\"draft\":true,\"prerelease\":false}")]
    [InlineData("{\"tag_name\":\"v0.2.2-beta\",\"draft\":false,\"prerelease\":true}")]
    [InlineData("{\"tag_name\":\"../unexpected\",\"draft\":false,\"prerelease\":false}")]
    [InlineData("{\"tag_name\":\"v01.2.3\",\"draft\":false,\"prerelease\":false}")]
    [InlineData("{\"tag_name\":\"v0.2.2\",\"tag_name\":\"v0.2.3\",\"draft\":false,\"prerelease\":false}")]
    public async Task Invalid_metadata_does_not_produce_a_verified_release(string body)
    {
        using var client = new HttpClient(new Handler { Body = body });
        var result = await Check(client);
        Assert.Equal("unknown", result.Value<string>("status"));
        Assert.Null(result["update_available"]!.Value<bool?>());
        Assert.Equal("invalid_release_metadata", result.Value<string>("reason"));
    }

    [Fact]
    public async Task Metadata_size_is_bounded()
    {
        using var client = new HttpClient(new Handler { Body = new string('x', ReleaseUpdateCheck.MaxResponseBytes + 1) });
        var result = await Check(client);
        Assert.Equal("unknown", result.Value<string>("status"));
        Assert.Equal("invalid_release_metadata", result.Value<string>("reason"));
    }

    [Fact]
    public async Task Network_failure_does_not_turn_into_a_no_update_result()
    {
        using var client = new HttpClient(new Handler { BeforeResponse = _ => throw new HttpRequestException("offline") });
        var result = await Check(client);
        Assert.Equal("unknown", result.Value<string>("status"));
        Assert.Equal("network_unavailable", result.Value<string>("reason"));
        Assert.Null(result["update_available"]!.Value<bool?>());
    }

    [Fact]
    public async Task Timeout_is_unknown_but_caller_cancellation_is_propagated()
    {
        using var client = new HttpClient(new Handler { BeforeResponse = ct => Task.Delay(Timeout.Infinite, ct) })
            { Timeout = TimeSpan.FromMilliseconds(30) };
        var checker = new ReleaseUpdateCheck(client);
        var result = await checker.CheckAsync(Identity("0.2.1"), null, CancellationToken.None);
        Assert.Equal("timeout", result.Value<string>("reason"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checker.CheckAsync(Identity("0.2.1"), null, cancelled.Token));
    }

    internal static JObject Identity(string version) => new() { ["assembly_informational_version"] = version };
    private static Task<JObject> Check(HttpClient client, string current = "0.2.1")
        => new ReleaseUpdateCheck(client).CheckAsync(Identity(current), Identity(current), CancellationToken.None);

    [Theory]
    [InlineData("0.2.1+aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "update_available", "older")]
    [InlineData("0.2.2+bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "release_mismatch", "match")]
    [InlineData("0.2.2+aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "release_match", "match")]
    [InlineData("0.2.2+development", "unknown", "match")]
    public async Task Current_server_does_not_hide_older_or_different_plugin(string plugin, string status, string versionStatus)
    {
        var handler = new Handler();
        using var client = new HttpClient(handler);
        var result = await new ReleaseUpdateCheck(client).CheckAsync(Identity("0.2.2+" + handler.Revision), Identity(plugin), CancellationToken.None);
        Assert.Equal(2, handler.Calls); // One release baseline and one shared tag resolution, not a query per component.
        Assert.Equal("release_match", (string?)result["components"]?["server"]?["status"]);
        Assert.Equal(status, (string?)result["components"]?["plugin"]?["status"]);
        Assert.Equal(versionStatus, (string?)result["components"]?["plugin"]?["version_status"]);
        Assert.Equal("not_performed", result.Value<string>("artifact_verification"));
        if (status == "update_available")
        {
            Assert.True(result.Value<bool>("update_available"));
            Assert.Equal("NEW_RELEASE_AVAILABLE", (string?)result["notification"]?["code"]);
            Assert.Equal(new[] { "plugin" }, result["notification"]!["components"]!.Values<string>());
            Assert.StartsWith("rvt-mcp ", (string?)result["notification"]?["message"]);
            Assert.True(result["notification"]!.Value<bool>("requires_user_authorization"));
        }
        else if (status == "release_match")
        {
            Assert.False(result.Value<bool>("update_available"));
            Assert.Equal(JTokenType.Null, result["notification"]!.Type);
        }
        else
        {
            Assert.Null(result.Value<bool?>("update_available"));
            Assert.Equal(status == "release_mismatch" ? "RELEASE_REVIEW_REQUIRED" : "RELEASE_IDENTITY_UNVERIFIED",
                (string?)result["notification"]?["code"]);
            Assert.Equal(new[] { "plugin" }, result["notification"]!["protected_components"]!.Values<string>());
            Assert.False(result["notification"]!.Value<bool>("bundle_update_proposal_allowed"));
            Assert.False(result["notification"]!.Value<bool>("requires_user_authorization"));
        }
    }

    [Fact]
    public async Task Missing_plugin_and_unresolvable_tag_keep_partial_evidence_unknown()
    {
        var handler = new Handler { RefStatus = HttpStatusCode.Forbidden };
        using var client = new HttpClient(handler);
        var result = await new ReleaseUpdateCheck(client).CheckAsync(Identity("0.2.2+" + handler.Revision), null, CancellationToken.None);
        Assert.Equal("checked", result.Value<string>("status"));
        Assert.Equal("http_403", result.Value<string>("build_reason"));
        Assert.Equal("match", (string?)result["components"]?["server"]?["version_status"]);
        Assert.Equal("unknown", (string?)result["components"]?["server"]?["build_status"]);
        Assert.Equal("unknown", (string?)result["components"]?["plugin"]?["status"]);
        Assert.Null(result.Value<bool?>("update_available"));
    }

    [Fact]
    public async Task Missing_plugin_cannot_inherit_the_servers_release_match()
    {
        var handler = new Handler();
        using var client = new HttpClient(handler);
        var result = await new ReleaseUpdateCheck(client).CheckAsync(Identity("0.2.2+" + handler.Revision), null, CancellationToken.None);
        Assert.Equal("release_match", (string?)result["components"]?["server"]?["status"]);
        Assert.Equal("unknown", (string?)result["components"]?["plugin"]?["status"]);
        Assert.Null(result.Value<bool?>("update_available"));
    }

    [Fact]
    public async Task Timeout_resolving_the_build_retains_version_evidence_without_claiming_a_build_match()
    {
        var handler = new Handler();
        handler.BeforeResponse = ct => handler.Calls > 1 ? Task.Delay(Timeout.Infinite, ct) : Task.CompletedTask;
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(40) };
        var result = await Check(client, "0.2.2+" + handler.Revision);
        Assert.Equal("checked", result.Value<string>("status"));
        Assert.Equal("timeout", result.Value<string>("build_reason"));
        Assert.Equal("match", (string?)result["components"]?["server"]?["version_status"]);
        Assert.Equal("unknown", (string?)result["components"]?["server"]?["build_status"]);
    }

    [Fact]
    public async Task Annotated_tag_is_peeled_without_following_API_provided_URLs()
    {
        var handler = new Handler {
            RefBody = """{"ref":"refs/tags/v0.2.2","object":{"type":"tag","sha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","url":"https://untrusted.invalid"}}""",
            TagBody = """{"tag":"v0.2.2","object":{"type":"commit","sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}""",
        };
        using var client = new HttpClient(handler);
        var result = await Check(client, "0.2.2+" + handler.Revision);
        Assert.Equal(3, handler.Calls);
        Assert.Equal(handler.Revision, result.Value<string>("release_revision"));
        Assert.False(result.Value<bool>("update_available"));
        Assert.DoesNotContain("untrusted", result.ToString());
    }

    [Theory]
    [InlineData("{\"ref\":\"refs/tags/other\",\"object\":{\"type\":\"commit\",\"sha\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}}")]
    [InlineData("{\"ref\":\"refs/tags/v0.2.2\",\"object\":{\"type\":\"commit\",\"sha\":\"../unsafe\"}}")]
    [InlineData("{\"ref\":\"refs/tags/v0.2.2\",\"object\":{\"type\":\"tree\",\"sha\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}}")]
    public async Task Invalid_tag_identity_never_claims_a_release_build_match(string metadata)
    {
        using var client = new HttpClient(new Handler { RefBody = metadata });
        var result = await Check(client, "0.2.2+" + new string('a', 40));
        Assert.Equal("invalid_release_metadata", result.Value<string>("build_reason"));
        Assert.Equal("unknown", (string?)result["components"]?["server"]?["build_status"]);
        Assert.Null(result.Value<bool?>("update_available"));
    }

    public static IEnumerable<object[]> WrongTagMetadataTypes()
    {
        foreach (var value in new JToken[] { new JObject(), new JArray(), new JValue(42), new JValue(true), JValue.CreateNull() })
        {
            foreach (var field in new[] { "ref", "type", "tag", "annotated_type" })
            {
                var reference = new JObject { ["ref"] = "refs/tags/v0.2.2",
                    ["object"] = new JObject { ["type"] = "commit", ["sha"] = new string('a', 40) } };
                var annotated = new JObject { ["tag"] = "v0.2.2",
                    ["object"] = new JObject { ["type"] = "commit", ["sha"] = new string('a', 40) } };
                if (field == "ref") reference["ref"] = value.DeepClone();
                else if (field == "type") reference["object"]!["type"] = value.DeepClone();
                else
                {
                    reference["object"]!["type"] = "tag";
                    reference["object"]!["sha"] = new string('b', 40);
                    if (field == "tag") annotated["tag"] = value.DeepClone();
                    else annotated["object"]!["type"] = value.DeepClone();
                }
                yield return new object[] { field, value.Type.ToString(), reference.ToString(), annotated.ToString() };
            }
        }
    }

    [Theory]
    [MemberData(nameof(WrongTagMetadataTypes))]
    public async Task Wrong_tag_metadata_types_return_unknown_build_without_throwing(
        string field, string tokenType, string reference, string annotated)
    {
        using var client = new HttpClient(new Handler { RefBody = reference, TagBody = annotated });
        var result = await Check(client, "0.2.2+" + new string('a', 40));
        Assert.Equal("checked", result.Value<string>("status"));
        Assert.True(result.Value<string>("build_reason") == "invalid_release_metadata", $"{field} ({tokenType}): {result}");
        Assert.Null(result.Value<string>("release_revision"));
        foreach (var component in new[] { "server", "plugin" })
        {
            Assert.Equal("match", (string?)result["components"]?[component]?["version_status"]);
            Assert.Equal("unknown", (string?)result["components"]?[component]?["build_status"]);
            Assert.Equal("unknown", (string?)result["components"]?[component]?["status"]);
        }
        Assert.Null(result.Value<bool?>("update_available"));
    }
}
