#nullable enable

// Bimwright.Targeting — shared pure core for target discovery & binding (spec BW-TARGET-001).
// This file is copied verbatim into each product server repo. Dependencies: BCL + Newtonsoft.Json.
// No file IO, no Process access (liveness goes through IProcessProbe), no product-specific code
// beyond the HostProducts tables.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Bimwright.Targeting
{
    internal enum HostProduct { Revit, AutoCad, Navisworks, Inventor }

    internal static class HostProducts
    {
        internal static string Prefix(HostProduct p) => p switch
        {
            HostProduct.Revit => "revit",
            HostProduct.AutoCad => "acad",
            HostProduct.Navisworks => "navis",
            HostProduct.Inventor => "inventor",
            _ => throw new ArgumentOutOfRangeException(nameof(p)),
        };

        internal static string HostApp(HostProduct p) => p switch
        {
            HostProduct.Revit => "revit",
            HostProduct.AutoCad => "autocad",
            HostProduct.Navisworks => "navisworks",
            HostProduct.Inventor => "inventor",
            _ => throw new ArgumentOutOfRangeException(nameof(p)),
        };

        internal static string DisplayName(HostProduct p) => p switch
        {
            HostProduct.Revit => "Revit",
            HostProduct.AutoCad => "AutoCAD",
            HostProduct.Navisworks => "Navisworks",
            HostProduct.Inventor => "Inventor",
            _ => throw new ArgumentOutOfRangeException(nameof(p)),
        };

        internal static string ToolPrefix(HostProduct p) => p switch
        {
            HostProduct.Revit => "revit",
            HostProduct.AutoCad => "dwg",
            HostProduct.Navisworks => "nwd",
            HostProduct.Inventor => "inventor",
            _ => throw new ArgumentOutOfRangeException(nameof(p)),
        };

        internal static bool HasLegacyFile(HostProduct p) =>
            p == HostProduct.Revit || p == HostProduct.AutoCad;
    }

    // ---- M1.1 Normalized descriptor (spec 5.3, 5.6 step 1) ----

    internal enum DescriptorSource { PerInstance, Legacy }

    internal sealed class TargetDescriptor
    {
        internal HostProduct Product { get; set; }
        internal string HostApp { get; set; } = "";
        internal int HostYear { get; set; }
        internal int Pid { get; set; }
        internal DateTime? ProcessStartUtc { get; set; }
        internal string TargetId { get; set; } = "";
        internal string Transport { get; set; } = "tcp"; // "pipe" | "tcp", lower-case
        internal int? Port { get; set; }
        internal string? PipeName { get; set; }
        internal string AuthToken { get; set; } = "";
        internal IReadOnlyList<string> Capabilities { get; set; } = Array.Empty<string>();
        internal DateTime? LastHeartbeatUtc { get; set; }
        internal DescriptorSource Source { get; set; }
        internal string FileName { get; set; } = "";
    }

    internal static class DescriptorNormalizer
    {
        private const int MinYear = 2022;
        private const int MaxYear = 2027;

        internal static bool TryNormalize(
            HostProduct product,
            string fileName,
            JObject json,
            out TargetDescriptor? descriptor,
            out string? rejectReason)
        {
            descriptor = null;
            rejectReason = null;
            string prefix = HostProducts.Prefix(product);

            // File name shape.
            var perInstance = System.Text.RegularExpressions.Regex.Match(
                fileName, "^" + prefix + "-(20[0-9]{2})-([0-9]+)\\.json$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var legacy = System.Text.RegularExpressions.Regex.Match(
                fileName, "^" + prefix + "-(20[0-9]{2})\\.json$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            DescriptorSource source;
            int? nameYear = null;
            int? namePid = null;
            if (perInstance.Success)
            {
                if (!int.TryParse(perInstance.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ny)
                    || !int.TryParse(perInstance.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int np))
                {
                    rejectReason = "file name year or pid is out of range";
                    return false;
                }
                source = DescriptorSource.PerInstance;
                nameYear = ny;
                namePid = np;
            }
            else if (legacy.Success && HostProducts.HasLegacyFile(product))
            {
                if (!int.TryParse(legacy.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ny))
                {
                    rejectReason = "file name year is out of range";
                    return false;
                }
                source = DescriptorSource.Legacy;
                nameYear = ny;
            }
            else
            {
                rejectReason = "file name does not match a descriptor pattern for this product";
                return false;
            }

            // host_app.
            string hostApp;
            if (!TryGetString(json["host_app"], out var hostAppValue))
            {
                rejectReason = "host_app must be a string";
                return false;
            }
            if (hostAppValue != null)
            {
                hostApp = hostAppValue;
            }
            else if (product == HostProduct.Navisworks)
            {
                TryGetString(json["host_product"], out var hostProduct);
                if (string.Equals(hostProduct, "Manage", StringComparison.OrdinalIgnoreCase))
                    hostApp = "navisworks";
                else
                {
                    rejectReason = "missing host_app";
                    return false;
                }
            }
            else
            {
                hostApp = HostProducts.HostApp(product);
            }
            if (!string.Equals(hostApp, HostProducts.HostApp(product), StringComparison.OrdinalIgnoreCase))
            {
                rejectReason = "host_app does not match this product";
                return false;
            }

            // host_year.
            if (!TryGetYear(json, "host_year", out int hostYear))
            {
                string legacyKey = product switch
                {
                    HostProduct.Revit => "revit_year",
                    HostProduct.AutoCad => "acad_year",
                    HostProduct.Navisworks => "navisworks_year",
                    _ => "inventor_year",
                };
                if (!TryGetYear(json, legacyKey, out hostYear)
                    && product == HostProduct.AutoCad
                    && !TryGetYear(json, "target", out hostYear))
                {
                    TryGetYear(json, "version", out hostYear);
                }
            }
            if (hostYear < MinYear || hostYear > MaxYear)
            {
                rejectReason = "host_year missing or outside 2022..2027";
                return false;
            }
            if (nameYear.HasValue && hostYear != nameYear.Value)
            {
                rejectReason = "host_year does not match file name";
                return false;
            }

            // pid.
            if (!TryGetInt(json["pid"], out int pid) && !TryGetInt(json["process_id"], out pid))
                pid = 0;
            if (pid <= 0)
            {
                rejectReason = "pid missing or not positive";
                return false;
            }
            if (namePid.HasValue && pid != namePid.Value)
            {
                rejectReason = "pid does not match file name";
                return false;
            }

            // transport.
            if (!TryGetString(json["transport"], out var transportField))
            {
                rejectReason = "transport must be a string";
                return false;
            }
            string transport = (transportField ?? "tcp").ToLowerInvariant();
            if (transport != "pipe" && transport != "tcp")
            {
                rejectReason = "transport must be pipe or tcp";
                return false;
            }

            // pipe_name / port.
            TryGetString(json["pipe_name"], out string? pipeName); // non-string treated as missing
            if (string.IsNullOrEmpty(pipeName))
            {
                TryGetString(json["pipe_path"], out var pipePath);
                if (!string.IsNullOrEmpty(pipePath))
                {
                    const string pipePrefix = @"\\.\pipe\";
                    pipeName = pipePath.StartsWith(pipePrefix, StringComparison.OrdinalIgnoreCase)
                        ? pipePath.Substring(pipePrefix.Length)
                        : pipePath;
                }
            }
            int? port = null;
            if (TryGetInt(json["port"], out int p))
                port = p;
            if (transport == "pipe")
            {
                if (port == 0)
                    port = null; // ipt writes 0 for pipe
                if (string.IsNullOrEmpty(pipeName))
                {
                    rejectReason = "pipe transport requires pipe_name";
                    return false;
                }
            }
            else
            {
                if (port == null || port <= 0)
                {
                    rejectReason = "tcp transport requires port > 0";
                    return false;
                }
            }

            // auth_token.
            if (!TryGetString(json["auth_token"], out var authToken) || string.IsNullOrEmpty(authToken))
            {
                rejectReason = "missing auth_token";
                return false;
            }

            // target_id.
            string canonicalTargetId = $"{prefix}-{hostYear}-{pid}";
            if (!TryGetString(json["target_id"], out var targetIdField))
            {
                rejectReason = "target_id must be a string";
                return false;
            }
            if (targetIdField != null
                && !string.Equals(targetIdField, canonicalTargetId, StringComparison.OrdinalIgnoreCase))
            {
                rejectReason = "target_id does not match file name parts";
                return false;
            }

            descriptor = new TargetDescriptor
            {
                Product = product,
                HostApp = HostProducts.HostApp(product),
                HostYear = hostYear,
                Pid = pid,
                ProcessStartUtc = ReadTimestamp(json["process_start_utc"]),
                TargetId = canonicalTargetId,
                Transport = transport,
                Port = port,
                PipeName = pipeName,
                AuthToken = authToken,
                Capabilities = ReadCapabilities(json["capabilities"]),
                LastHeartbeatUtc = ReadTimestamp(json["last_heartbeat_utc"]),
                Source = source,
                FileName = fileName,
            };
            return true;
        }

        // Absent/null → (true, null); String → (true, value); anything else → false.
        private static bool TryGetString(JToken? token, out string? value)
        {
            value = null;
            if (token == null || token.Type == JTokenType.Null)
                return true;
            if (token.Type == JTokenType.String)
            {
                value = token.Value<string>();
                return true;
            }
            return false;
        }

        private static bool TryGetInt(JToken? token, out int value)
        {
            value = 0;
            if (token == null)
                return false;
            if (token.Type == JTokenType.Integer)
            {
                // Newtonsoft stores oversized integers as BigInteger; Value<long>() would throw.
                if (((JValue)token).Value is long l && l <= int.MaxValue && l >= int.MinValue)
                {
                    value = (int)l;
                    return true;
                }
                if (((JValue)token).Value is int i)
                {
                    value = i;
                    return true;
                }
                return false;
            }
            if (token.Type == JTokenType.String)
                return int.TryParse(token.Value<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            return false;
        }

        private static bool TryGetYear(JObject json, string key, out int year)
        {
            year = 0;
            var token = json[key];
            if (token == null)
                return false;
            if (token.Type == JTokenType.String)
            {
                var s = token.Value<string>();
                if (s == null || s.Length != 4 || !s.All(char.IsDigit))
                    return false;
            }
            return TryGetInt(token, out year);
        }

        private static DateTime? ReadTimestamp(JToken? token)
        {
            if (token == null)
                return null;
            if (token.Type == JTokenType.Date)
            {
                var dt = token.Value<DateTime>();
                return dt.Kind switch
                {
                    DateTimeKind.Utc => dt,
                    DateTimeKind.Local => dt.ToUniversalTime(),
                    _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
                };
            }
            if (token.Type == JTokenType.String)
            {
                if (DateTime.TryParse(token.Value<string>(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                    return dt.ToUniversalTime();
            }
            return null;
        }

        private static IReadOnlyList<string> ReadCapabilities(JToken? token)
        {
            if (token is JArray arr)
                return arr.Where(t => t.Type == JTokenType.String).Select(t => t.Value<string>()!).ToList();
            return Array.Empty<string>();
        }
    }

    // ---- M1.2 Selector (spec 6.1) ----

    internal enum SelectorKind { Auto, Year, Pid, TargetId }
    internal enum SelectorError { Empty, RCode, Invalid }

    internal sealed class TargetSelector
    {
        internal SelectorKind Kind { get; private set; }
        internal int? Year { get; private set; }
        internal int? Pid { get; private set; }
        internal string? TargetId { get; private set; }
        internal string Canonical { get; private set; } = "auto";
        internal bool IsExplicit => Kind == SelectorKind.Pid || Kind == SelectorKind.TargetId;

        internal static readonly TargetSelector Auto = new TargetSelector
        {
            Kind = SelectorKind.Auto,
            Canonical = "auto",
        };

        internal static TargetSelector ForYear(int year) => new TargetSelector
        {
            Kind = SelectorKind.Year,
            Year = year,
            Canonical = year.ToString(CultureInfo.InvariantCulture),
        };

        internal static TargetSelector ForPid(int pid) => new TargetSelector
        {
            Kind = SelectorKind.Pid,
            Pid = pid,
            Canonical = "pid:" + pid.ToString(CultureInfo.InvariantCulture),
        };

        internal static TargetSelector ForTargetId(string targetId) => new TargetSelector
        {
            Kind = SelectorKind.TargetId,
            TargetId = targetId.ToLowerInvariant(),
            Canonical = "id:" + targetId.ToLowerInvariant(),
        };

        internal bool Matches(TargetDescriptor d) => Kind switch
        {
            SelectorKind.Auto => true,
            SelectorKind.Year => d.HostYear == Year,
            SelectorKind.Pid => d.Pid == Pid,
            SelectorKind.TargetId => string.Equals(d.TargetId, TargetId, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    internal static class SelectorParser
    {
        internal static bool TryParse(
            HostProduct product,
            string? input,
            out TargetSelector? selector,
            out SelectorError error,
            out string? message)
        {
            selector = null;
            error = SelectorError.Invalid;
            message = null;
            string prefix = HostProducts.Prefix(product);

            var s = input?.Trim();
            if (string.IsNullOrEmpty(s))
            {
                error = SelectorError.Empty;
                message = "selector is empty";
                return false;
            }

            if (string.Equals(s, "auto", StringComparison.OrdinalIgnoreCase))
            {
                selector = TargetSelector.Auto;
                return true;
            }

            if (System.Text.RegularExpressions.Regex.IsMatch(s, "^[Rr][0-9]{2}$"))
            {
                error = SelectorError.RCode;
                message = "R-codes are not accepted; use the 4-digit year, pid:<n> or id:<target_id>";
                return false;
            }

            var ci = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
            var idPattern = "^" + prefix + "-20[0-9]{2}-[0-9]+$";

            var m = System.Text.RegularExpressions.Regex.Match(s, "^pid:([0-9]+)$", ci);
            if (m.Success)
            {
                if (int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0)
                {
                    selector = TargetSelector.ForPid(n);
                    return true;
                }
                message = "pid must be a positive integer";
                return false;
            }

            m = System.Text.RegularExpressions.Regex.Match(s, "^id:(.+)$", ci);
            if (m.Success)
            {
                var x = m.Groups[1].Value.Trim();
                if (System.Text.RegularExpressions.Regex.IsMatch(x, idPattern, ci))
                {
                    selector = TargetSelector.ForTargetId(x);
                    return true;
                }
                message = "target_id must match " + prefix + "-<year>-<pid>";
                return false;
            }

            if (System.Text.RegularExpressions.Regex.IsMatch(s, "^[0-9]+$"))
            {
                if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                {
                    message = "number out of range";
                    return false;
                }
                if (n >= 2022 && n <= 2027)
                {
                    selector = TargetSelector.ForYear(n);
                    return true;
                }
                if (product == HostProduct.Inventor && n > 0)
                {
                    selector = TargetSelector.ForPid(n);
                    return true;
                }
                message = "year must be in 2022..2027";
                return false;
            }

            if ((product == HostProduct.Navisworks || product == HostProduct.Inventor)
                && System.Text.RegularExpressions.Regex.IsMatch(s, idPattern, ci))
            {
                selector = TargetSelector.ForTargetId(s);
                return true;
            }

            if (product == HostProduct.Inventor)
            {
                m = System.Text.RegularExpressions.Regex.Match(s, "^BimwrightInventor-([0-9]+)$", ci);
                if (m.Success
                    && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                    && n > 0)
                {
                    selector = TargetSelector.ForPid(n);
                    return true;
                }
            }

            message = "unrecognized selector";
            return false;
        }
    }

    // ---- M1.3 Liveness (spec 5.6) ----

    internal enum ProcessState { NotFound, Exited, Running, AccessDenied }

    internal sealed record ProcessProbeResult(ProcessState State, DateTime? StartUtc, string? WindowTitle);

    internal interface IProcessProbe
    {
        ProcessProbeResult Probe(int pid);
    }

    internal enum Liveness { Alive, ProvenDead }

    internal sealed record LivenessResult(Liveness Liveness, bool IdentityVerified, string Reason);

    internal static class LivenessEvaluator
    {
        internal static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(1);

        internal static LivenessResult Evaluate(DateTime? expectedStartUtc, ProcessProbeResult probe)
        {
            switch (probe.State)
            {
                case ProcessState.NotFound:
                    return new LivenessResult(Liveness.ProvenDead, false, "process not found");
                case ProcessState.Exited:
                    return new LivenessResult(Liveness.ProvenDead, false, "process exited");
                case ProcessState.AccessDenied:
                    return new LivenessResult(Liveness.Alive, false, "process exists but access denied");
                default:
                    if (probe.StartUtc == null || expectedStartUtc == null)
                        return new LivenessResult(Liveness.Alive, false, "start time not comparable");
                    var delta = probe.StartUtc.Value - expectedStartUtc.Value;
                    if (delta < TimeSpan.Zero)
                        delta = -delta;
                    if (delta > StartTolerance)
                        return new LivenessResult(Liveness.ProvenDead, false, "process start time differs; pid was reused");
                    return new LivenessResult(Liveness.Alive, true, "start time verified");
            }
        }
    }

    // ---- M1.4 Scan, filter, order (spec 5.6, 5.8, 6.3) ----

    internal sealed class TargetCandidate
    {
        internal TargetCandidate(TargetDescriptor descriptor, bool identityVerified, DateTime? observedStartUtc, string? windowTitle)
        {
            Descriptor = descriptor;
            IdentityVerified = identityVerified;
            ObservedStartUtc = observedStartUtc;
            WindowTitle = string.IsNullOrEmpty(windowTitle) ? null : windowTitle;
            TargetId = descriptor.TargetId;
            Pid = descriptor.Pid;
            HostYear = descriptor.HostYear;
        }

        internal TargetDescriptor Descriptor { get; }
        internal bool IdentityVerified { get; }
        internal DateTime? ObservedStartUtc { get; }
        internal string? WindowTitle { get; }
        internal string TargetId { get; }
        internal int Pid { get; }
        internal int HostYear { get; }
    }

    internal sealed class ScanResult
    {
        internal IReadOnlyList<TargetCandidate> Live { get; set; } = Array.Empty<TargetCandidate>();
        internal IReadOnlyList<TargetDescriptor> ProvenDead { get; set; } = Array.Empty<TargetDescriptor>();
    }

    internal static class TargetScan
    {
        internal static ScanResult Evaluate(IEnumerable<TargetDescriptor> descriptors, IProcessProbe probe)
        {
            var probeCache = new Dictionary<int, ProcessProbeResult>();
            var live = new List<TargetCandidate>();
            var dead = new List<TargetDescriptor>();

            foreach (var d in descriptors)
            {
                if (!probeCache.TryGetValue(d.Pid, out var pr))
                {
                    pr = probe.Probe(d.Pid);
                    probeCache[d.Pid] = pr;
                }
                var liveness = LivenessEvaluator.Evaluate(d.ProcessStartUtc, pr);
                if (liveness.Liveness == Liveness.ProvenDead)
                {
                    dead.Add(d);
                }
                else
                {
                    live.Add(new TargetCandidate(d, liveness.IdentityVerified, pr.StartUtc, pr.WindowTitle));
                }
            }

            // Merge duplicates by pid (legacy + per-instance for the same host).
            var byPid = new Dictionary<int, TargetCandidate>();
            foreach (var c in live)
            {
                if (!byPid.TryGetValue(c.Pid, out var existing) || PreferredOver(c, existing))
                    byPid[c.Pid] = c;
            }

            return new ScanResult { Live = Order(byPid.Values), ProvenDead = dead };
        }

        // Per-instance wins over legacy; then later process_start_utc; then file name.
        private static bool PreferredOver(TargetCandidate a, TargetCandidate b)
        {
            if (a.Descriptor.Source != b.Descriptor.Source)
                return a.Descriptor.Source == DescriptorSource.PerInstance;
            int cmp = Nullable.Compare(a.Descriptor.ProcessStartUtc, b.Descriptor.ProcessStartUtc);
            if (cmp != 0)
                return cmp > 0;
            return string.CompareOrdinal(a.Descriptor.FileName, b.Descriptor.FileName) < 0;
        }

        internal static IReadOnlyList<TargetCandidate> Order(IEnumerable<TargetCandidate> candidates) =>
            candidates
                .OrderByDescending(c => c.HostYear)
                .ThenByDescending(c => c.Descriptor.ProcessStartUtc.HasValue ? 1 : 0)
                .ThenByDescending(c => c.Descriptor.ProcessStartUtc)
                .ThenByDescending(c => c.Pid)
                .ToList();

        internal static IReadOnlyList<TargetCandidate> Filter(TargetSelector selector, IEnumerable<TargetCandidate> candidates) =>
            candidates.Where(c => selector.Matches(c.Descriptor)).ToList();
    }

    // ---- M1.7 Payload (spec 6.10) ----

    internal enum TargetCode
    {
        NoTarget,
        TargetUnavailable,
        TargetBusy,
        TargetLost,
        AmbiguousTarget,
        TargetChanged,
        TargetInterrupted,
        InvalidTargetSelector,
    }

    internal sealed record TargetRef(string TargetId, int Pid, int HostYear, string? WindowTitle);

    internal sealed record CandidateInfo(string TargetId, int HostYear, int Pid, bool IdentityVerified, string? WindowTitle);

    internal sealed class TargetPayload
    {
        internal TargetCode Code { get; set; }
        internal bool Sent { get; set; }
        internal string Outcome { get; set; } = "not_started";
        internal string Selector { get; set; } = "auto";
        internal string Binding { get; set; } = "none";
        internal TargetRef? Previous { get; set; }
        internal TargetRef? Current { get; set; }
        internal IReadOnlyList<CandidateInfo> Candidates { get; set; } = Array.Empty<CandidateInfo>();
        internal string? RecommendedNextTool { get; set; }
        internal string NextStep { get; set; } = "";
        internal string Message { get; set; } = "";

        internal static string CodeName(TargetCode c) => c switch
        {
            TargetCode.NoTarget => "NO_TARGET",
            TargetCode.TargetUnavailable => "TARGET_UNAVAILABLE",
            TargetCode.TargetBusy => "TARGET_BUSY",
            TargetCode.TargetLost => "TARGET_LOST",
            TargetCode.AmbiguousTarget => "AMBIGUOUS_TARGET",
            TargetCode.TargetChanged => "TARGET_CHANGED",
            TargetCode.TargetInterrupted => "TARGET_INTERRUPTED",
            TargetCode.InvalidTargetSelector => "INVALID_TARGET_SELECTOR",
            _ => c.ToString(),
        };

        internal JObject ToJson()
        {
            return new JObject
            {
                ["code"] = CodeName(Code),
                ["sent"] = Sent,
                ["outcome"] = Outcome,
                ["selector"] = Selector,
                ["binding"] = Binding,
                ["previous"] = RefToJson(Previous),
                ["current"] = RefToJson(Current),
                ["candidates"] = new JArray(Candidates.Select(c =>
                {
                    var o = new JObject
                    {
                        ["target_id"] = c.TargetId,
                        ["host_year"] = c.HostYear,
                        ["pid"] = c.Pid,
                        ["identity_verified"] = c.IdentityVerified,
                    };
                    if (c.WindowTitle != null)
                        o["window_title"] = c.WindowTitle;
                    return o;
                })),
                ["recommended_next_tool"] = RecommendedNextTool != null ? (JToken)RecommendedNextTool : JValue.CreateNull(),
                ["next_step"] = NextStep,
            };
        }

        private static JToken RefToJson(TargetRef? r)
        {
            if (r == null)
                return JValue.CreateNull();
            var o = new JObject
            {
                ["target_id"] = r.TargetId,
                ["pid"] = r.Pid,
                ["host_year"] = r.HostYear,
            };
            if (r.WindowTitle != null)
                o["window_title"] = r.WindowTitle;
            return o;
        }

        internal static TargetPayload InvalidSelector(HostProduct product, string raw, string message)
        {
            var p = Create(product, TargetCode.InvalidTargetSelector, raw ?? "", "none", null, null,
                Array.Empty<CandidateInfo>());
            p.Message = message;
            return p;
        }

        internal static TargetPayload Create(
            HostProduct product,
            TargetCode code,
            string selectorCanonical,
            string binding,
            TargetRef? previous,
            TargetRef? current,
            IReadOnlyList<CandidateInfo> candidates,
            bool generationChanged = false)
        {
            string app = HostProducts.DisplayName(product);
            string toolPrefix = HostProducts.ToolPrefix(product);
            string switchTool = toolPrefix + "_switch_target";
            string listTool = toolPrefix + "_list_available_targets";

            bool sent = code == TargetCode.TargetInterrupted;
            string? nextTool;
            if (generationChanged)
            {
                nextTool = toolPrefix + "_get_current_target";
            }
            else
            {
                nextTool = code switch
                {
                    TargetCode.TargetChanged => switchTool,
                    TargetCode.TargetLost => switchTool,
                    TargetCode.AmbiguousTarget => switchTool,
                    TargetCode.TargetInterrupted => null,
                    _ => listTool,
                };
            }
            string nextStep;
            if (generationChanged)
            {
                nextStep =
                    $"The call was not sent because the target binding changed after the call arrived. " +
                    $"The current binding is {(current != null ? current.TargetId : "none")}. Call again if still intended.";
            }
            else
            {
                nextStep = code switch
            {
                TargetCode.NoTarget =>
                    $"No live {app} instance matches the selector. Call {listTool} to see running instances.",
                TargetCode.TargetUnavailable =>
                    $"The {app} instance appears alive but did not accept a connection. Check it with {listTool}, then retry.",
                TargetCode.TargetBusy =>
                    $"The {app} instance is busy with another client. Retry later, or check {listTool} for another instance.",
                TargetCode.TargetLost =>
                    $"The bound {app} instance is gone. Review the candidates and call {switchTool} to bind a new instance.",
                TargetCode.AmbiguousTarget =>
                    $"The previous {app} is gone and several instances match the selector. The call was not sent. Call {switchTool} with the intended instance.",
                TargetCode.TargetChanged =>
                    $"The call was not sent. The previous {app} is gone. Check the proposed instance, call {switchTool} to confirm it, then call again if still intended.",
                TargetCode.TargetInterrupted =>
                    $"The command was sent and its outcome is unknown. Check the model state in {app} before retrying a write command.",
                TargetCode.InvalidTargetSelector =>
                    $"Invalid target selector. Use auto, a year 2022..2027, pid:<n> or id:<target_id>. Call {listTool} to see running instances.",
                _ => "",
            };
            }

            return new TargetPayload
            {
                Code = code,
                Sent = sent,
                Outcome = sent ? "unknown" : "not_started",
                Selector = selectorCanonical,
                Binding = binding,
                Previous = previous,
                Current = current,
                Candidates = candidates,
                RecommendedNextTool = nextTool,
                NextStep = nextStep,
                Message = CodeName(code) + ": " + nextStep,
            };
        }
    }

    // ---- M1.5 / M1.6 Binding state machine (spec 6.2, 6.5, 6.6, 6.8) ----

    internal enum BindingState { None, Bound, Lost }
    internal enum BindingKind { Implicit, Explicit }
    internal enum ConnectFailure { Busy, Unavailable }

    internal sealed record BindingRecord(
        long Generation,
        BindingKind Kind,
        TargetSelector Selector,
        string TargetId,
        int Pid,
        DateTime? ObservedStartUtc,
        int HostYear,
        bool IdentityVerified,
        DateTime BoundAtUtc);

    internal sealed record SwitchInfo(string? From, string To, DateTime AtUtc);

    internal sealed record BindingSnapshot(
        BindingState State,
        TargetSelector Selector,
        long Generation,
        BindingRecord? Record,
        SwitchInfo? LastSwitch,
        bool ConfirmationRequired);

    internal abstract record CallPlan;
    internal sealed record ConnectPlan(TargetCandidate? FirstCandidate, BindingRecord? Record, long EffectiveGeneration) : CallPlan;
    internal sealed record FailPlan(TargetPayload Payload) : CallPlan;

    internal sealed record SwitchResult(
        bool Ok,
        bool InstanceChanged,
        BindingRecord? Previous,
        BindingRecord? Current,
        TargetPayload? Failure);

    internal sealed class TargetBinding
    {
        private readonly object _gate = new object();
        private readonly HostProduct _product;
        private readonly IProcessProbe _probe;
        private readonly Func<DateTime> _utcNow;

        private BindingState _state = BindingState.None;
        private TargetSelector _selector;
        private long _generation;
        private BindingRecord? _record;
        private SwitchInfo? _lastSwitch;

        internal TargetBinding(HostProduct product, TargetSelector initialSelector, IProcessProbe probe, Func<DateTime> utcNow)
        {
            _product = product;
            _selector = initialSelector;
            _probe = probe;
            _utcNow = utcNow;
        }

        internal BindingSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new BindingSnapshot(_state, _selector, _generation, _record, _lastSwitch,
                    _state == BindingState.Lost);
            }
        }

        internal long CaptureGeneration()
        {
            lock (_gate)
            {
                return _generation;
            }
        }

        internal CallPlan PlanCall(long pinnedGeneration, IReadOnlyList<TargetCandidate> liveOrdered)
        {
            lock (_gate)
            {
                if (_state == BindingState.Bound && _record != null)
                {
                    var probe = _probe.Probe(_record.Pid);
                    var liveness = LivenessEvaluator.Evaluate(_record.ObservedStartUtc, probe);
                    if (liveness.Liveness == Liveness.ProvenDead)
                        _state = BindingState.Lost;
                    else if (EffectiveGeneration(pinnedGeneration) != _generation)
                        return new FailPlan(Payload(TargetCode.TargetChanged,
                            null, RefWithTitle(_record, liveOrdered), CandidatesFor(_selector, liveOrdered),
                            generationChanged: true));
                    else
                        return new ConnectPlan(null, _record, _generation);
                }

                if (_state == BindingState.Lost && _record != null)
                    return new FailPlan(LostPayload(liveOrdered));

                // None.
                var matches = TargetScan.Filter(_selector, liveOrdered);
                if (matches.Count == 0)
                    return new FailPlan(Payload(TargetCode.NoTarget, null, null,
                        CandidatesFor(_selector, liveOrdered)));
                return new ConnectPlan(matches[0], null, 0);
            }
        }

        internal bool TryCommitFirst(
            long pinnedGeneration,
            TargetCandidate candidate,
            out long effectiveGeneration,
            out TargetPayload? failure)
        {
            lock (_gate)
            {
                if (_state == BindingState.None)
                {
                    var probe = _probe.Probe(candidate.Pid);
                    if (LivenessEvaluator.Evaluate(candidate.ObservedStartUtc, probe).Liveness == Liveness.ProvenDead)
                    {
                        effectiveGeneration = 0;
                        failure = Payload(TargetCode.TargetUnavailable, null,
                            new TargetRef(candidate.TargetId, candidate.Pid, candidate.HostYear, candidate.WindowTitle),
                            Array.Empty<CandidateInfo>());
                        return false;
                    }
                    var start = probe.State == ProcessState.Running ? probe.StartUtc : null;
                    _generation = 1;
                    _record = new BindingRecord(
                        1,
                        _selector.IsExplicit ? BindingKind.Explicit : BindingKind.Implicit,
                        _selector,
                        candidate.TargetId,
                        candidate.Pid,
                        start,
                        candidate.HostYear,
                        candidate.IdentityVerified && start != null,
                        _utcNow());
                    _state = BindingState.Bound;
                    effectiveGeneration = 1;
                    failure = null;
                    return true;
                }
                // Adopt a concurrent first binding only if it is demonstrably the same instance:
                // same pid and both start times known and within tolerance. Anything else fails
                // (a retry is cheap; misrouting is not).
                if (_state == BindingState.Bound && _generation == 1 && _record != null
                    && _record.Pid == candidate.Pid
                    && _record.ObservedStartUtc != null
                    && candidate.ObservedStartUtc != null
                    && SameStart(_record.ObservedStartUtc, candidate.ObservedStartUtc))
                {
                    effectiveGeneration = 1;
                    failure = null;
                    return true;
                }
                effectiveGeneration = 0;
                failure = Payload(TargetCode.TargetChanged, null, Ref(_record), Array.Empty<CandidateInfo>(),
                    generationChanged: true);
                return false;
            }
        }

        internal bool CanSend(long effectiveGeneration, IReadOnlyList<TargetCandidate> liveOrdered, out TargetPayload? failure)
        {
            lock (_gate)
            {
                if (_state == BindingState.Bound && effectiveGeneration == _generation)
                {
                    failure = null;
                    return true;
                }
                if (_state == BindingState.Lost && _record != null)
                {
                    failure = LostPayload(liveOrdered);
                    return false;
                }
                failure = Payload(TargetCode.TargetChanged, null, RefWithTitle(_record, liveOrdered),
                    CandidatesFor(_selector, liveOrdered), generationChanged: true);
                return false;
            }
        }

        internal TargetPayload ReportConnectFailure(
            long effectiveGeneration,
            ConnectFailure kind,
            IReadOnlyList<TargetCandidate> liveOrdered)
        {
            lock (_gate)
            {
                var code = kind == ConnectFailure.Busy ? TargetCode.TargetBusy : TargetCode.TargetUnavailable;

                if (_state == BindingState.Bound && _record != null)
                {
                    if (effectiveGeneration != _generation)
                    {
                        // Stale report from before a switch; do not probe or transition the
                        // current binding on account of it.
                        return Payload(TargetCode.TargetChanged, null,
                            RefWithTitle(_record, liveOrdered), CandidatesFor(_selector, liveOrdered),
                            generationChanged: true);
                    }
                    var probe = _probe.Probe(_record.Pid);
                    var liveness = LivenessEvaluator.Evaluate(_record.ObservedStartUtc, probe);
                    if (liveness.Liveness == Liveness.ProvenDead)
                    {
                        _state = BindingState.Lost;
                        return LostPayload(liveOrdered);
                    }
                    return Payload(code, null, RefWithTitle(_record, liveOrdered),
                        CandidatesFor(_selector, liveOrdered));
                }

                if (_state == BindingState.Lost && _record != null)
                    return LostPayload(liveOrdered);

                if (_state == BindingState.None && effectiveGeneration == 0)
                {
                    // First-selection connect failure.
                    return Payload(code, null, CurrentCandidate(liveOrdered),
                        CandidatesFor(_selector, liveOrdered));
                }

                // Anything else: generation moved on (e.g. eff 0 while another call already bound).
                return Payload(TargetCode.TargetChanged, null, RefWithTitle(_record, liveOrdered),
                    CandidatesFor(_selector, liveOrdered), generationChanged: true);
            }
        }

        internal TargetPayload Interrupted(long effectiveGeneration)
        {
            lock (_gate)
            {
                TargetRef? current = _record != null && _record.Generation == effectiveGeneration
                    ? Ref(_record)
                    : null;
                return Payload(TargetCode.TargetInterrupted, null, current, Array.Empty<CandidateInfo>());
            }
        }

        internal SwitchResult Switch(TargetSelector selector, IReadOnlyList<TargetCandidate> liveOrdered)
        {
            lock (_gate)
            {
                var matches = TargetScan.Filter(selector, liveOrdered);
                if (matches.Count == 0)
                {
                    return new SwitchResult(false, false, _record, _record,
                        Payload(TargetCode.NoTarget, Ref(_record), null,
                            CandidatesFor(selector, liveOrdered), selector));
                }

                var chosen = matches[0];
                var previous = _record;

                // Never bind a provably dead instance — applies to every switch outcome.
                var probe = _probe.Probe(chosen.Pid);
                if (LivenessEvaluator.Evaluate(chosen.ObservedStartUtc, probe).Liveness == Liveness.ProvenDead)
                {
                    return new SwitchResult(false, false, _record, _record,
                        Payload(TargetCode.NoTarget, Ref(_record), null,
                            CandidatesFor(selector, liveOrdered), selector));
                }

                if (_state == BindingState.Bound
                    && previous != null
                    && previous.Pid == chosen.Pid
                    && SameStart(previous.ObservedStartUtc, chosen.ObservedStartUtc))
                {
                    _selector = selector;
                    _record = previous with
                    {
                        Selector = selector,
                        Kind = selector.IsExplicit ? BindingKind.Explicit : BindingKind.Implicit,
                    };
                    return new SwitchResult(true, false, previous, _record, null);
                }

                var start = probe.State == ProcessState.Running ? probe.StartUtc : null;
                var record = new BindingRecord(
                    _generation + 1,
                    selector.IsExplicit ? BindingKind.Explicit : BindingKind.Implicit,
                    selector,
                    chosen.TargetId,
                    chosen.Pid,
                    start,
                    chosen.HostYear,
                    chosen.IdentityVerified && start != null,
                    _utcNow());

                bool differentInstance = previous != null
                    && (previous.Pid != chosen.Pid || !SameStart(previous.ObservedStartUtc, chosen.ObservedStartUtc));

                _generation = record.Generation;
                _record = record;
                _selector = selector;
                _state = BindingState.Bound;
                _lastSwitch = new SwitchInfo(previous?.TargetId, record.TargetId, _utcNow());

                return new SwitchResult(true, differentInstance, previous, record, null);
            }
        }

        internal string? NextTargetId(IReadOnlyList<TargetCandidate> liveOrdered)
        {
            lock (_gate)
            {
                if (_state == BindingState.None)
                    return TargetScan.Filter(_selector, liveOrdered).FirstOrDefault()?.TargetId;
                if (_state == BindingState.Lost
                    && _record != null
                    && _record.Kind == BindingKind.Implicit)
                {
                    var matches = TargetScan.Filter(_selector, liveOrdered);
                    if (matches.Count == 1)
                        return matches[0].TargetId;
                }
                return null;
            }
        }

        // ---- helpers (all called under _gate) ----

        private long EffectiveGeneration(long pinned)
        {
            if (pinned != 0)
                return pinned;
            // A call that arrived in state None may adopt only the first binding of the session.
            return _generation <= 1 ? _generation : 1;
        }

        private bool SameStart(DateTime? a, DateTime? b)
        {
            if (a == null || b == null)
                return true;
            var delta = a.Value - b.Value;
            if (delta < TimeSpan.Zero)
                delta = -delta;
            return delta <= LivenessEvaluator.StartTolerance;
        }

        private string BindingName() =>
            _state == BindingState.None || _record == null
                ? "none"
                : _record.Kind == BindingKind.Explicit ? "explicit" : "implicit";

        private static TargetRef? Ref(BindingRecord? r) =>
            r == null ? null : new TargetRef(r.TargetId, r.Pid, r.HostYear, null);

        private static TargetRef? RefWithTitle(BindingRecord? r, IReadOnlyList<TargetCandidate> liveOrdered)
        {
            if (r == null)
                return null;
            var title = liveOrdered.FirstOrDefault(c => c.Pid == r.Pid)?.WindowTitle;
            return new TargetRef(r.TargetId, r.Pid, r.HostYear, title);
        }

        private TargetRef? CurrentCandidate(IReadOnlyList<TargetCandidate> liveOrdered)
        {
            var c = TargetScan.Filter(_selector, liveOrdered).FirstOrDefault();
            return c == null ? null : new TargetRef(c.TargetId, c.Pid, c.HostYear, c.WindowTitle);
        }

        // Explicit selectors match at most one instance, so list every live instance (spec 6.10).
        private static IReadOnlyList<CandidateInfo> CandidatesFor(
            TargetSelector selector, IReadOnlyList<TargetCandidate> liveOrdered) =>
            (selector.IsExplicit ? liveOrdered : TargetScan.Filter(selector, liveOrdered))
                .Select(c => new CandidateInfo(c.TargetId, c.HostYear, c.Pid, c.IdentityVerified, c.WindowTitle))
                .ToList();

        private TargetPayload Payload(
            TargetCode code,
            TargetRef? previous,
            TargetRef? current,
            IReadOnlyList<CandidateInfo> candidates,
            TargetSelector? selector = null,
            bool generationChanged = false) =>
            TargetPayload.Create(_product, code,
                (selector ?? _selector).Canonical, BindingName(), previous, current, candidates,
                generationChanged);

        // Spec 6.5 payload computed fresh from the live list. State stays Lost.
        private TargetPayload LostPayload(IReadOnlyList<TargetCandidate> liveOrdered)
        {
            var previous = Ref(_record);
            if (_record!.Kind == BindingKind.Explicit)
            {
                return Payload(TargetCode.TargetLost, previous, null,
                    CandidatesAll(liveOrdered));
            }
            var matches = TargetScan.Filter(_record.Selector, liveOrdered);
            if (matches.Count == 0)
                return Payload(TargetCode.NoTarget, previous, null, Array.Empty<CandidateInfo>());
            if (matches.Count == 1)
            {
                var c = matches[0];
                return Payload(TargetCode.TargetChanged, previous,
                    new TargetRef(c.TargetId, c.Pid, c.HostYear, c.WindowTitle),
                    new List<CandidateInfo>
                    {
                        new CandidateInfo(c.TargetId, c.HostYear, c.Pid, c.IdentityVerified, c.WindowTitle),
                    });
            }
            return Payload(TargetCode.AmbiguousTarget, previous, null,
                matches.Select(c => new CandidateInfo(c.TargetId, c.HostYear, c.Pid, c.IdentityVerified, c.WindowTitle))
                    .ToList());
        }

        private static IReadOnlyList<CandidateInfo> CandidatesAll(IReadOnlyList<TargetCandidate> liveOrdered) =>
            liveOrdered
                .Select(c => new CandidateInfo(c.TargetId, c.HostYear, c.Pid, c.IdentityVerified, c.WindowTitle))
                .ToList();
    }
}
