using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Update
{
    /// <summary>
    /// Minimal semantic version (major.minor.patch[-prerelease][+build]) with
    /// SemVer 2.0 precedence. A leading "v" is accepted.
    /// </summary>
    public sealed class SemanticVersion : IComparable<SemanticVersion>
    {
        public int Major { get; }
        public int Minor { get; }
        public int Patch { get; }
        public string Prerelease { get; }
        public bool IsPrerelease => !string.IsNullOrEmpty(Prerelease);

        private SemanticVersion(int major, int minor, int patch, string prerelease)
        {
            Major = major; Minor = minor; Patch = patch; Prerelease = prerelease ?? string.Empty;
        }

        public static bool TryParse(string text, out SemanticVersion version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var s = text.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
            var plus = s.IndexOf('+');
            if (plus >= 0) s = s.Substring(0, plus);
            string pre = null;
            var dash = s.IndexOf('-');
            if (dash >= 0) { pre = s.Substring(dash + 1); s = s.Substring(0, dash); if (pre.Length == 0) return false; }
            var parts = s.Split('.');
            if (parts.Length < 2 || parts.Length > 3) return false;
            if (!TryPart(parts[0], out var major) || !TryPart(parts[1], out var minor)) return false;
            var patch = 0;
            if (parts.Length == 3 && !TryPart(parts[2], out patch)) return false;
            version = new SemanticVersion(major, minor, patch, pre);
            return true;
        }

        private static bool TryPart(string p, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(p)) return false;
            foreach (var c in p) if (c < '0' || c > '9') return false;
            return int.TryParse(p, out value);
        }

        public int CompareTo(SemanticVersion other)
        {
            if (other is null) return 1;
            var c = Major.CompareTo(other.Major); if (c != 0) return c;
            c = Minor.CompareTo(other.Minor); if (c != 0) return c;
            c = Patch.CompareTo(other.Patch); if (c != 0) return c;
            if (!IsPrerelease && !other.IsPrerelease) return 0;
            if (!IsPrerelease) return 1;   // release > prerelease
            if (!other.IsPrerelease) return -1;
            var a = Prerelease.Split('.');
            var b = other.Prerelease.Split('.');
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                var an = int.TryParse(a[i], out var ai);
                var bn = int.TryParse(b[i], out var bi);
                if (an && bn) { c = ai.CompareTo(bi); }
                else if (an) { c = -1; }
                else if (bn) { c = 1; }
                else { c = string.CompareOrdinal(a[i], b[i]); }
                if (c != 0) return c < 0 ? -1 : 1;
            }
            return a.Length.CompareTo(b.Length);
        }

        public override string ToString() =>
            Major + "." + Minor + "." + Patch + (IsPrerelease ? "-" + Prerelease : string.Empty);
    }

    /// <summary>Persisted result of the last update check (one small JSON file
    /// next to the add-in's other state in %LOCALAPPDATA%\Bimwright\rvt-mcp).</summary>
    public sealed class UpdateCheckState
    {
        [JsonProperty("lastCheckedUtc")] public DateTime? LastCheckedUtc { get; set; }
        [JsonProperty("latestTag")] public string LatestTag { get; set; }
        [JsonProperty("latestUrl")] public string LatestUrl { get; set; }
        [JsonProperty("skippedVersion")] public string SkippedVersion { get; set; }

        public static string DefaultPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bimwright", "rvt-mcp", "update-check.json");

        public static UpdateCheckState Load(string path = null)
        {
            try
            {
                path = path ?? DefaultPath;
                if (!File.Exists(path)) return new UpdateCheckState();
                return JsonConvert.DeserializeObject<UpdateCheckState>(File.ReadAllText(path, Encoding.UTF8)) ?? new UpdateCheckState();
            }
            catch { return new UpdateCheckState(); }
        }

        public void Save(string path = null)
        {
            try
            {
                path = path ?? DefaultPath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(this, Formatting.Indented), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch { }
        }
    }

    /// <summary>A newer stable release than the running build.</summary>
    public sealed class UpdateInfo
    {
        public string CurrentVersion { get; set; }
        public string LatestVersion { get; set; }
        public string ReleaseUrl { get; set; }
        public bool IsSkipped { get; set; }
    }

    /// <summary>
    /// Background check against GitHub's "latest release" endpoint. At most one
    /// network request per 24 h (successful or not); every failure is silent.
    /// </summary>
    public static class UpdateChecker
    {
        public const string LatestReleaseApiUrl = "https://api.github.com/repos/bimwright/rvt-mcp/releases/latest";
        public const string ReleasesLatestUrl = "https://github.com/bimwright/rvt-mcp/releases/latest";
        public const string DisableEnvVar = "BIMWRIGHT_DISABLE_UPDATE_CHECK";
        public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
        public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

        public static bool IsDisabledByEnvironment()
        {
            var v = Environment.GetEnvironmentVariable(DisableEnvVar);
            return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Clean semver of an assembly's InformationalVersion ("0.8.2+sha" → "0.8.2").</summary>
        public static string VersionOf(System.Reflection.Assembly assembly)
        {
            try
            {
                var info = (System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
                var v = info?.InformationalVersion ?? assembly.GetName().Version?.ToString(3) ?? "0.0.0";
                return v.Split('+')[0];
            }
            catch { return "0.0.0"; }
        }

        /// <summary>Returns (tag, html_url) for a stable, published release; null otherwise.</summary>
        public static Tuple<string, string> ParseLatestRelease(string json)
        {
            try
            {
                var o = JObject.Parse(json);
                if (o.Value<bool?>("prerelease") == true || o.Value<bool?>("draft") == true) return null;
                var tag = o.Value<string>("tag_name");
                if (!SemanticVersion.TryParse(tag, out var v) || v.IsPrerelease) return null;
                var url = o.Value<string>("html_url");
                if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://github.com/bimwright/rvt-mcp/", StringComparison.Ordinal))
                    url = ReleasesLatestUrl;
                return Tuple.Create(tag, url);
            }
            catch { return null; }
        }

        /// <summary>Builds the notice from the cached state only (no network).</summary>
        public static UpdateInfo FromState(string currentVersion, UpdateCheckState state)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.LatestTag)) return null;
            if (!SemanticVersion.TryParse(currentVersion, out var current)) return null;
            if (!SemanticVersion.TryParse(state.LatestTag, out var latest) || latest.IsPrerelease) return null;
            if (latest.CompareTo(current) <= 0) return null;
            var latestText = "v" + latest;
            return new UpdateInfo
            {
                CurrentVersion = "v" + current,
                LatestVersion = latestText,
                ReleaseUrl = string.IsNullOrWhiteSpace(state.LatestUrl) ? ReleasesLatestUrl : state.LatestUrl,
                IsSkipped = SemanticVersion.TryParse(state.SkippedVersion, out var skipped) && skipped.CompareTo(latest) == 0,
            };
        }

        public static UpdateInfo FromCache(string currentVersion, string statePath = null) =>
            FromState(currentVersion, UpdateCheckState.Load(statePath));

        /// <summary>
        /// Refreshes the cache when it is older than <see cref="CheckInterval"/> and
        /// returns the resulting notice (null when up to date or unknown). Never throws.
        /// </summary>
        public static async Task<UpdateInfo> CheckAsync(string currentVersion, string statePath = null,
            Func<CancellationToken, Task<string>> fetch = null, DateTime? nowUtc = null)
        {
            try
            {
                var now = nowUtc ?? DateTime.UtcNow;
                var state = UpdateCheckState.Load(statePath);
                var due = state.LastCheckedUtc == null
                    || now - state.LastCheckedUtc.Value >= CheckInterval
                    || state.LastCheckedUtc.Value > now.AddMinutes(5); // clock moved backwards
                if (due)
                {
                    state.LastCheckedUtc = now;
                    state.Save(statePath); // count the attempt even if the request fails
                    string json = null;
                    using (var cts = new CancellationTokenSource(RequestTimeout))
                    {
                        try { json = await (fetch ?? FetchLatestAsync)(cts.Token).ConfigureAwait(false); }
                        catch { json = null; }
                    }
                    var parsed = json == null ? null : ParseLatestRelease(json);
                    if (parsed != null)
                    {
                        state.LatestTag = parsed.Item1;
                        state.LatestUrl = parsed.Item2;
                        state.Save(statePath);
                    }
                }
                return FromState(currentVersion, state);
            }
            catch { return null; }
        }

        public static void SkipVersion(string latestVersion, string statePath = null)
        {
            var state = UpdateCheckState.Load(statePath);
            state.SkippedVersion = latestVersion;
            state.Save(statePath);
        }

        private static async Task<string> FetchLatestAsync(CancellationToken token)
        {
            using (var http = new HttpClient { Timeout = RequestTimeout })
            using (var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl))
            {
                request.Headers.UserAgent.ParseAdd("rvt-mcp-update-check");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using (var response = await http.SendAsync(request, token).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) return null;
                    return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>User/agent-facing texts. The prompt is English on purpose: it is
    /// pasted into an AI agent, which answers in the user's language.</summary>
    public static class UpdateNoticeText
    {
        public static string BuildAgentPrompt(UpdateInfo info)
        {
            if (info == null) return string.Empty;
            var sb = new StringBuilder();
            sb.AppendLine("Please help me update rvt-mcp (the Revit MCP server and add-in by bimwright).");
            sb.AppendLine("Installed version: " + info.CurrentVersion);
            sb.AppendLine("Latest version: " + info.LatestVersion);
            sb.AppendLine("Release page: " + info.ReleaseUrl);
            sb.AppendLine();
            sb.AppendLine("Steps:");
            sb.AppendLine("1. Open the release page and read the release notes and install instructions (also AGENTS.md in the repository).");
            sb.AppendLine("2. Download the RvtMcp.Setup-v" + info.LatestVersion + "-win-x64.zip asset and verify it against its .sha256 file.");
            sb.AppendLine("3. Tell me to close every running Revit before installing, then guide me through running the installer step by step.");
            sb.AppendLine("4. After installing, help me restart Revit and my MCP client, and confirm the server reports " + info.LatestVersion + ".");
            sb.Append("Ask me before running any command on my computer.");
            return sb.ToString();
        }

        /// <summary>One-line notice for MCP clients (instructions / status results).</summary>
        public static string ForAgent(UpdateInfo info) =>
            info == null ? null :
            "rvt-mcp " + info.LatestVersion + " is available (running " + info.CurrentVersion + "). Tell the user and offer to guide the update: " + info.ReleaseUrl;

        /// <summary>
        /// Prepends <paramref name="notice"/> to the server instructions and keeps the
        /// result within <paramref name="maxUtf8Bytes"/> by dropping trailing lines of
        /// the base text (the notice and the leading keyword paragraph survive).
        /// </summary>
        public static string ComposeInstructions(string baseText, string notice, int maxUtf8Bytes = 2048)
        {
            if (string.IsNullOrWhiteSpace(notice)) return baseText;
            var prefix = "UPDATE: " + notice + "\n\n";
            var text = prefix + baseText;
            if (Encoding.UTF8.GetByteCount(text) <= maxUtf8Bytes) return text;
            var lines = new List<string>((baseText ?? string.Empty).Split('\n'));
            while (lines.Count > 1 && Encoding.UTF8.GetByteCount(prefix + string.Join("\n", lines)) > maxUtf8Bytes)
                lines.RemoveAt(lines.Count - 1);
            return prefix + string.Join("\n", lines);
        }
    }
}
