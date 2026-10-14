using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using RvtMcp.Server.Bake;
using Newtonsoft.Json;
using Xunit;

namespace RvtMcp.Tests
{
    public class ClusterEngineTests
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-04-27T00:00:00Z");

        [Fact]
        public void Analyze_below_threshold_returns_no_suggestion()
        {
            var candidates = new ClusterEngine().Analyze(LoadFixture("cluster-a-below-threshold.jsonl"), Now);

            Assert.Empty(candidates);
        }

        [Fact]
        public void Analyze_at_threshold_returns_one_cluster_candidate()
        {
            var candidates = new ClusterEngine().Analyze(LoadFixture("cluster-a-at-threshold.jsonl"), Now);

            var candidate = Assert.Single(candidates);
            Assert.Equal("send_code", candidate.Source);
            Assert.Equal("send_code:collector-walls", candidate.NormalizedKey);
            Assert.Equal(9, candidate.Count);
        }

        [Fact]
        public void Analyze_unstable_parameter_kind_returns_no_suggestion()
        {
            var candidates = new ClusterEngine().Analyze(LoadFixture("cluster-b-unstable-parameter-kind.jsonl"), Now);

            Assert.Empty(candidates);
        }

        [Fact]
        public void Analyze_failed_macro_sequence_rate_returns_no_suggestion()
        {
            var candidates = new ClusterEngine().Analyze(LoadFixture("cluster-c-failed-rate.jsonl"), Now);

            Assert.Empty(candidates);
        }

        private static IReadOnlyList<UsageEvent> LoadFixture(string fileName, [CallerFilePath] string sourceFile = null)
        {
            var fixturePath = Path.Combine(
                Path.GetDirectoryName(sourceFile) ?? ".",
                "..", "..",
                "fixtures",
                "usage-streams",
                fileName);

            return File.ReadLines(fixturePath)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonConvert.DeserializeObject<UsageEvent>(line))
                .ToArray();
        }
    }
}
