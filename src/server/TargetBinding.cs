#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using Bimwright.Targeting;

namespace RvtMcp.Server
{
    /// <summary>
    /// Real-process implementation of the core liveness probe (spec §5.6).
    /// Window titles are cached for at most 2 s per pid (spec §6.9).
    /// </summary>
    internal sealed class SystemProcessProbe : IProcessProbe
    {
        private static readonly TimeSpan TitleCacheTtl = TimeSpan.FromSeconds(2);
        private readonly Dictionary<int, (DateTime AtUtc, string? Title)> _titleCache = new();
        private readonly object _gate = new();
        private readonly bool _withTitles;

        /// <param name="withTitles">False skips MainWindowTitle reads — the connect
        /// path only needs liveness; display paths fill titles separately.</param>
        public SystemProcessProbe(bool withTitles = true)
        {
            _withTitles = withTitles;
        }

        public ProcessProbeResult Probe(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited)
                    return new ProcessProbeResult(ProcessState.Exited, SafeStartUtc(p), null);
                return new ProcessProbeResult(ProcessState.Running, SafeStartUtc(p),
                    _withTitles ? WindowTitle(pid, p) : null);
            }
            catch (ArgumentException)
            {
                return new ProcessProbeResult(ProcessState.NotFound, null, null);
            }
            catch (Win32Exception)
            {
                return new ProcessProbeResult(ProcessState.AccessDenied, null, null);
            }
            catch (InvalidOperationException)
            {
                // The process exited between GetProcessById and the first read.
                return new ProcessProbeResult(ProcessState.Exited, null, null);
            }
        }

        private static DateTime? SafeStartUtc(Process p)
        {
            try { return p.StartTime.ToUniversalTime(); }
            catch (Win32Exception) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        private string? WindowTitle(int pid, Process p)
        {
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                if (_titleCache.TryGetValue(pid, out var cached) && now - cached.AtUtc < TitleCacheTtl)
                    return cached.Title;
                string? title;
                try { title = p.MainWindowTitle; }
                catch { title = null; }
                if (string.IsNullOrEmpty(title)) title = null;
                _titleCache[pid] = (now, title);
                // Drop expired entries so dead pids cannot grow the cache without bound.
                List<int>? stale = null;
                foreach (var kv in _titleCache)
                    if (now - kv.Value.AtUtc >= TitleCacheTtl)
                        (stale ??= new List<int>()).Add(kv.Key);
                if (stale != null)
                    foreach (var s in stale)
                        _titleCache.Remove(s);
                return title;
            }
        }
    }

    /// <summary>
    /// Per tool-call target context (spec §8): the generation pinned when the call
    /// arrived, and the §6.10 payload the binding layer produced (if any).
    /// </summary>
    internal sealed class TargetCallContext
    {
        private static readonly AsyncLocal<TargetCallContext?> Slot = new();

        public static TargetCallContext? Current => Slot.Value;

        public long PinnedGeneration;
        public TargetPayload? Payload;
        public bool InToolCall;

        internal static TargetCallContext Begin()
        {
            var ctx = new TargetCallContext
            {
                PinnedGeneration = RevitTargetBinding.CaptureGeneration(),
                InToolCall = true
            };
            Slot.Value = ctx;
            return ctx;
        }

        internal static void End(TargetCallContext ctx)
        {
            if (Slot.Value == ctx) Slot.Value = null;
        }
    }

    /// <summary>
    /// Server-side facade over the core <see cref="TargetBinding"/> state machine:
    /// one binding for this process, the initial selector from --target/env/config,
    /// and the scan helper that joins the descriptor scanner with the process probe.
    /// </summary>
    internal static class RevitTargetBinding
    {
        private static readonly object InitGate = new();
        private static TargetBinding? _binding;
        private static IProcessProbe _probe = new SystemProcessProbe();
        private static IProcessProbe _probeNoTitle = new SystemProcessProbe(withTitles: false);
        private static TargetSelector _initialSelector = TargetSelector.Auto;

        /// <summary>Install the initial selector (called once from Program after CLI parse).</summary>
        internal static void Initialize(TargetSelector selector)
        {
            lock (InitGate)
            {
                _initialSelector = selector;
                _binding = new TargetBinding(HostProduct.Revit, selector, _probeNoTitle, () => DateTime.UtcNow);
            }
        }

        /// <summary>Test seam: swap in a fake probe and reset the binding. null restores defaults.</summary>
        internal static void ResetForTests(IProcessProbe? probe = null, TargetSelector? selector = null)
        {
            lock (InitGate)
            {
                _probe = probe ?? new SystemProcessProbe();
                _probeNoTitle = probe ?? new SystemProcessProbe(withTitles: false);
                _initialSelector = selector ?? TargetSelector.Auto;
                _binding = new TargetBinding(HostProduct.Revit, _initialSelector, _probeNoTitle, () => DateTime.UtcNow);
            }
        }

        internal static TargetBinding Binding
        {
            get
            {
                lock (InitGate)
                    return _binding ??= new TargetBinding(HostProduct.Revit, _initialSelector, _probeNoTitle, () => DateTime.UtcNow);
            }
        }

        internal static IProcessProbe Probe
        {
            get { lock (InitGate) return _probe; }
        }

        private static IProcessProbe ProbeNoTitle
        {
            get { lock (InitGate) return _probeNoTitle; }
        }

        internal static long CaptureGeneration() => Binding.CaptureGeneration();

        internal static BindingSnapshot Snapshot() => Binding.Snapshot();

        /// <summary>Scan the discovery dir and evaluate liveness (also deletes proven-dead files).</summary>
        internal static ScanResult Scan(string? dir = null) => AuthToken.Scan(Probe, dir);

        /// <summary>Connect-path scan: same liveness evaluation without window-title reads.</summary>
        internal static ScanResult ScanNoTitles(string? dir = null) => AuthToken.Scan(ProbeNoTitle, dir);

        /// <summary>Live window-title lookup for display/error-envelope enrichment.</summary>
        internal static string? WindowTitle(int pid)
        {
            try { return Probe.Probe(pid).WindowTitle; }
            catch { return null; }
        }

        internal static IReadOnlyList<TargetCandidate> ScanLive(string? dir = null) => Scan(dir).Live;

        internal static string? NextTargetId(IReadOnlyList<TargetCandidate> liveOrdered) =>
            Binding.NextTargetId(liveOrdered);

        internal static SwitchResult Switch(TargetSelector selector, IReadOnlyList<TargetCandidate> liveOrdered) =>
            Binding.Switch(selector, liveOrdered);
    }

    /// <summary>
    /// A target-layer failure carrying the §6.10 payload. The message is always
    /// "&lt;CODE&gt;: &lt;message&gt;"; the call filter replaces the tool result with
    /// the payload envelope.
    /// </summary>
    internal sealed class TargetException : InvalidOperationException
    {
        public TargetException(TargetPayload payload)
            : base(TargetPayload.CodeName(payload.Code) + ": " + payload.NextStep)
        {
            Payload = payload;
            var ctx = TargetCallContext.Current;
            if (ctx != null) ctx.Payload = payload;
        }

        public TargetPayload Payload { get; }
    }
}
