using System;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class DiscoveryPublisherTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bw-discovery-" + Guid.NewGuid().ToString("N"));
        private readonly DateTime _start = new DateTime(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Publish_writes_v3_and_legacy_readable_by_release_parser()
        {
            using var publisher = new DiscoveryPublisher(_dir, "2027", System.Diagnostics.Process.GetCurrentProcess().Id, _start);
            publisher.Publish("pipe", null, "test-pipe", "token");
            var instance = JObject.Parse(File.ReadAllText(Path.Combine(_dir, publisher.FileName)));
            Assert.Equal(3, instance.Value<int>("schema_version"));
            Assert.Equal("revit", instance.Value<string>("host_app"));
            Assert.Equal(2027, instance.Value<int>("host_year"));
            Assert.Equal("2026-10-06T08:00:00.000Z", instance.Value<DateTime>("process_start_utc").ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            Assert.Equal(publisher.TargetId, instance.Value<string>("target_id"));
            Assert.True(RvtMcp.Server.AuthToken.TryParseDiscovery(Path.Combine(_dir, "revit-2027.json"), out var legacy));
            Assert.Equal("test-pipe", legacy.PipeName);
            Assert.Equal("token", legacy.AuthToken);
        }

        [Fact]
        public void Stop_owner_hands_legacy_to_newest_live_peer_and_never_republishes()
        {
            using var older = new DiscoveryPublisher(_dir, "2027", 11, _start, (_, __) => true);
            using var newer = new DiscoveryPublisher(_dir, "2027", 22, _start.AddSeconds(5), (_, __) => true);
            older.Publish("pipe", null, "older", "old-token");
            newer.Publish("pipe", null, "newer", "new-token");
            newer.Dispose();
            var legacy = JObject.Parse(File.ReadAllText(Path.Combine(_dir, "revit-2027.json")));
            Assert.Equal(11, legacy.Value<int>("pid"));
            Assert.False(File.Exists(Path.Combine(_dir, newer.FileName)));
            newer.Publish("pipe", null, "late", "late-token");
            Assert.False(File.Exists(Path.Combine(_dir, newer.FileName)));
            newer.Dispose();
            Assert.Equal(11, JObject.Parse(File.ReadAllText(Path.Combine(_dir, "revit-2027.json"))).Value<int>("pid"));
        }

        [Fact]
        public void Late_stop_of_same_pid_listener_keeps_new_token()
        {
            using var old = new DiscoveryPublisher(_dir, "2027", 11, _start, (_, __) => true);
            using var fresh = new DiscoveryPublisher(_dir, "2027", 11, _start, (_, __) => true);
            old.Publish("pipe", null, "same", "old");
            fresh.Publish("pipe", null, "same", "fresh");
            old.Dispose();
            Assert.Equal("fresh", JObject.Parse(File.ReadAllText(Path.Combine(_dir, fresh.FileName))).Value<string>("auth_token"));
            Assert.Equal("fresh", JObject.Parse(File.ReadAllText(Path.Combine(_dir, "revit-2027.json"))).Value<string>("auth_token"));
        }

        [Fact]
        public void Locked_destination_retries_without_deleting_old_file_then_cleans_unique_temp()
        {
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, "descriptor.json");
            File.WriteAllText(path, "old");
            var delays = new System.Collections.Generic.List<int>();
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.False(AtomicDescriptorFile.TryWrite(path, "new", 11, delay: ms =>
                {
                    delays.Add(ms);
                    Assert.Equal("old", File.ReadAllText(path));
                    Assert.Single(Directory.GetFiles(_dir, "descriptor.json.11.*.tmp"));
                }));
            }
            Assert.Equal(new[] { 50, 100, 200, 400, 800 }, delays);
            Assert.Equal("old", File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
            Assert.True(AtomicDescriptorFile.TryWrite(path, "new", 11));
            Assert.Equal("new", File.ReadAllText(path));
        }

        // ---- handover/delete branches (Dispose) ----

        private void WriteSibling(int pid, DateTime start, string token, string content = null)
        {
            Directory.CreateDirectory(_dir);
            var json = content ?? new JObject
            {
                ["schema_version"] = 3,
                ["host_app"] = "revit",
                ["host_year"] = 2027,
                ["pid"] = pid,
                ["process_start_utc"] = start.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                ["target_id"] = "revit-2027-" + pid,
                ["transport"] = "pipe",
                ["pipe_name"] = "p" + pid,
                ["auth_token"] = token,
                ["capabilities"] = new JArray("tool_catalog")
            }.ToString();
            File.WriteAllText(Path.Combine(_dir, "revit-2027-" + pid + ".json"), json);
        }

        private static string LegacyPath(string dir) => Path.Combine(dir, "revit-2027.json");

        [Fact]
        public void Handover_skips_candidate_whose_pid_was_reused()
        {
            // The file claims start S0, but the live process at pid 33 started at S1 —
            // pid reuse; the candidate must not be chosen (start time mismatch).
            var s0 = _start;
            var s1 = _start.AddMinutes(30);
            var publisher = new DiscoveryPublisher(_dir, "2027", 11, _start,
                (pid, start) => !(pid == 33 && start == s0)); // reused: real start differs
            publisher.Publish("pipe", null, "own", "own-token");
            WriteSibling(33, s0, "peer-token");

            publisher.Dispose();

            // No live candidate → the legacy file we still own is deleted, not handed over.
            Assert.False(File.Exists(LegacyPath(_dir)));
            Assert.True(File.Exists(Path.Combine(_dir, "revit-2027-33.json"))); // peer file untouched
        }

        [Fact]
        public void Handover_skips_candidate_whose_start_cannot_be_read()
        {
            // process_start_utc missing → isAlive is consulted with a null start and
            // (cannot verify) must reject — the candidate is not chosen.
            var seen = new System.Collections.Generic.List<(int pid, DateTime? start)>();
            var publisher = new DiscoveryPublisher(_dir, "2027", 11, _start,
                (pid, start) => { lock (seen) seen.Add((pid, start)); return false; });
            publisher.Publish("pipe", null, "own", "own-token");
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "revit-2027-44.json"), new JObject
            {
                ["schema_version"] = 3, ["host_app"] = "revit", ["host_year"] = 2027,
                ["pid"] = 44, ["target_id"] = "revit-2027-44",
                ["transport"] = "pipe", ["pipe_name"] = "p44", ["auth_token"] = "peer-token"
            }.ToString()); // no process_start_utc at all

            publisher.Dispose();

            Assert.Contains(seen, s => s.pid == 44 && s.start == null); // consulted with null start
            Assert.False(File.Exists(LegacyPath(_dir)));
        }

        [Fact]
        public void Dispose_keeps_legacy_file_belonging_to_another_instance()
        {
            var publisher = new DiscoveryPublisher(_dir, "2027", 11, _start, (_, __) => true);
            publisher.Publish("pipe", null, "own", "own-token");
            // Another instance overwrote the legacy file (its pid, its token).
            var foreign = JObject.Parse(File.ReadAllText(LegacyPath(_dir)));
            foreign["pid"] = 22;
            foreign["auth_token"] = "foreign-token";
            File.WriteAllText(LegacyPath(_dir), foreign.ToString());

            publisher.Dispose();

            Assert.False(File.Exists(Path.Combine(_dir, publisher.FileName))); // own file cleaned
            var surviving = JObject.Parse(File.ReadAllText(LegacyPath(_dir)));
            Assert.Equal(22, surviving.Value<int>("pid"));
            Assert.Equal("foreign-token", surviving.Value<string>("auth_token"));
        }

        [Fact]
        public void Dispose_keeps_legacy_file_when_token_no_longer_ours()
        {
            // Same pid (a newer listener of this process) but a rotated token:
            // the late Dispose of the old lifetime must not delete or hand over.
            var publisher = new DiscoveryPublisher(_dir, "2027", 11, _start, (_, __) => true);
            publisher.Publish("pipe", null, "own", "own-token");
            var legacy = JObject.Parse(File.ReadAllText(LegacyPath(_dir)));
            legacy["auth_token"] = "rotated-token";
            File.WriteAllText(LegacyPath(_dir), legacy.ToString());

            publisher.Dispose();

            Assert.Equal("rotated-token",
                JObject.Parse(File.ReadAllText(LegacyPath(_dir))).Value<string>("auth_token"));
        }

        [Fact]
        public void Handover_deletes_legacy_when_no_candidate_remains()
        {
            var publisher = new DiscoveryPublisher(_dir, "2027", 11, _start, (_, __) => true);
            publisher.Publish("pipe", null, "own", "own-token");

            publisher.Dispose();

            Assert.False(File.Exists(Path.Combine(_dir, publisher.FileName)));
            Assert.False(File.Exists(LegacyPath(_dir)));
        }

        [Fact]
        public void Malformed_sibling_descriptors_are_ignored_during_handover()
        {
            var publisher = new DiscoveryPublisher(_dir, "2027", 11, _start, (_, __) => true);
            publisher.Publish("pipe", null, "own", "own-token");
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "revit-2027-44.json"), "not json at all {");
            WriteSibling(55, _start, "peer-token"); // one valid live peer

            publisher.Dispose();

            var legacy = JObject.Parse(File.ReadAllText(LegacyPath(_dir)));
            Assert.Equal(55, legacy.Value<int>("pid")); // handover picked the valid peer
            Assert.Equal("not json at all {",
                File.ReadAllText(Path.Combine(_dir, "revit-2027-44.json"))); // malformed file untouched
        }

        [Fact]
        public void Handover_prefers_newest_candidate_and_breaks_ties_by_higher_pid()
        {
            var publisher = new DiscoveryPublisher(_dir, "2027", 11, _start, (_, __) => true);
            publisher.Publish("pipe", null, "own", "own-token");
            WriteSibling(33, _start.AddMinutes(10), "newest-loses-pid"); // newest by start
            WriteSibling(44, _start, "tied-high");
            WriteSibling(22, _start, "tied-low"); // same start as 44, lower pid

            publisher.Dispose();

            Assert.Equal(33, JObject.Parse(File.ReadAllText(LegacyPath(_dir))).Value<int>("pid"));

            // Tie case alone: equal starts → the higher pid wins.
            var dir2 = Path.Combine(_dir, "tie");
            var pub2 = new DiscoveryPublisher(dir2, "2027", 11, _start, (_, __) => true);
            pub2.Publish("pipe", null, "own", "own-token");
            Directory.CreateDirectory(dir2);
            foreach (var pid in new[] { 44, 22 })
            {
                File.WriteAllText(Path.Combine(dir2, "revit-2027-" + pid + ".json"), new JObject
                {
                    ["schema_version"] = 3, ["host_app"] = "revit", ["host_year"] = 2027,
                    ["pid"] = pid, ["process_start_utc"] = _start.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    ["target_id"] = "revit-2027-" + pid, ["transport"] = "pipe",
                    ["pipe_name"] = "p" + pid, ["auth_token"] = "tok" + pid,
                    ["capabilities"] = new JArray("tool_catalog")
                }.ToString());
            }
            pub2.Dispose();
            Assert.Equal(44, JObject.Parse(File.ReadAllText(LegacyPath(dir2))).Value<int>("pid"));
        }

        [Fact]
        public void Handover_scan_failure_is_logged_and_keeps_legacy()
        {
            var logs = new System.Collections.Generic.List<string>();
            var publisher = new DiscoveryPublisher(_dir, "2027", 11, _start, (_, __) => true,
                log: m => { lock (logs) logs.Add(m); },
                enumerateFiles: _ => throw new IOException("simulated enumeration failure"));
            publisher.Publish("pipe", null, "own", "own-token");

            publisher.Dispose(); // must not throw

            Assert.False(File.Exists(Path.Combine(_dir, publisher.FileName))); // own file still cleaned
            Assert.True(File.Exists(LegacyPath(_dir))); // legacy kept on scan failure
            Assert.Contains(logs, l => l.Contains("Legacy handover scan failed"));
        }

        [Fact]
        public void Concurrent_publish_and_dispose_are_safe_and_idempotent()
        {
            var publisher = new DiscoveryPublisher(_dir, "2027", 11, _start, (_, __) => true);
            var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            var publishLoop = new Thread(() =>
            {
                try
                {
                    for (var i = 0; i < 200; i++)
                        publisher.Publish("pipe", null, "own", "own-token");
                }
                catch (Exception ex) { errors.Add(ex); }
            }) { IsBackground = true };
            var disposeLoop = new Thread(() =>
            {
                try { publisher.Dispose(); publisher.Dispose(); }
                catch (Exception ex) { errors.Add(ex); }
            }) { IsBackground = true };

            publishLoop.Start();
            disposeLoop.Start();
            Assert.True(publishLoop.Join(30000) && disposeLoop.Join(30000), "threads did not finish");

            Assert.Empty(errors);
            publisher.Publish("pipe", null, "late", "late-token"); // post-dispose no-op
            if (Directory.Exists(_dir))
            {
                Assert.Empty(Directory.GetFiles(_dir, "*.tmp")); // no temp litter
                // Every surviving descriptor is a complete v3 document — never half-written.
                foreach (var path in Directory.GetFiles(_dir, "revit-2027*.json"))
                    Assert.Equal(3, JObject.Parse(File.ReadAllText(path)).Value<int>("schema_version"));
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }
    }
}
