#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Bimwright.Targeting;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    /// <summary>Result of the client-sandbox check (family contract FC-TGT-07).</summary>
    internal sealed class ClientSandboxResult
    {
        internal ClientSandboxResult(bool detected, string? kind, IReadOnlyList<string> evidence)
        {
            Detected = detected;
            Kind = kind;
            Evidence = evidence;
        }

        internal bool Detected { get; }
        internal string? Kind { get; }
        internal IReadOnlyList<string> Evidence { get; }

        internal static readonly ClientSandboxResult NotDetected =
            new ClientSandboxResult(false, null, Array.Empty<string>());
    }

    /// <summary>
    /// Detects that this server runs inside the MCP client's Windows AppContainer sandbox (FC-TGT-07).
    /// There the per-user data folder is redirected under <c>…\Packages\&lt;package&gt;\AC</c> and the
    /// process cannot read Revit's descriptors or reach it, so discovery would silently find nothing.
    /// Only the AppContainer case is flagged; Low integrity and restricted tokens are not.
    /// </summary>
    internal static class ClientSandbox
    {
        internal const string KindAppContainer = "appcontainer";
        internal const string EvidenceToken = "token_is_appcontainer";
        internal const string EvidencePath = "local_app_data_under_packages_ac";

        // "\Packages\<one segment>\AC" followed by a separator or the end of the path.
        private static readonly Regex PackagesAc = new Regex(@"\\Packages\\[^\\]+\\AC(?:\\|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Lazy<ClientSandboxResult> Probed = new Lazy<ClientSandboxResult>(() =>
        {
            string? localAppData;
            try { localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); }
            catch { localAppData = null; }
            return Classify(TokenIsAppContainer(), localAppData);
        });

        /// <summary>Test seam: when set, replaces the real (P/Invoke) result. null restores it.</summary>
        internal static ClientSandboxResult? OverrideForTests;

        /// <summary>Evaluated once per process, lazily.</summary>
        internal static ClientSandboxResult Current => OverrideForTests ?? Probed.Value;

        /// <summary>Pure classifier: either signal on its own is evidence of an AppContainer.</summary>
        internal static ClientSandboxResult Classify(bool? tokenIsAppContainer, string? resolvedLocalAppData)
        {
            var evidence = new List<string>();
            if (tokenIsAppContainer == true) evidence.Add(EvidenceToken);
            if (IsUnderPackagesAc(resolvedLocalAppData)) evidence.Add(EvidencePath);
            return evidence.Count == 0
                ? ClientSandboxResult.NotDetected
                : new ClientSandboxResult(true, KindAppContainer, evidence);
        }

        internal static bool IsUnderPackagesAc(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            return PackagesAc.IsMatch(path!.Trim().Replace('/', '\\'));
        }

        /// <summary>TokenIsAppContainer of the current process token; null off Windows or on any failure.</summary>
        internal static bool? TokenIsAppContainer()
        {
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token)) return null;
                try
                {
                    return GetTokenInformation(token, TokenInformationIsAppContainer, out int value, sizeof(int), out _)
                        ? value != 0
                        : null;
                }
                finally { CloseHandle(token); }
            }
            catch { return null; }
        }

        private const uint TokenQuery = 0x0008;
        private const int TokenInformationIsAppContainer = 29;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
            out int tokenInformation, int tokenInformationLength, out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }

    /// <summary>The CLIENT_SANDBOXED response pieces, shared by every path that reports it (FC-TGT-07).</summary>
    internal static class ClientSandboxGuidance
    {
        internal const string Code = "CLIENT_SANDBOXED";

        internal const string NextStep =
            "rvt-mcp is running inside your MCP client's sandbox (a Windows AppContainer), so it cannot see or reach Revit on this computer. " +
            "The command was not sent. The sandbox is how your client protects your computer by limiting what agent tools can access; " +
            "Revit and rvt-mcp are working normally. To use rvt-mcp, a person must choose to let this one MCP server run outside the sandbox " +
            "in the client's settings: prefer a per-server sandbox exception; otherwise use a client permission mode that runs local MCP servers unsandboxed. " +
            "Do not loosen file or folder permissions to work around it. Agents: report this to the user and do not try to bypass the sandbox.";

        internal const string Message = Code + ": " + NextStep;

        internal static JObject SandboxJson(ClientSandboxResult result) => new JObject
        {
            ["detected"] = result.Detected,
            ["kind"] = result.Kind,
            ["evidence"] = new JArray(result.Evidence),
            ["data_dir"] = AuthToken.DiscoveryDir()
        };

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TargetPayload, object> DecisionCache = new();
        private static readonly object Replaced = new();
        private static readonly object Kept = new();

        /// <summary>True when a NO_TARGET payload must be reported as CLIENT_SANDBOXED: sandboxed and nothing live discovered.</summary>
        internal static bool Replaces(TargetPayload payload, bool? discoveryEmpty = null)
        {
            if (payload == null || payload.Code != TargetCode.NoTarget || payload.Candidates.Count > 0
                || !ClientSandbox.Current.Detected)
                return false;
            if (DecisionCache.TryGetValue(payload, out var cached))
                return ReferenceEquals(cached, Replaced);
            bool replace;
            if (discoveryEmpty.HasValue)
            {
                replace = discoveryEmpty.Value;
            }
            else
            {
                try { replace = RevitTargetBinding.ScanNoTitles().Live.Count == 0; }
                catch { replace = false; } // leave the original payload when discovery cannot be re-checked
            }
            DecisionCache.AddOrUpdate(payload, replace ? Replaced : Kept);
            return replace;
        }
    }
}
