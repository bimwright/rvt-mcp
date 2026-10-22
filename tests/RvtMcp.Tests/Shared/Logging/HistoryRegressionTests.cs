using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("McpSessionLogConfig")]
    public class HistoryRegressionTests
    {
        [Fact]
        public void Add_RedactsSummaryAsWellAsError()
        {
            const string error = @"cannot open D:\secret\model.rvt";
            var entry = new McpCallEntry
            {
                ToolName = "open_document",
                ErrorMessage = error,
                Summary = SummaryGenerator.Generate("open_document", "{}", null, false, error)
            };

            new McpSessionLog().Add(entry);

            Assert.DoesNotContain("secret", entry.Summary);
            Assert.DoesNotContain("model.rvt", entry.Summary);
            Assert.DoesNotContain("secret", entry.ErrorMessage);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Add_RerunEntryProtectsSummaryAndRestoresSnippetOnlyWhenOptedIn(bool cacheBodies)
        {
            var previous = McpSessionLog.ConfigLoader;
            McpSessionLog.ConfigLoader = () => new RvtMcpConfig { CacheSendCodeBodies = cacheBodies };
            try
            {
                const string code = "return doc.PathName;";
                var parameters = JsonConvert.SerializeObject(new { code });
                var result = JsonConvert.SerializeObject(new { result = @"D:\secret\model.rvt" });
                // HistoryWindow's rerun initializer receives raw wire data and omits CodeSnippet.
                var entry = new McpCallEntry
                {
                    ToolName = "send_code_to_revit", ParamsJson = parameters,
                    ResultJson = result, Success = true, RerunOfIndex = 1,
                    Summary = SummaryGenerator.Generate("send_code_to_revit", parameters, result, true, null)
                };
                new McpSessionLog().Add(entry);

                Assert.DoesNotContain("secret", entry.Summary);
                Assert.DoesNotContain("model.rvt", entry.Summary);
                Assert.DoesNotContain("secret", entry.ResultJson);
                if (cacheBodies)
                {
                    Assert.Equal(code, entry.CodeSnippet);
                    Assert.Equal(parameters, entry.ParamsJson);
                }
                else
                {
                    Assert.Null(entry.CodeSnippet);
                    Assert.Null(JObject.Parse(entry.ParamsJson)["code"]);
                    Assert.Equal(BakeRedactor.HashBody(code), JObject.Parse(entry.ParamsJson).Value<string>("code_hash"));
                }
            }
            finally { McpSessionLog.ConfigLoader = previous; }
        }

        [Fact]
        public void Add_RestoredSnippetIsBoundedButRerunParametersStayComplete()
        {
            var previous = McpSessionLog.ConfigLoader;
            McpSessionLog.ConfigLoader = () => new RvtMcpConfig { CacheSendCodeBodies = true };
            try
            {
                var parameters = JsonConvert.SerializeObject(new { code = new string('x', 140 * 1024) });
                var entry = new McpCallEntry { ToolName = "send_code_to_revit", ParamsJson = parameters };
                new McpSessionLog().Add(entry);
                Assert.NotNull(entry.CodeSnippet);
                Assert.EndsWith("... (truncated)", entry.CodeSnippet);
                Assert.True(entry.CodeSnippet.Length < 129 * 1024);
                Assert.Equal(parameters, entry.ParamsJson);
                Assert.False(entry.ParamsTruncated);
            }
            finally { McpSessionLog.ConfigLoader = previous; }
        }

        [Fact]
        public void NewSession_ClearsLiveAndHistoricalRowsAndRestartsLiveNumbering()
        {
            var log = new McpSessionLog();
            log.Add(new McpCallEntry { ToolName = "live" });
            log.Entries.Add(new McpCallEntry { IsHistorical = true, Index = -1 });
            log.Clear();
            Assert.Empty(log.Entries);
            log.Add(new McpCallEntry { ToolName = "next" });
            Assert.Equal(1, log.Entries.Single().Index);
        }

        [Fact]
        public void RefreshHistory_ReplacesSnapshotAndKeepsOnlyLatest200UniqueExecutions()
        {
            var log = new McpSessionLog();
            var live = new McpCallEntry { CallId = "live", ToolName = "live", Timestamp = DateTime.Now };
            log.Add(live);
            var history = Enumerable.Range(1, 250).Select(i => new McpCallEntry { IsHistorical = true,
                CallId = "old-" + i, Index = -i, Timestamp = DateTime.Now.AddMinutes(-i) }).ToArray();
            log.RefreshHistory(history.Concat(new[] { new McpCallEntry { CallId = "live", IsHistorical = true } }));
            Assert.Equal(200, log.Entries.Count);
            Assert.Contains(live, log.Entries);
            Assert.Equal(1, live.Index);
            var selectedHistory = log.Entries.First(e => e.IsHistorical);
            log.RefreshHistory(history);
            Assert.Equal(200, log.Entries.Count);
            Assert.Contains(selectedHistory, log.Entries);
            Assert.Equal(200, log.Entries.Select(e => e.CallId).Distinct().Count());
            log.RefreshHistory(Array.Empty<McpCallEntry>());
            Assert.Same(live, log.Entries.Single());
        }

        [Fact]
        public void HistoricalRowsArePinnedAndOnlyLiveRowsCountTowardCapAndBadge()
        {
            var log = new McpSessionLog();
            for (int i = 0; i < 3; i++) log.Add(new McpCallEntry { ToolName = "probe" });
            var oldestLive = log.Entries[0];
            // Mirrors HistoryWindow's direct insertion of loader rows.
            var historical = Enumerable.Range(0, 1500)
                .Select(i => new McpCallEntry { IsHistorical = true, Index = i - 1500 }).ToArray();
            for (int i = 0; i < historical.Length; i++) log.Entries.Insert(i, historical[i]);
            Assert.Equal(3, log.Count);

            for (int i = 0; i < 998; i++) log.Add(new McpCallEntry { ToolName = "probe" });

            Assert.Equal(1000, log.Count);
            Assert.Equal(2500, log.Entries.Count);
            Assert.Equal(historical, log.Entries.Where(e => e.IsHistorical).ToArray());
            Assert.DoesNotContain(oldestLive, log.Entries);
            Assert.Equal(1001, log.Entries.Last().Index);

            log.Clear();
            Assert.Equal(0, log.Count);
            Assert.Empty(log.Entries);
            log.Add(new McpCallEntry { ToolName = "probe" });
            Assert.Equal(1, log.Count);
            Assert.Equal(1, log.Entries.Last().Index);
        }
    }
}
