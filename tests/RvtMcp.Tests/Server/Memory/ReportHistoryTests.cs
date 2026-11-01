using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Reports.History;
using RvtMcp.Server.Memory;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class ReportHistoryTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rvt-report-history-" + Guid.NewGuid().ToString("N"));
        private readonly string _id = Guid.NewGuid().ToString("N");
        private readonly ChangeHistoryStore _store;
        private readonly ReportHistoryLookup _lookup;
        public ReportHistoryTests() { _store = new ChangeHistoryStore(_root); _lookup = new ReportHistoryLookup(_root); }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        private JObject Reference(JObject payload) => new JObject { ["call_id"] = payload["callId"], ["client_session"] = payload["session"],
            ["model_key"] = payload["activeModel"]["key"], ["tool"] = payload["tool"], ["history_requested"] = true };
        private void Save(params JObject[] refs)
        {
            var dir = Path.Combine(_root, "reports"); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "20261031-" + _id + ".json"), new JObject { ["schema_version"] = 1, ["report_id"] = _id,
                ["changes"] = new JObject { ["scope"] = "calls", ["calls"] = new JArray(refs) } }.ToString(Formatting.None));
        }
        private JObject Call() => (JObject)_lookup.Read(_id)["calls"].Single();
        [Fact] public void Exact_call_returns_record_reason_and_before_after_then_reflects_new_reason_without_rewriting_report()
        {
            var payload = ChangeHistoryTests.Capture(count: 2);
            payload["parameterValues"] = new JObject { ["parameter"] = "Comments", ["updated"] = new JArray(new JObject
            { ["elementId"] = 1, ["oldValue"] = "old", ["newValue"] = "new" }) };
            _store.Ingest(payload); Save(Reference(payload));
            Assert.Equal("unassigned", Call().Value<string>("reason_status"));
            _store.Record(payload["activeModel"].Value<string>("key"), new[] { payload.Value<string>("callId") },
                new JObject { ["request"] = "Synthetic adjustment", ["goal"] = "Synthetic review", ["reason"] = "User requested coordination" });
            var call = Call();
            Assert.Equal("available", call.Value<string>("status")); Assert.Equal("recorded", call.Value<string>("reason_status"));
            Assert.NotNull(call.Value<string>("record_id")); Assert.Equal("User requested coordination", call["reason"].Value<string>("reason"));
            Assert.Equal("old", call["elements"][0]["before"].Value<string>("value"));
            Assert.Equal("new", call["elements"][0]["after"].Value<string>("value"));
        }
        [Fact] public void Session_and_model_must_match_even_if_call_id_and_title_match()
        {
            var payload = ChangeHistoryTests.Capture(); _store.Ingest(payload);
            var reference = Reference(payload); reference["client_session"] = Guid.NewGuid().ToString("N"); Save(reference);
            Assert.Equal("pending_or_missing", Call().Value<string>("status")); Assert.Empty(Call()["elements"]);
            reference = Reference(payload); reference["model_key"] = new string('f', 64); Save(reference);
            Assert.Equal("unavailable", Call().Value<string>("status"));
            Assert.False(File.Exists(Path.Combine(_root, "projects", new string('f', 64) + ".db")));
        }
        [Theory] [InlineData("disabled")] [InlineData("model_unavailable")] [InlineData("invalid_reference")]
        public void Missing_evidence_never_creates_a_database_or_guesses_links(string status)
        {
            var reference = Reference(ChangeHistoryTests.Capture());
            if (status == "disabled") reference["history_requested"] = false;
            if (status == "model_unavailable") reference["model_key"] = "../elsewhere";
            if (status == "invalid_reference") reference["call_id"] = "invalid";
            Save(reference); Assert.Equal(status, Call().Value<string>("status"));
            Assert.False(Directory.Exists(Path.Combine(_root, "projects")));
        }
        [Fact] public void Calls_and_elements_have_independent_bounded_pagination()
        {
            var a = ChangeHistoryTests.Capture(count: 12); var b = ChangeHistoryTests.Capture(count: 1);
            _store.Ingest(a); _store.Ingest(b); Save(Reference(a), Reference(b));
            var page = _lookup.Read(_id, callLimit: 1);
            Assert.Equal(1, page.Value<int>("next_call_offset")); Assert.Equal(10, page["calls"][0]["elements"].Count());
            Assert.Equal(10, page["calls"][0].Value<int>("next_element_offset"));
            Assert.Equal(2, _lookup.Read(_id, elementOffset: 10, callLimit: 1)["calls"][0]["elements"].Count());
            Assert.Equal(b.Value<string>("callId"), _lookup.Read(_id, callOffset: 1, callLimit: 1)["calls"][0].Value<string>("call_id"));
        }
        [Fact] public void Legacy_report_has_no_invented_provenance() { Save(); Assert.Equal("no_provenance", _lookup.Read(_id).Value<string>("status")); }
        [Theory] [InlineData("../../anything")] [InlineData("bad")]
        public void Invalid_report_id_is_rejected_before_file_access(string id) => Assert.Throws<ArgumentException>(() => _lookup.Read(id));
        [Fact] public void Oversized_values_are_labelled_instead_of_unbounded_output()
        {
            var payload = ChangeHistoryTests.Capture(); payload["parameterValues"] = new JObject { ["parameter"] = "Comments", ["updated"] = new JArray(new JObject
            { ["elementId"] = 1, ["newValue"] = new string('x', 20000) }) };
            _store.Ingest(payload); Save(Reference(payload));
            Assert.True(Call()["elements"][0]["after"].Value<bool>("truncated"));
        }
    }
}
