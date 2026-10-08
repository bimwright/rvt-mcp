using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bimwright.Targeting;
using Newtonsoft.Json.Linq;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    /// <summary>
    /// AuthToken.Scan on injected temp dirs with a fake probe (spec §5.3/§5.6):
    /// per-instance + legacy normalization, dedup by pid, proven-dead deletion,
    /// malformed files skipped but never deleted.
    /// </summary>
    public class TargetScannerTests : IDisposable
    {
        private readonly string _dir;

        public TargetScannerTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "rvtmcp-scan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private sealed class FakeProbe : IProcessProbe
        {
            private readonly Dictionary<int, ProcessProbeResult> _byPid = new();

            public void Set(int pid, ProcessState state, DateTime? start = null, string title = null)
                => _byPid[pid] = new ProcessProbeResult(state, start, title);

            public ProcessProbeResult Probe(int pid)
                => _byPid.TryGetValue(pid, out var r) ? r : new ProcessProbeResult(ProcessState.NotFound, null, null);
        }

        private const int Pid = 424242;
        private static readonly DateTime Start = new DateTime(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc);

        private static JObject LegacyJson(int pid = Pid, int year = 2024, int port = 49301)
            => new JObject
            {
                ["schema_version"] = 2,
                ["revit_year"] = year,
                ["transport"] = "tcp",
                ["port"] = port,
                ["pipe_name"] = null,
                ["auth_token"] = "tok-" + pid,
                ["pid"] = pid,
                ["capabilities"] = new JArray("tool_catalog"),
                ["process_start_utc"] = Start.ToString("o"),
            };

        private static JObject PerInstanceJson(int pid = Pid, int year = 2024, string pipeName = null, int? port = 49301)
            => new JObject
            {
                ["schema_version"] = 3,
                ["host_app"] = "revit",
                ["host_year"] = year,
                ["pid"] = pid,
                ["process_start_utc"] = Start.ToString("o"),
                ["target_id"] = $"revit-{year}-{pid}",
                ["transport"] = pipeName != null ? "pipe" : "tcp",
                ["port"] = pipeName != null ? null : port,
                ["pipe_name"] = pipeName,
                ["auth_token"] = "tok-" + pid,
                ["capabilities"] = new JArray("tool_catalog"),
            };

        private string Write(string name, JObject json)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllText(path, json.ToString(Newtonsoft.Json.Formatting.None));
            return path;
        }

        private FakeProbe Alive(params int[] pids)
        {
            var probe = new FakeProbe();
            foreach (var pid in pids) probe.Set(pid, ProcessState.Running, Start, "Revit " + pid);
            return probe;
        }

        [Fact]
        public void Malformed_descriptor_is_skipped_and_never_deleted()
        {
            var path = Path.Combine(_dir, "revit-2024.json");
            File.WriteAllText(path, "{ not json ");

            var result = AuthToken.Scan(new FakeProbe(), _dir);

            Assert.Empty(result.Live);
            Assert.Empty(result.ProvenDead);
            Assert.True(File.Exists(path));
        }

        [Fact]
        public void Legacy_only_descriptor_is_listed()
        {
            Write("revit-2024.json", LegacyJson());

            var result = AuthToken.Scan(Alive(Pid), _dir);

            var c = Assert.Single(result.Live);
            Assert.Equal(Pid, c.Pid);
            Assert.Equal(2024, c.HostYear);
            Assert.Equal("revit-2024-" + Pid, c.TargetId);
            Assert.Equal(DescriptorSource.Legacy, c.Descriptor.Source);
            Assert.True(c.IdentityVerified);
        }

        [Fact]
        public void Per_instance_and_legacy_same_pid_dedup_to_per_instance()
        {
            Write("revit-2024.json", LegacyJson());
            Write($"revit-2024-{Pid}.json", PerInstanceJson());

            var result = AuthToken.Scan(Alive(Pid), _dir);

            var c = Assert.Single(result.Live);
            Assert.Equal(DescriptorSource.PerInstance, c.Descriptor.Source);
        }

        [Fact]
        public void Dead_owner_descriptor_is_dropped_and_deleted()
        {
            var path = Write("revit-2024.json", LegacyJson());
            var probe = new FakeProbe(); // pid not registered → NotFound
            probe.Set(Pid, ProcessState.Exited, Start);

            var result = AuthToken.Scan(probe, _dir);

            Assert.Empty(result.Live);
            Assert.Single(result.ProvenDead);
            Assert.False(File.Exists(path));
        }

        [Fact]
        public void Reused_pid_with_mismatched_start_is_proven_dead_and_deleted()
        {
            var path = Write($"revit-2024-{Pid}.json", PerInstanceJson());
            var probe = new FakeProbe();
            probe.Set(Pid, ProcessState.Running, Start.AddMinutes(5), "Other process");

            var result = AuthToken.Scan(probe, _dir);

            Assert.Empty(result.Live);
            Assert.Single(result.ProvenDead);
            Assert.False(File.Exists(path));
        }

        [Fact]
        public void Access_denied_owner_is_kept_alive_but_unverified()
        {
            var path = Write($"revit-2024-{Pid}.json", PerInstanceJson());
            var probe = new FakeProbe();
            probe.Set(Pid, ProcessState.AccessDenied);

            var result = AuthToken.Scan(probe, _dir);

            var c = Assert.Single(result.Live);
            Assert.False(c.IdentityVerified);
            Assert.Empty(result.ProvenDead);
            Assert.True(File.Exists(path));
        }

        [Fact]
        public void ReadDescriptorFor_prefers_per_instance_then_legacy_with_same_pid()
        {
            Write("revit-2024.json", LegacyJson());
            Write($"revit-2024-{Pid}.json", PerInstanceJson());

            var d = AuthToken.ReadDescriptorFor(Pid, 2024, _dir);

            Assert.NotNull(d);
            Assert.Equal(DescriptorSource.PerInstance, d.Source);
        }

        [Fact]
        public void ReadDescriptorFor_falls_back_to_legacy_with_same_pid_only()
        {
            Write("revit-2024.json", LegacyJson(pid: 999999));

            Assert.Null(AuthToken.ReadDescriptorFor(Pid, 2024, _dir));

            Write("revit-2024.json", LegacyJson());
            var d = AuthToken.ReadDescriptorFor(Pid, 2024, _dir);
            Assert.NotNull(d);
            Assert.Equal(DescriptorSource.Legacy, d.Source);
        }
    }
}
