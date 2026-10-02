using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Plugin.Survey;
using RvtMcp.Plugin.Views.Toast;
using RvtMcp.Server;
using RvtMcp.Tests.Helpers;
using Xunit;

namespace RvtMcp.Tests
{
    public class SurveyChangeImpactTests
    {
        private static SurveyOptions Options() => SurveyOptions.Parse(JObject.Parse(
            "{\"elementIds\":[1],\"scopeThreshold\":20,\"changeKind\":\"instance\",\"maxIdsPerRelation\":2}"));

        [Theory]
        [InlineData("{\"elementIds\":[1]}")]
        [InlineData("{\"elementIds\":[],\"scopeThreshold\":20}")]
        [InlineData("{\"elementIds\":[\"1\"],\"scopeThreshold\":20}")]
        [InlineData("{\"elementIds\":[1],\"scopeThreshold\":0}")]
        [InlineData("{\"elementIds\":[1],\"scopeThreshold\":20,\"depth\":3}")]
        [InlineData("{\"elementIds\":[1],\"scopeThreshold\":20,\"phaseId\":\"0\"}")]
        [InlineData("{\"elementIds\":[1],\"scopeThreshold\":20,\"maxViews\":101}")]
        [InlineData("{\"elementIds\":[1],\"scopeThreshold\":20,\"maxIdsPerRelation\":201}")]
        [InlineData("{\"elementIds\":[1],\"scopeThreshold\":20,\"proximityPaddingMm\":-1}")]
        [InlineData("{\"elementIds\":[1],\"scopeThreshold\":20,\"output_path\":\"unwanted.json\"}")]
        public void Invalid_or_unbounded_inputs_are_rejected(string json) =>
            Assert.Throws<ArgumentException>(() => SurveyOptions.Parse(JObject.Parse(json)));

        [Fact]
        public void Default_views_are_opt_in_and_phase_zero_is_preserved()
        {
            var o = SurveyOptions.Parse(JObject.Parse("{\"elementIds\":[2,1,2],\"scopeThreshold\":20,\"phaseId\":0}"));
            Assert.Equal(new long[] { 1, 2 }, o.ElementIds);
            Assert.Equal(0L, o.PhaseId);
            Assert.Equal(0, o.MaxViews);
            Assert.Equal(50000, o.MaxScanned);
            Assert.Equal(0, JObject.Parse(SurveyContract.ParametersSchema)["properties"]["maxViews"].Value<int>("default"));
            Assert.Equal(new[] { "elementIds", "scopeThreshold" }, JObject.Parse(SurveyContract.ParametersSchema)["required"].Values<string>());
        }

        [Fact]
        public void Missing_target_and_empty_scope_never_become_false_none()
        {
            var groups = SurveyContract.Kinds.ToDictionary(k => k, k => new SurveyRelation(k, Options()));
            groups["type"].Check("resolved");
            groups["type"].Add(1, 2, "type", 1);
            SurveyContract.FinalizeCoverage(groups, true);
            Assert.Equal(10, groups.Count);
            Assert.All(groups.Values, g => Assert.False(g.Complete));
            Assert.Equal("found", groups["type"].Json().Value<string>("status"));
            Assert.Equal("lower_bound", groups["type"].Json().Value<string>("countKind"));
            Assert.All(groups.Values.Where(g => g.Kind != "type"), g =>
            {
                Assert.Equal("not_checked", g.Json().Value<string>("status"));
                Assert.Equal(JTokenType.Null, g.Json()["count"].Type);
            });
        }

        [Fact]
        public void Empty_complete_and_unknown_are_distinct()
        {
            var r = new SurveyRelation("join", Options());
            Assert.Equal("not_checked", r.Json().Value<string>("status"));
            r.Check("completed");
            Assert.Equal("none", r.Json().Value<string>("status"));
            Assert.Equal(0, r.Json().Value<int>("count"));
            r.Skip("unsupported", "not_implemented");
            Assert.Equal("not_checked", r.Json().Value<string>("status"));
        }

        [Fact]
        public void Counts_ignore_duplicate_edges_self_and_preview_truncation()
        {
            var r = new SurveyRelation("type", Options());
            r.Check("completed");
            r.Add(1, 1, "self", 1); r.Add(1, -1, "invalid", 1);
            r.Add(1, 2, "type", 1); r.Add(1, 2, "type", 1);
            r.Add(1, 3, "type", 1); r.Add(1, 4, "type", 1);
            var j = r.Json();
            Assert.Equal(3, j.Value<int>("count"));
            Assert.Equal("exact", j.Value<string>("countKind"));
            Assert.Equal(2, j["ids"].Count());
            Assert.True(j.Value<bool>("idsTruncated"));
            Assert.True(j.Value<bool>("evidenceTruncated"));
        }

        [Fact]
        public void Graph_exhaustion_preserves_lower_bound_and_distinct_paths()
        {
            var o = Options(); o.MaxNodes = 1;
            var r = new SurveyRelation("host", o);
            r.Check("scan"); r.Add(1, 3, "host", 1); r.Add(2, 3, "host", 1);
            var limit = Assert.Throws<SurveyLimitException>(() => r.Add(1, 4, "host", 1));
            r.Skip("limit", limit.Message);
            Assert.Equal(1, r.Json().Value<int>("count"));
            Assert.Equal(2, r.Json()["evidence"].Count());
            Assert.Equal("lower_bound", r.Json().Value<string>("countKind"));
        }

        [Theory]
        [InlineData(true, false, false, false, true, true, "instance", true)]
        [InlineData(false, true, false, false, true, true, "instance", true)]
        [InlineData(false, false, true, false, true, true, "instance", true)]
        [InlineData(false, false, false, true, true, true, "instance", true)]
        [InlineData(false, false, false, false, false, true, "instance", true)]
        [InlineData(false, false, false, false, true, false, "instance", true)]
        [InlineData(false, false, false, false, true, true, "unknown", true)]
        [InlineData(false, false, false, false, true, true, "instance", false)]
        public void Stop_flags_preserve_uncertainty(bool type, bool group, bool datum, bool missing,
            bool scopeComplete, bool coverageComplete, string kind, bool discussion)
        {
            var o = Options(); o.ChangeKind = kind;
            var flags = SurveyContract.StopFlags(o, 20, scopeComplete, coverageComplete, type, group, datum, missing);
            Assert.Equal(discussion, flags.Value<bool>("requiresDiscussion"));
            Assert.Equal(scopeComplete ? JTokenType.Boolean : JTokenType.Null, flags["overThreshold"].Type);
            Assert.True(SurveyContract.OverThreshold(21, 20, false).Value<bool>());
        }

        [Fact]
        public void Scan_limit_and_byte_cap_do_not_discard_counts_or_checks()
        {
            var o = Options(); o.MaxScanned = 1; o.MaxIds = 200;
            var budget = new SurveyBudget(o); budget.Scan();
            Assert.Equal("scan_budget_exhausted", Assert.Throws<SurveyLimitException>(() => budget.Scan()).Message);
            Assert.Equal(1, budget.Scanned);
            var groups = new JArray();
            foreach (var kind in SurveyContract.Kinds)
            {
                var r = new SurveyRelation(kind, o); r.Check("complete");
                for (var i = 2; i < 202; i++) r.Add(1, i, new string('x', 300), 1);
                groups.Add(r.Json());
            }
            var result = SurveyContract.Bound(new JObject { ["relations"] = groups, ["outputTrimmed"] = false });
            Assert.True(Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)) <= 256 * 1024);
            Assert.True(result.Value<bool>("outputTrimmed"));
            Assert.All(groups, r => { Assert.Equal(200, r.Value<int>("count")); Assert.Equal("exact", r.Value<string>("countKind")); Assert.Single(r["checks"]); });
        }

        [Fact]
        public async Task Shell_forwards_defaults_without_optional_nulls_and_accepts_explicit_view_scope()
        {
            var sent = await Capture.Send(() => QueryTools.SurveyChangeImpact(new long[] { 7 }, 20));
            Assert.Equal("survey_change_impact", sent.Command);
            var j = sent.Json();
            Assert.Equal(0, j.Value<int>("maxViews")); Assert.Null(j["phaseId"]); Assert.Null(j["viewIds"]);
            Assert.Equal(20, j.Value<int>("scopeThreshold")); Assert.Equal(1, j.Value<int>("depth"));
            sent = await Capture.Send(() => QueryTools.SurveyChangeImpact(new long[] { 7 }, 20,
                changeKind: "type", depth: 2, phaseId: 0, viewIds: new long[] { 10 }, maxViews: 1));
            Assert.Equal(0, sent.Json().Value<long>("phaseId"));
            Assert.Equal(10, sent.Json()["viewIds"][0].Value<long>());
            Assert.Equal(1, sent.Json().Value<int>("maxViews"));
        }

        [Fact]
        public async Task Invalid_request_is_rejected_before_Revit_dispatch()
        {
            await Capture.WithSendOverride((c, p, t) => throw new Exception("Should not dispatch"), async () =>
            {
                Assert.Contains("scopeThreshold", await QueryTools.SurveyChangeImpact(new long[] { 1 }, 0));
                Assert.Contains("elementIds", await QueryTools.SurveyChangeImpact(Array.Empty<long>(), 20));
                Assert.Contains("depth", await QueryTools.SurveyChangeImpact(new long[] { 1 }, 20, depth: 3));
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Available_in_default_query_with_send_code_disabled_and_read_only(bool readOnly)
        {
            var config = new RvtMcpConfig { ReadOnly = readOnly, EnableSendCode = false };
            var method = Assert.Single(Program.ResolveRegisteredToolMethods(ToolsetFilter.Resolve(config), config)
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == "revit_survey_change_impact"));
            var attr = method.GetCustomAttribute<McpServerToolAttribute>();
            Assert.True(attr.ReadOnly); Assert.False(attr.Destructive); Assert.True(attr.Idempotent); Assert.False(attr.OpenWorld);
            Assert.Null(ToolReadPolicy.Rejection("survey_change_impact", true, false));
            Assert.Equal(ToolActivityKind.Read, ToolActivityClassifier.Classify("survey_change_impact"));
            Assert.False(ResponseSpillPolicy.Evaluate("survey_change_impact", "{\"output\":\"file\"}", int.MaxValue).ShouldSpill);
            var schema = JObject.Parse(McpServerTool.Create(method, (object)null).ProtocolTool.InputSchema.GetRawText());
            Assert.Contains("scopeThreshold", schema["required"].Values<string>());
            Assert.Equal(0, schema["properties"]["maxViews"].Value<int>("default"));
        }
    }
}
