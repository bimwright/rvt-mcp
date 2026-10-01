using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server.Memory;
using Xunit;

namespace RvtMcp.Tests
{
    public class ChangeHistoryTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rvt-history-" + Guid.NewGuid().ToString("N"));
        private ChangeHistoryStore Store => new ChangeHistoryStore(_root);
        internal static JObject Capture(string path = @"C:\private-client\one\model.rvt", int count = 1)
        {
            var model = ChangeHistoryIdentity.Create(path, "file", "Same model title", path);
            var accumulator = new DocumentChangeAccumulator();
            for (int i = 1; i <= count; i++) accumulator.Observe(i, "modified", "Walls", "unique-" + i);
            return new JObject
            {
                ["callId"] = Guid.NewGuid().ToString("N"), ["session"] = Guid.NewGuid().ToString("N"), ["at"] = "2026-10-01T09:00:00Z",
                ["tool"] = "set_element_parameter_values", ["success"] = true, ["complete"] = true, ["revitYear"] = "2027", ["user"] = "tester", ["machine"] = "test-host",
                ["activeModel"] = model.DeepClone(), ["documents"] = new JArray(new JObject
                { ["model"] = model, ["summary"] = accumulator.Snapshot("Same model title"), ["elements"] = accumulator.HistoryElements() })
            };
        }
        private static string Key(JObject payload) => payload["activeModel"].Value<string>("key");
        private static JObject Reason(string reason = "Coordination request") => new JObject { ["request"] = "Adjust Comments", ["goal"] = "Coordination", ["reason"] = reason };

        [Fact]
        public void Identity_is_case_stable_but_never_derived_from_title()
        {
            var a = Capture(); var b = Capture(@"c:\PRIVATE-CLIENT\ONE\MODEL.RVT"); var other = Capture(@"C:\private-client\two\model.rvt");
            Assert.Equal(Key(a), Key(b)); Assert.NotEqual(Key(a), Key(other));
            Assert.Null(ChangeHistoryIdentity.Create("", "file", "Same model title"));
            Store.Ingest(a); Store.Ingest(other);
            Assert.Single(Store.Query(Key(a))["calls"]); Assert.Single(Store.Query(Key(other))["calls"]);
            Assert.Throws<ArgumentException>(() => Store.Record(Key(a), new[] { other.Value<string>("callId") }, Reason()));
        }

        [Fact]
        public void Full_history_finds_an_ID_beyond_the_public_cap_after_reopening()
        {
            var payload = Capture(count: 1000);
            Assert.Equal(200, payload["documents"][0]["summary"]["modified"]["ids"].Count());
            var receipt = Store.Ingest(payload);
            Assert.Equal("recorded", receipt.Value<string>("status"));
            var call = Assert.Single(Store.Query(Key(payload), elementId: 999)["calls"]);
            Assert.Equal(1000, call.Value<int>("elementCount"));
            Assert.Equal("unassigned", call.Value<string>("reasonStatus"));
            Assert.Equal(999, Assert.Single(call["elements"]).Value<long>("elementId"));
            Assert.Single(Store.Query(Key(payload), uniqueId: "unique-999")["calls"]);
            Assert.True(Assert.Single(Store.Query(Key(payload))["calls"]).Value<bool>("elementsTruncated"));
        }

        [Fact]
        public void Explicit_reason_assignment_is_atomic_and_does_not_claim_unselected_calls()
        {
            var first = Capture(); var second = Capture(); Store.Ingest(first); Store.Ingest(second);
            var firstId = first.Value<string>("callId"); var secondId = second.Value<string>("callId");
            Assert.Throws<ArgumentException>(() => Store.Record(Key(first), new[] { firstId, Guid.NewGuid().ToString("N") }, Reason()));
            Assert.All(Store.Query(Key(first))["calls"], c => Assert.Equal("unassigned", c.Value<string>("reasonStatus")));
            Store.Record(Key(first), new[] { firstId }, Reason());
            Assert.Throws<ArgumentException>(() => Store.Record(Key(first), new[] { firstId, secondId }, Reason()));
            var calls = Store.Query(Key(first))["calls"];
            Assert.Equal("recorded", calls.Single(c => c.Value<string>("callId") == firstId).Value<string>("reasonStatus"));
            Assert.Equal("unassigned", calls.Single(c => c.Value<string>("callId") == secondId).Value<string>("reasonStatus"));
        }

        [Fact]
        public void Empty_reason_is_unknown_and_sensitive_text_never_returns()
        {
            var payload = Capture(); Store.Ingest(payload);
            var reason = Reason(""); reason["request"] = @"Open C:\secret\customer.rvt";
            reason["survey"] = new JObject { ["password"] = "hidden-passphrase", ["api_key"] = "hidden-key" };
            var record = Store.Record(Key(payload), new[] { payload.Value<string>("callId") }, reason);
            Assert.False(record.Value<bool>("reasonKnown"));
            var query = Store.Query(Key(payload));
            Assert.Equal("unknown", Assert.Single(query["calls"]).Value<string>("reasonStatus"));
            Assert.DoesNotContain("private-client", query.ToString()); Assert.DoesNotContain("secret", query.ToString());
            Assert.DoesNotContain("hidden-passphrase", query.ToString()); Assert.DoesNotContain("hidden-key", query.ToString());
        }

        [Fact]
        public void Trusted_parameter_values_are_stored_and_arbitrary_reason_fields_are_rejected()
        {
            var payload = Capture();
            payload["parameterValues"] = new JObject { ["parameter"] = "Comments", ["updated"] = new JArray(new JObject
            { ["elementId"] = 1, ["oldValue"] = "before", ["newValue"] = "after", ["oldDisplayValue"] = "before", ["newDisplayValue"] = "after" }) };
            payload["success"] = false; // Other requested elements failed, but this updated row committed.
            Store.Ingest(payload);
            var element = Assert.Single(Assert.Single(Store.Query(Key(payload))["calls"])["elements"]);
            Assert.Equal("before", element["before"].Value<string>("value")); Assert.Equal("after", element["after"].Value<string>("value"));
            var reason = Reason(); reason["elements"] = new JArray(999);
            Assert.Throws<ArgumentException>(() => Store.Record(Key(payload), new[] { payload.Value<string>("callId") }, reason));
        }

        [Fact]
        public void Incomplete_capture_and_deleted_unknown_metadata_remain_explicit()
        {
            var accumulator = new DocumentChangeAccumulator(); accumulator.Observe(1, "modified", "Walls", "uid");
            accumulator.Observe(2, "added"); accumulator.Observe(2, "deleted"); accumulator.Observe(3, "deleted");
            Assert.Equal(2, accumulator.HistoryElements().Count); Assert.Null(accumulator.HistoryElements()[1].Value<string>("uniqueId"));
            accumulator.MarkIncomplete("group rollback"); Assert.Empty(accumulator.HistoryElements());
            var payload = Capture(); payload["complete"] = false;
            payload["documents"][0]["elements"] = accumulator.HistoryElements(); payload["documents"][0]["summary"] = accumulator.Snapshot("Same model title");
            Store.Ingest(payload);
            var call = Assert.Single(Store.Query(Key(payload))["calls"]);
            Assert.False(call.Value<bool>("complete")); Assert.Empty(call["elements"]);
        }

        [Fact]
        public void No_change_context_creates_no_database_and_private_transfer_is_consumed()
        {
            var payload = Capture(); payload["documents"] = new JArray();
            var id = payload.Value<string>("callId"); Store.Transfer.Write(id, payload);
            var result = Store.Consume(id);
            Assert.Equal("no_changes", result.Value<string>("status")); Assert.Equal(Key(payload), result.Value<string>("modelKey"));
            Assert.False(Directory.Exists(Path.Combine(_root, "projects"))); Assert.Empty(Store.Transfer.PendingIds());
            Assert.DoesNotContain("private-client", result.ToString());
        }

        [Fact]
        public void Pending_capture_survives_storage_failure_and_recovers_idempotently()
        {
            var payload = Capture(); var id = payload.Value<string>("callId"); Store.Transfer.Write(id, payload);
            var projects = Path.Combine(_root, "projects"); File.WriteAllText(projects, "block directory creation");
            Assert.ThrowsAny<IOException>(() => Store.Consume(id)); Assert.Single(Store.Transfer.PendingIds());
            File.Delete(projects); Store.RecoverPending(); Assert.Empty(Store.Transfer.PendingIds());
            Store.Ingest(payload); Assert.Single(Store.Query(Key(payload))["calls"]);
            payload["documents"][0]["elements"][0]["elementId"] = 2;
            Assert.Throws<InvalidOperationException>(() => Store.Ingest(payload));
        }

        [Fact]
        public async Task Concurrent_writers_keep_both_calls_and_replay_does_not_duplicate()
        {
            var a = Capture(); var b = Capture(); Store.Ingest(Capture()); // initialize before racing connections
            await Task.WhenAll(Task.Run(() => Store.Ingest(a)), Task.Run(() => Store.Ingest(b)), Task.Run(() => Store.Ingest(a)));
            Assert.Equal(3, Store.Query(Key(a))["calls"].Count());
        }

        [Fact]
        public void Unresolved_identity_retains_pending_capture_instead_of_merging_by_title()
        {
            var payload = Capture(); payload["documents"][0]["model"] = null;
            Store.Transfer.Write(payload.Value<string>("callId"), payload);
            Assert.Equal("partial", Store.Consume(payload.Value<string>("callId")).Value<string>("status"));
            Assert.Single(Store.Transfer.PendingIds()); Assert.False(Directory.Exists(Path.Combine(_root, "projects")));
        }

        [Fact]
        public void Queries_validate_date_limit_and_paths_without_creating_files()
        {
            var payload = Capture(); Store.Ingest(payload);
            Assert.Single(Store.Query(Key(payload), from: "2026-10-01", until: "2026-10-02")["calls"]);
            Assert.Empty(Store.Query(Key(payload), from: "2026-10-02")["calls"]);
            Assert.Throws<ArgumentException>(() => Store.Query(Key(payload), limit: 0));
            Assert.Throws<ArgumentException>(() => Store.Query(Key(payload), from: "bad-date"));
            Assert.Throws<ArgumentException>(() => Store.Query(Key(payload), from: "2026-10-02", until: "2026-10-01"));
            Assert.Throws<ArgumentException>(() => Store.Query("../outside"));
            Assert.Throws<ArgumentException>(() => Store.Transfer.Read("../outside"));
        }

        [Fact]
        public void Config_default_and_overrides_are_independent_of_call_logging()
        {
            var config = new RvtMcpConfig(); Assert.True(config.EnableChangeHistoryOrDefault); Assert.False(config.EnableCallLogOrDefault);
            RvtMcpConfig.ApplyEnvVars(config, key => key == RvtMcpConfig.EnvEnableChangeHistory ? "false" : null);
            Assert.False(config.EnableChangeHistoryOrDefault);
            RvtMcpConfig.ApplyCliArgs(config, new[] { "--enable-change-history" }); Assert.True(config.EnableChangeHistoryOrDefault);
            RvtMcpConfig.ApplyCliArgs(config, new[] { "--disable-change-history" });
            Assert.False(new RvtMcpConfig().WithRuntimeOptions(config.ToRuntimeOptions()).EnableChangeHistoryOrDefault);
        }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
