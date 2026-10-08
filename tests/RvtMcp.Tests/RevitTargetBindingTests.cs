using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Bimwright.Targeting;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>
    /// Server binding facade over the shared core: snapshot shape, generation
    /// pinning, switch semantics, next-target proposal — with a fake probe.
    /// </summary>
    public class RevitTargetBindingTests : IDisposable
    {
        private const int PidA = 424242;
        private const int PidB = 434343;
        private static readonly DateTime StartA = new DateTime(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime StartB = new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc);

        private sealed class FakeProbe : IProcessProbe
        {
            private readonly Dictionary<int, ProcessProbeResult> _byPid = new();

            public void Set(int pid, ProcessState state, DateTime? start = null, string title = null)
                => _byPid[pid] = new ProcessProbeResult(state, start, title);

            public ProcessProbeResult Probe(int pid)
                => _byPid.TryGetValue(pid, out var r) ? r : new ProcessProbeResult(ProcessState.NotFound, null, null);
        }

        private readonly FakeProbe _probe = new FakeProbe();

        public RevitTargetBindingTests()
        {
            _probe.Set(PidA, ProcessState.Running, StartA, "Revit A");
            _probe.Set(PidB, ProcessState.Running, StartB, "Revit B");
            RevitTargetBinding.ResetForTests(_probe);
        }

        public void Dispose() => RevitTargetBinding.ResetForTests();

        private static TargetCandidate Candidate(int pid, int year, DateTime start, string title)
        {
            var d = new TargetDescriptor
            {
                Product = HostProduct.Revit,
                HostApp = "revit",
                HostYear = year,
                Pid = pid,
                ProcessStartUtc = start,
                TargetId = $"revit-{year}-{pid}",
                Transport = "tcp",
                Port = 49301,
                AuthToken = "tok",
                Capabilities = new[] { "tool_catalog" },
                Source = DescriptorSource.PerInstance,
                FileName = $"revit-{year}-{pid}.json",
            };
            return new TargetCandidate(d, identityVerified: true, observedStartUtc: start, windowTitle: title);
        }

        private IReadOnlyList<TargetCandidate> Live() => TargetScan.Order(new[]
        {
            Candidate(PidA, 2024, StartA, "Revit A"),
            Candidate(PidB, 2024, StartB, "Revit B"),
        });

        [Fact]
        public void Fresh_binding_is_none_with_generation_zero()
        {
            var snap = RevitTargetBinding.Snapshot();
            Assert.Equal(BindingState.None, snap.State);
            Assert.Equal(0, snap.Generation);
            Assert.Null(snap.Record);
            Assert.Equal(0, RevitTargetBinding.CaptureGeneration());
        }

        [Fact]
        public void Switch_to_live_candidate_binds_with_generation_one()
        {
            var result = RevitTargetBinding.Switch(TargetSelector.ForPid(PidA), Live());

            Assert.True(result.Ok);
            Assert.False(result.InstanceChanged); // nothing was bound before
            var snap = RevitTargetBinding.Snapshot();
            Assert.Equal(BindingState.Bound, snap.State);
            Assert.Equal(1, snap.Generation);
            Assert.Equal(PidA, snap.Record.Pid);
            Assert.Equal(BindingKind.Explicit, snap.Record.Kind);
        }

        [Fact]
        public void Switch_to_same_instance_keeps_generation_and_updates_selector()
        {
            var live = Live();
            RevitTargetBinding.Switch(TargetSelector.ForPid(PidA), live);
            var before = RevitTargetBinding.Snapshot();

            var result = RevitTargetBinding.Switch(TargetSelector.ForTargetId("revit-2024-" + PidA), live);

            Assert.True(result.Ok);
            Assert.False(result.InstanceChanged);
            var snap = RevitTargetBinding.Snapshot();
            Assert.Equal(before.Generation, snap.Generation);
            Assert.Equal(PidA, snap.Record.Pid);
            Assert.Equal(SelectorKind.TargetId, snap.Record.Selector.Kind);
        }

        [Fact]
        public void Switch_to_different_instance_bumps_generation_and_records_last_switch()
        {
            var live = Live();
            RevitTargetBinding.Switch(TargetSelector.ForPid(PidA), live);

            var result = RevitTargetBinding.Switch(TargetSelector.ForPid(PidB), live);

            Assert.True(result.Ok);
            Assert.True(result.InstanceChanged);
            var snap = RevitTargetBinding.Snapshot();
            Assert.Equal(2, snap.Generation);
            Assert.Equal(PidB, snap.Record.Pid);
            Assert.Equal("revit-2024-" + PidA, snap.LastSwitch.From);
            Assert.Equal("revit-2024-" + PidB, snap.LastSwitch.To);
        }

        [Fact]
        public void Switch_to_dead_candidate_fails_without_touching_binding()
        {
            var live = Live();
            RevitTargetBinding.Switch(TargetSelector.ForPid(PidA), live);
            var before = RevitTargetBinding.Snapshot();
            _probe.Set(PidB, ProcessState.Exited, StartB);

            var result = RevitTargetBinding.Switch(TargetSelector.ForPid(PidB), live);

            Assert.False(result.Ok);
            Assert.Equal(TargetCode.NoTarget, result.Failure.Code);
            var snap = RevitTargetBinding.Snapshot();
            Assert.Equal(before.Generation, snap.Generation);
            Assert.Equal(PidA, snap.Record.Pid);
        }

        [Fact]
        public void Switch_with_no_match_returns_no_target_and_keeps_binding()
        {
            RevitTargetBinding.Switch(TargetSelector.ForPid(PidA), Live());

            var result = RevitTargetBinding.Switch(TargetSelector.ForYear(2026), Live());

            Assert.False(result.Ok);
            Assert.Equal(TargetCode.NoTarget, result.Failure.Code);
            Assert.Equal(PidA, RevitTargetBinding.Snapshot().Record.Pid);
        }

        [Fact]
        public void NextTargetId_proposes_first_match_when_unbound()
        {
            // §6.3 order: same year → later start first → B (StartB > StartA).
            Assert.Equal("revit-2024-" + PidB, RevitTargetBinding.NextTargetId(Live()));
        }

        [Fact]
        public void NextTargetId_is_null_while_bound()
        {
            RevitTargetBinding.Switch(TargetSelector.ForPid(PidA), Live());
            Assert.Null(RevitTargetBinding.NextTargetId(Live()));
        }

        [Fact]
        public void CanSend_rejects_stale_pinned_generation()
        {
            RevitTargetBinding.Switch(TargetSelector.ForPid(PidA), Live());
            var pinned = RevitTargetBinding.CaptureGeneration();
            RevitTargetBinding.Switch(TargetSelector.ForPid(PidB), Live());

            var ok = RevitTargetBinding.Binding.CanSend(pinned, Live(), out var failure);

            Assert.False(ok);
            Assert.Equal(TargetCode.TargetChanged, failure.Code);
            Assert.True(failure.ToJson()["target"] != null || failure.Current != null);
        }

        [Fact]
        public void Implicit_binding_with_single_replacement_waits_for_switch_confirmation()
        {
            // Single-Revit restart: the bound instance exits and exactly one new instance is live.
            RevitTargetBinding.Switch(TargetSelector.Auto, new[] { Candidate(PidA, 2024, StartA, "Revit A") });
            var pinned = RevitTargetBinding.CaptureGeneration();
            _probe.Set(PidA, ProcessState.Exited, StartA);
            var onlyB = new[] { Candidate(PidB, 2024, StartB, "Revit B") };

            for (var i = 0; i < 2; i++) // never rebinds on its own, however often the call repeats
            {
                var plan = Assert.IsType<FailPlan>(RevitTargetBinding.Binding.PlanCall(pinned, onlyB));
                Assert.Equal(TargetCode.TargetChanged, plan.Payload.Code);
                Assert.False(plan.Payload.Sent);
                Assert.Equal("revit-2024-" + PidB, plan.Payload.Current.TargetId);
                Assert.True(RevitTargetBinding.Snapshot().ConfirmationRequired);
            }

            var result = RevitTargetBinding.Switch(TargetSelector.Auto, onlyB);

            Assert.True(result.Ok);
            var snap = RevitTargetBinding.Snapshot();
            Assert.Equal(PidB, snap.Record.Pid);
            Assert.False(snap.ConfirmationRequired);
            Assert.True(RevitTargetBinding.Binding.CanSend(snap.Generation, onlyB, out _));
        }

        [Fact]
        public void Title_cache_prunes_entries_older_than_ttl()
        {
            var probe = new SystemProcessProbe(withTitles: true);
            var cache = (System.Collections.IDictionary)typeof(SystemProcessProbe)
                .GetField("_titleCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .GetValue(probe);
            using var p1 = Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1")
                { CreateNoWindow = true, UseShellExecute = false });
            using var p2 = Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1")
                { CreateNoWindow = true, UseShellExecute = false });
            try
            {
                probe.Probe(p1.Id);
                Assert.Single(cache);
                Thread.Sleep(2100); // past the 2 s TTL — p1's entry is now stale
                probe.Probe(p2.Id); // cache miss: inserts p2 and prunes the stale p1 entry
                Assert.Single(cache);
            }
            finally
            {
                try { p1.Kill(); } catch { }
                try { p2.Kill(); } catch { }
            }
        }
    }
}
