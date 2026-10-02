// Bounded, API-free survey contract shared by the gateway, plugin and tests.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Survey
{
    public sealed class SurveyOptions
    {
        public long[] ElementIds;
        public int ScopeThreshold, Depth, MaxIds, BudgetMs, MaxViews, MaxScanned, MaxNodes;
        public string ChangeKind, ViewScope;
        public long? PhaseId;
        public long[] ViewIds;
        public double PaddingMm;
        public static SurveyOptions Parse(JObject j)
        {
            var allowed = new HashSet<string>(new[] { "elementIds", "scopeThreshold", "changeKind", "depth", "viewScope", "phaseId", "viewIds", "proximityPaddingMm", "maxIdsPerRelation", "budgetMs", "maxViews", "maxScannedElements", "maxGraphNodes" });
            if (j == null || j.Properties().Any(p => !allowed.Contains(p.Name)))
                throw new ArgumentException("Invalid survey input fields.");
            var ids = Ids(j, "elementIds", true, 25);
            if (j["scopeThreshold"] == null)
                throw new ArgumentException("scopeThreshold is required; there is no public default.");
            var o = new SurveyOptions
            {
                ElementIds = ids,
                ScopeThreshold = Int(j, "scopeThreshold", 1, 1, 200000),
                Depth = Int(j, "depth", 1, 1, 2),
                MaxIds = Int(j, "maxIdsPerRelation", 100, 1, 200),
                BudgetMs = Int(j, "budgetMs", 10000, 1, 30000),
                MaxViews = Int(j, "maxViews", 0, 0, 100),
                MaxScanned = Int(j, "maxScannedElements", 50000, 1, 200000),
                MaxNodes = Int(j, "maxGraphNodes", 10000, 1, 20000),
                ChangeKind = Choice(j, "changeKind", "unknown", new[] { "instance", "type", "unknown" }),
                ViewScope = Choice(j, "viewScope", "sheets", new[] { "sheets", "all" }),
                ViewIds = Ids(j, "viewIds", false, 100)
            };
            if (j["phaseId"] != null && j["phaseId"].Type != JTokenType.Null)
            {
                if (j["phaseId"].Type != JTokenType.Integer)
                    throw new ArgumentException("phaseId must be an integer.");
                o.PhaseId = j.Value<long>("phaseId");
            }

            var padding = j["proximityPaddingMm"];
            if (padding != null && padding.Type != JTokenType.Float && padding.Type != JTokenType.Integer)
                throw new ArgumentException("proximityPaddingMm must be numeric.");
            o.PaddingMm = padding == null ? 100 : padding.Value<double>();
            if (double.IsNaN(o.PaddingMm) || double.IsInfinity(o.PaddingMm) || o.PaddingMm < 0 || o.PaddingMm > 10000)
                throw new ArgumentException("proximityPaddingMm must be finite and in 0..10000.");
            return o;
        }

        static int Int(JObject j, string k, int fallback, int min, int max)
        {
            var t = j[k];
            if (t == null)
                return fallback;
            if (t.Type != JTokenType.Integer)
                throw new ArgumentException(k + " must be an integer.");
            var v = t.Value<long>();
            if (v < min || v > max)
                throw new ArgumentException(k + " is outside its supported range.");
            return (int)v;
        }

        static string Choice(JObject j, string k, string fallback, string[] choices)
        {
            var t = j[k];
            if (t == null)
                return fallback;
            if (t.Type != JTokenType.String || !choices.Contains(t.Value<string>()))
                throw new ArgumentException("Invalid " + k + ".");
            return t.Value<string>();
        }

        static long[] Ids(JObject j, string k, bool required, int max)
        {
            var t = j[k];
            if (t == null && !required)
                return new long[0];
            var a = t as JArray;
            if (a == null || a.Count > max || required && a.Count == 0 || a.Any(x => x.Type != JTokenType.Integer))
                throw new ArgumentException(k + " must be a bounded integer array.");
            return a.Select(x => x.Value<long>()).Distinct().OrderBy(x => x).ToArray();
        }
    }

    public sealed class SurveyLimitException : Exception
    {
        public SurveyLimitException(string reason) : base(reason)
        {
        }
    }

    public sealed class SurveyBudget
    {
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly SurveyOptions options;
        public int Scanned { get; private set; }
        public long ElapsedMs => clock.ElapsedMilliseconds;

        public SurveyBudget(SurveyOptions o)
        {
            options = o;
        }

        public void Check()
        {
            if (clock.ElapsedMilliseconds >= options.BudgetMs)
                throw new SurveyLimitException("time_budget_exhausted");
        }

        public void Scan()
        {
            Check();
            if (Scanned >= options.MaxScanned)
                throw new SurveyLimitException("scan_budget_exhausted");
            Scanned++;
        }
    }

    public sealed class SurveyCheck
    {
        public string Name, Reason;
        public long? TargetId;
        public bool Complete;
        public long ElapsedMs;
        public int ScannedElements;
        public void Partial(string reason)
        {
            Complete = false;
            Reason = reason;
        }

        public JObject Json() => new JObject
        {
            ["name"] = Name,
            ["targetId"] = TargetId,
            ["complete"] = Complete,
            ["reason"] = Reason,
            ["elapsedMs"] = ElapsedMs,
            ["scannedElements"] = ScannedElements
        };
    }

    public sealed class SurveyRelation
    {
        public readonly string Kind;
        readonly int maxIds, maxNodes;
        public readonly HashSet<long> Ids = new HashSet<long>();
        readonly List<JObject> evidence = new List<JObject>();
        readonly HashSet<string> evidenceKeys = new HashSet<string>();
        bool evidenceTruncated;
        public readonly List<SurveyCheck> Checks = new List<SurveyCheck>();
        public SurveyRelation(string kind, SurveyOptions o)
        {
            Kind = kind;
            maxIds = o.MaxIds;
            maxNodes = o.MaxNodes;
        }

        public bool Complete => Checks.Count > 0 && Checks.All(c => c.Complete);

        public void Add(long target, long related, string via, int depth)
        {
            if (related == -1 || related == target)
                return;
            if (!Ids.Contains(related) && Ids.Count >= maxNodes)
                throw new SurveyLimitException("graph_node_budget_exhausted");
            Ids.Add(related);
            var key = target + "/" + related + "/" + via + "/" + depth;
            if (evidenceKeys.Contains(key))
                return;
            if (evidence.Count < maxIds)
            {
                evidenceKeys.Add(key);
                evidence.Add(new JObject { ["targetId"] = target, ["relatedId"] = related, ["via"] = via, ["depth"] = depth });
            }
            else
                evidenceTruncated = true;
        }

        public SurveyCheck Check(string name, long? target = null)
        {
            var c = new SurveyCheck
            {
                Name = name,
                TargetId = target,
                Complete = true
            };
            Checks.Add(c);
            return c;
        }

        public void Skip(string name, string reason, long? target = null)
        {
            Check(name, target).Partial(reason);
        }

        public JObject Json() => new JObject
        {
            ["kind"] = Kind,
            ["status"] = Ids.Count > 0 ? "found" : Complete ? "none" : "not_checked",
            ["coverage"] = Complete ? "complete" : "partial",
            ["count"] = Ids.Count > 0 || Complete ? (JToken)new JValue(Ids.Count) : JValue.CreateNull(),
            ["countKind"] = Complete ? "exact" : Ids.Count > 0 ? "lower_bound" : "unknown",
            ["ids"] = new JArray(Ids.OrderBy(x => x).Take(maxIds)),
            ["idsTruncated"] = Ids.Count > maxIds,
            ["evidence"] = new JArray(evidence),
            ["evidenceTruncated"] = evidenceTruncated,
            ["checks"] = new JArray(Checks.Select(c => c.Json()))
        };
    }

    public static class SurveyContract
    {
        public static void FinalizeCoverage(IDictionary<string, SurveyRelation> relations, bool missingTargets)
        {
            foreach (var relation in relations.Values)
            {
                if (missingTargets)
                    relation.Skip("target_resolution", "one_or_more_targets_unresolved");
                if (relation.Checks.Count == 0)
                    relation.Skip("targets", "no_resolved_targets");
            }
        }

        public static JObject StopFlags(SurveyOptions options, int count, bool scopeComplete, bool coverageComplete, bool touchesType, bool touchesGroup, bool touchesDatum, bool missingTargets)
        {
            var over = OverThreshold(count, options.ScopeThreshold, scopeComplete);
            return new JObject
            {
                ["touchesType"] = touchesType,
                ["touchesGroup"] = touchesGroup,
                ["touchesLevelOrGrid"] = touchesDatum,
                ["overThreshold"] = over,
                ["requiresDiscussion"] = touchesType || touchesGroup || touchesDatum || missingTargets || over.Type == JTokenType.Null || over.Value<bool>() || options.ChangeKind == "unknown" || !coverageComplete,
                ["unknownChangeKind"] = options.ChangeKind == "unknown",
                ["missingTargets"] = missingTargets,
                ["incompleteCoverage"] = !coverageComplete
            };
        }

        public const string ParametersSchema = @"{""type"":""object"",""additionalProperties"":false,""properties"":{""elementIds"":{""type"":""array"",""items"":{""type"":""integer""},""minItems"":1,""maxItems"":25},""scopeThreshold"":{""type"":""integer"",""minimum"":1,""maximum"":200000},""changeKind"":{""type"":""string"",""enum"":[""instance"",""type"",""unknown""],""default"":""unknown""},""depth"":{""type"":""integer"",""enum"":[1,2],""default"":1},""viewScope"":{""type"":""string"",""enum"":[""sheets"",""all""],""default"":""sheets""},""phaseId"":{""type"":""integer""},""viewIds"":{""type"":""array"",""items"":{""type"":""integer""},""maxItems"":100},""proximityPaddingMm"":{""type"":""number"",""default"":100,""minimum"":0,""maximum"":10000},""maxIdsPerRelation"":{""type"":""integer"",""default"":100,""minimum"":1,""maximum"":200},""budgetMs"":{""type"":""integer"",""default"":10000,""minimum"":1,""maximum"":30000},""maxViews"":{""type"":""integer"",""default"":0,""minimum"":0,""maximum"":100},""maxScannedElements"":{""type"":""integer"",""default"":50000,""minimum"":1,""maximum"":200000},""maxGraphNodes"":{""type"":""integer"",""default"":10000,""minimum"":1,""maximum"":20000}},""required"":[""elementIds"",""scopeThreshold""]}";
        public static readonly string[] Kinds =
        {
            "type",
            "host",
            "join",
            "connector",
            "group",
            "spatial",
            "annotations",
            "presentation",
            "datum",
            "proximity"
        };
        public static JToken OverThreshold(int count, int threshold, bool complete) => count > threshold ? new JValue(true) : complete ? new JValue(false) : JValue.CreateNull();
        public static JObject Bound(JObject report)
        {
            // Trim evidence previews, never core results/counts/checks, before the transport guard.
            const int maxBytes = 256 * 1024;
            while (System.Text.Encoding.UTF8.GetByteCount(report.ToString(Newtonsoft.Json.Formatting.None)) > maxBytes)
            {
                var arrays = report["relations"].SelectMany(r => new[] { r["evidence"] as JArray, r["ids"] as JArray }).Where(a => a != null && a.Count > 0).OrderByDescending(a => a.Count).ToList();
                if (arrays.Count == 0)
                    throw new InvalidOperationException("Survey fixed metadata exceeds the response budget.");
                var array = arrays[0];
                var owner = (JObject)array.Parent.Parent;
                if (((JProperty)array.Parent).Name == "ids")
                    owner["idsTruncated"] = true;
                else
                    owner["evidenceTruncated"] = true;
                var keep = array.Count / 2;
                while (array.Count > keep)
                    array.RemoveAt(array.Count - 1);
                report["outputTrimmed"] = true;
            }

            return report;
        }
    }
}
