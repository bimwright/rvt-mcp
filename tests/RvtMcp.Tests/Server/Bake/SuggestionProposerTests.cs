using System;
using System.Linq;
using RvtMcp.Server.Bake;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests
{
    public class SuggestionProposerTests
    {
        [Fact]
        public void Propose_turns_cluster_candidates_into_open_user_suggestions()
        {
            var proposer = new SuggestionProposer();
            var now = DateTimeOffset.Parse("2026-04-27T00:00:00Z");

            var suggestions = proposer.Propose(new[]
            {
                new ClusterCandidate
                {
                    Source = "preset",
                    NormalizedKey = "preset:create_level:elevation,name",
                    Tool = "create_level",
                    Count = 15,
                    PayloadJson = @"{""event_ids"":[""e1""],""sample"":{""parameter_kinds"":{""elevation"":""number"",""name"":""string""}}}",
                    FirstSeenUtc = now.AddDays(-3),
                    LastSeenUtc = now
                }
            }, Array.Empty<BakeSuggestionRecord>(), now);

            var suggestion = Assert.Single(suggestions);
            Assert.Equal("preset:create_level:elevation,name", suggestion.ClusterKey);
            Assert.Equal("preset", suggestion.Source);
            Assert.Equal("open", suggestion.State);
            Assert.Equal("Create Level", suggestion.Title);
            Assert.Equal(now.ToString("o"), suggestion.CreatedAt);
            Assert.Equal(now.ToString("o"), suggestion.UpdatedAt);
            Assert.True(suggestion.Score > 0);

            var payload = JObject.Parse(suggestion.PayloadJson);
            Assert.Equal("create_level", (string)payload["tool"]);
            Assert.Equal("preset:create_level:elevation,name", (string)payload["normalized_key"]);
            Assert.Equal("mcp_only", payload["output_choices"]![0]!.Value<string>());
            Assert.Equal("ribbon_plus_mcp", payload["output_choices"]![1]!.Value<string>());
        }

        [Fact]
        public void Propose_dedupes_existing_cluster_keys()
        {
            var proposer = new SuggestionProposer();

            var suggestions = proposer.Propose(new[]
            {
                Candidate("preset:create_level:elevation,name", "create_level")
            }, new[]
            {
                new BakeSuggestionRecord
                {
                    Id = "existing",
                    ClusterKey = "preset:create_level:elevation,name",
                    Source = "preset",
                    State = "open",
                    PayloadJson = "{}",
                    VersionHistoryBlob = "[]"
                }
            });

            Assert.Empty(suggestions);
        }

        [Fact]
        public void Propose_dedupes_archived_cluster_keys()
        {
            var proposer = new SuggestionProposer();

            var suggestions = proposer.Propose(new[]
            {
                Candidate("preset:create_level:elevation,name", "create_level")
            }, new[]
            {
                new BakeSuggestionRecord
                {
                    Id = "archived",
                    ClusterKey = "preset:create_level:elevation,name",
                    Source = "preset",
                    State = "archived",
                    PayloadJson = "{}",
                    VersionHistoryBlob = "[]"
                }
            });

            Assert.Empty(suggestions);
        }

        [Fact]
        public void Propose_enforces_naming_cap_across_existing_and_current_candidates()
        {
            var provider = new CountingNameProvider();
            var proposer = new SuggestionProposer(
                envLookup: key => key == "ANTHROPIC_API_KEY" ? "present" : null,
                nameProvider: provider);
            var now = DateTimeOffset.Parse("2026-04-27T12:00:00Z");

            var suggestions = proposer.Propose(new[]
            {
                SendCodeCandidate("send_code:a", "send_code_to_revit"),
                SendCodeCandidate("send_code:b", "send_code_to_revit")
            }, new[]
            {
                new BakeSuggestionRecord
                {
                    Id = "prior",
                    ClusterKey = "send_code:prior",
                    Source = "send_code",
                    State = "open",
                    PayloadJson = @"{""naming_attempts"":[{""attempted_at"":""2026-04-27T01:00:00Z""},{""attempted_at"":""2026-04-27T02:00:00Z""},{""attempted_at"":""2026-04-27T03:00:00Z""},{""attempted_at"":""2026-04-27T04:00:00Z""}]}",
                    VersionHistoryBlob = "[]"
                }
            }, now);

            Assert.Equal(2, suggestions.Count);
            Assert.Equal(1, provider.Calls);
            Assert.Equal("Named Send Code 1", suggestions[0].Title);
            Assert.Equal("Condense Revit Code", suggestions[1].Title);
            Assert.Single((JArray)JObject.Parse(suggestions[0].PayloadJson)["naming_attempts"]!);
            Assert.Null(JObject.Parse(suggestions[1].PayloadJson)["naming_attempts"]);
        }

        [Fact]
        public void Propose_supersedes_highly_similar_accepted_suggestion_and_preserves_history()
        {
            var proposer = new SuggestionProposer();

            var suggestions = proposer.Propose(new[]
            {
                Candidate("preset:create_level:elevation", "create_level")
            }, new[]
            {
                new BakeSuggestionRecord
                {
                    Id = "accepted-1",
                    ClusterKey = "preset:create_level:elevation,name",
                    Source = "preset",
                    Title = "Create Level",
                    State = "accepted",
                    PayloadJson = @"{""tool"":""create_level"",""normalized_key"":""preset:create_level:elevation,name""}",
                    VersionHistoryBlob = "[]"
                }
            }, DateTimeOffset.Parse("2026-04-27T00:00:00Z"));

            var suggestion = Assert.Single(suggestions);
            Assert.Equal("superseded", suggestion.State);

            var payload = JObject.Parse(suggestion.PayloadJson);
            Assert.Equal("accepted-1", (string)payload["superseded_by_suggestion_id"]);

            var history = JArray.Parse(suggestion.VersionHistoryBlob);
            Assert.Equal("superseded_by_existing_accepted", (string)history[0]!["event"]);
            Assert.Equal("accepted-1", (string)history[0]!["accepted_suggestion_id"]);
        }

        private static ClusterCandidate Candidate(string key, string tool)
        {
            return new ClusterCandidate
            {
                Source = "preset",
                NormalizedKey = key,
                Tool = tool,
                Count = 15,
                PayloadJson = @"{""sample"":{""parameter_kinds"":{""elevation"":""number""}}}",
                FirstSeenUtc = DateTimeOffset.Parse("2026-04-20T00:00:00Z"),
                LastSeenUtc = DateTimeOffset.Parse("2026-04-27T00:00:00Z")
            };
        }

        private static ClusterCandidate SendCodeCandidate(string key, string tool)
        {
            return new ClusterCandidate
            {
                Source = "send_code",
                NormalizedKey = key,
                Tool = tool,
                Count = 9,
                PayloadJson = @"{""sample"":{}}",
                FirstSeenUtc = DateTimeOffset.Parse("2026-04-20T00:00:00Z"),
                LastSeenUtc = DateTimeOffset.Parse("2026-04-27T00:00:00Z")
            };
        }

        private sealed class CountingNameProvider : ISuggestionNameProvider
        {
            public int Calls { get; private set; }

            public string SuggestName(ClusterCandidate candidate)
            {
                Calls++;
                return "Named Send Code " + Calls;
            }
        }
    }
}
