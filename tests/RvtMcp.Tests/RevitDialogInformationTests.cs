using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RvtMcp.Failures;
using RvtMcp.Plugin;
using RvtMcp.Plugin.Handlers;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    // Issue 16, phase A: Revit dialog information for the agent. Every catalogue entry here is made up for the
    // test; no Revit text is copied into the repository.
    [Collection("ServerStateConfig")]
    public sealed class RevitDialogInformationTests : IDisposable
    {
        private const string FailureGuid = "11111111-2222-3333-4444-555555555555";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rvt-dialog-info-" + Guid.NewGuid().ToString("N"));
        private readonly string _oldRoot = RevitMessageLookup.RootOverride;
        private readonly Func<int?> _oldYear = FailureTools.YearProvider;

        public RevitDialogInformationTests()
        {
            RevitMessageLookup.RootOverride = _root;
        }

        public void Dispose()
        {
            RevitMessageLookup.RootOverride = _oldRoot;
            FailureTools.YearProvider = _oldYear;
            try { Directory.Delete(_root, true); } catch { }
        }

        private static RevitMessageCatalogData Sample() => new RevitMessageCatalogData
        {
            RevitYear = 2099, Build = "test-build", Language = "Test",
            TaskDialogs =
            {
                new TaskDialogEntry
                {
                    Id = "TaskDialog_Test_Sample", Title = "Sample title", MainInstruction = "Sample instruction about %Item%.",
                    CommonButtons = "Close", DefaultButton = "1001",
                    Buttons = { new CatalogButton { Id = "1001", Text = "First choice" }, new CatalogButton { Id = "1002", Text = "Second choice" } }
                }
            },
            Failures =
            {
                new FailureEntry { Guid = FailureGuid, Name = "TestFailures.SampleWarning", Severity = "Warning", Description = "Sample warning.",
                    DefaultResolution = "DeleteElements", Resolutions = { new FailureResolutionEntry { Type = "DeleteElements", Caption = "Sample fix" } } }
            }
        };

        [Fact]
        public void Lookup_finds_dialogs_by_code_or_short_code_and_failures_by_guid_or_name()
        {
            var data = Sample();
            var dialog = RevitMessageLookup.Lookup(data, "TaskDialog_Test_Sample", 2099);
            Assert.True(dialog.Value<bool>("found"));
            Assert.Equal("TaskDialog", dialog["kind"]);
            Assert.Equal("Sample title", dialog["entry"]["title"]);
            Assert.Equal("1002", dialog["entry"]["buttons"][1]["id"]);
            Assert.True(RevitMessageLookup.Lookup(data, "test_sample", 2099).Value<bool>("found"));

            var failure = RevitMessageLookup.Lookup(data, FailureGuid.ToUpperInvariant(), 2099);
            Assert.Equal("Failure", failure["kind"]);
            Assert.Equal("TestFailures.SampleWarning", failure["entry"]["name"]);
            Assert.Equal("Sample fix", failure["entry"]["resolutions"][0]["caption"]);
            Assert.True(RevitMessageLookup.Lookup(data, "TestFailures.SampleWarning", 2099).Value<bool>("found"));

            var missing = RevitMessageLookup.Lookup(data, "0", 2099);
            Assert.False(missing.Value<bool>("found"));
            Assert.Contains("not in the catalogue", missing.Value<string>("note"));
            Assert.False(RevitMessageLookup.Lookup(null, "TaskDialog_Test_Sample", 2099).Value<bool>("available"));
        }

        [Fact]
        public void Dialog_info_carries_live_text_and_catalogue_buttons_and_masks_paths()
        {
            var entry = Sample().TaskDialogs[0];
            var info = RevitMessageLookup.DialogInfo("TaskDialog_Test_Sample", "TaskDialog", @"Sample instruction about C:\Projects\Secret\Model.rvt.", entry);
            Assert.Equal("TaskDialog_Test_Sample", info["code"]);
            Assert.True(info.Value<bool>("inCatalog"));
            Assert.Equal("Sample title", info["title"]);
            Assert.Equal("Second choice", info["buttons"][1]["text"]);
            Assert.Equal("Close", info["commonButtons"]);
            Assert.Equal("1001", info["defaultButton"]);
            Assert.DoesNotContain("Secret", info.Value<string>("text"));
            Assert.Contains("[local path]", info.Value<string>("text"));

            var unknown = RevitMessageLookup.DialogInfo("", "Dialog", null, null);
            Assert.False(unknown.Value<bool>("inCatalog"));
            Assert.Null(unknown["title"]);
            Assert.Null(unknown["text"]);
            Assert.True(RevitMessageLookup.DialogInfo("x", "TaskDialog", new string('a', 5000), null).Value<string>("text").Length <= RevitMessageLookup.MaxDialogText);
        }

        [Fact]
        public void Catalogue_cache_round_trips_and_rejects_other_schema_versions()
        {
            var path = RevitMessageLookup.CachePath(2099);
            Assert.StartsWith(_root, path);
            RevitMessageLookup.Write(path, Sample());
            RevitMessageLookup.Write(path, Sample()); // Replaces an existing file.
            var read = RevitMessageLookup.Read(path);
            Assert.Equal("test-build", read.Build);
            Assert.Equal("Sample title", RevitMessageLookup.FindDialog(read, "TaskDialog_Test_Sample").Title);
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)));

            File.WriteAllText(path, "{\"schemaVersion\":99}");
            Assert.Null(RevitMessageLookup.Read(path));
            File.WriteAllText(path, "not json");
            Assert.Null(RevitMessageLookup.Read(path));
        }

        [Fact]
        public void Lookup_tool_reads_the_local_catalogue_of_the_bound_year_without_dispatching()
        {
            var send = ToolGateway.SendOverride;
            try
            {
                ToolGateway.SendOverride = (_, __, ___) => throw new Exception("The lookup must not dispatch Revit.");
                FailureTools.YearProvider = () => null;
                Assert.Equal("TARGET_NOT_BOUND", JObject.Parse(FailureTools.LookupMessage("TaskDialog_Test_Sample"))["error"]);

                FailureTools.YearProvider = () => 2099;
                Assert.False(JObject.Parse(FailureTools.LookupMessage("TaskDialog_Test_Sample")).Value<bool>("available"));
                Assert.Equal("INVALID_ARGUMENT", JObject.Parse(FailureTools.LookupMessage(" "))["error"]);

                RevitMessageLookup.Write(RevitMessageLookup.CachePath(2099), Sample());
                var found = JObject.Parse(FailureTools.LookupMessage(FailureGuid));
                Assert.True(found.Value<bool>("found"));
                Assert.Equal(2099, found.Value<int>("revitYear"));
                Assert.Equal("Sample warning.", found["entry"]["description"]);
            }
            finally { ToolGateway.SendOverride = send; }
        }

        [Fact]
        public void Waiting_receipt_carries_the_dialog_and_a_mechanism_only_next_step()
        {
            var execution = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var lifecycle = PendingRequestLifecycle.For(execution);
            string emitted = null;
            lifecycle.Configure("dialog-1", Guid.NewGuid().ToString("N"), frame => emitted = frame);
            try
            {
                Assert.True(lifecycle.TryStart());
                var dialog = RevitMessageLookup.DialogInfo("TaskDialog_Test_Sample", "TaskDialog", "Live text", Sample().TaskDialogs[0]);
                lifecycle.EmitDialogWait(new JObject { ["dialogId"] = "TaskDialog_Test_Sample", ["manual"] = true, ["dialog"] = dialog });
                var frame = JObject.Parse(emitted);
                Assert.Equal("TaskDialog_Test_Sample", frame["_operation"]["dialog"]["code"]);
                Assert.Equal(FailureCapture.WaitingNextStep, frame["_operation"]["nextStep"]);
                Assert.DoesNotContain("click", FailureCapture.WaitingNextStep, StringComparison.OrdinalIgnoreCase);

                var pending = new GatewayPendingRequest(frame.Value<string>("operationId"), 1);
                Assert.False(pending.Route(frame));
                Assert.Equal("Live text", JObject.Parse(pending.Caller.Task.Result)["_operation"]["dialog"]["text"]);
                var blockedBy = pending.BlockedBy();
                Assert.Equal(frame.Value<string>("operationId"), blockedBy["operationId"]);
                Assert.Equal("TaskDialog_Test_Sample", blockedBy["dialog"]["code"]);
            }
            finally { execution.TrySetResult("{}"); }
        }

        [Fact]
        public void Dialog_text_is_shortened_before_the_operation_receipt_loses_it()
        {
            var operation = new JObject
            {
                ["operationId"] = "op-1", ["execution"] = "waiting_for_user", ["outcome"] = "unknown",
                ["dialog"] = new JObject { ["code"] = "TaskDialog_Test_Sample", ["kind"] = "TaskDialog", ["text"] = new string('\u00e9', 2000),
                    ["buttons"] = new JArray(Enumerable.Range(0, 8).Select(i => new JObject { ["id"] = i.ToString(), ["text"] = new string('\u00e9', 120) })) }
            };
            var bounded = SafetyResponseMetadata.Bound(operation, 4096);
            Assert.Equal("TaskDialog_Test_Sample", bounded["dialog"]["code"]);
            Assert.Null(bounded["dialog"]["buttons"]);
            Assert.EndsWith("[truncated]", bounded["dialog"].Value<string>("text"));
            Assert.Null(bounded["summaryTruncated"]);
        }

        [Fact]
        public void User_dialog_reports_pass_through_receipts_but_not_compact_ones()
        {
            var source = new JObject { ["success"] = true, ["_userDialogs"] = new JObject { ["model"] = "Sample", ["items"] = new JArray() } };
            var target = new JObject();
            SafetyResponseMetadata.Attach(target, source);
            Assert.Equal("Sample", target["_userDialogs"]["model"]);
            var compact = new JObject();
            SafetyResponseMetadata.Attach(compact, source, compact: true);
            Assert.Null(compact["_userDialogs"]);
        }

        [Fact]
        public void User_dialog_buffer_counts_repeats_reports_once_and_bounds_each_model()
        {
            var buffer = new UserDialogBuffer();
            var key = UserDialogBuffer.Key(@"C:\Models\Sample.rvt", "Sample");
            Assert.Equal(key, UserDialogBuffer.Key(@"c:\models\sample.rvt ", null));
            Assert.Null(UserDialogBuffer.Key("", " "));
            var now = DateTimeOffset.UtcNow;
            buffer.Add(key, "Sample", new JObject { ["code"] = "TaskDialog_Test_Sample", ["kind"] = "TaskDialog", ["text"] = "Same" }, now);
            buffer.Add(key, "Sample", new JObject { ["code"] = "TaskDialog_Test_Sample", ["kind"] = "TaskDialog", ["text"] = "Same" }, now);
            buffer.Add(key, "Sample", new JObject { ["code"] = FailureGuid, ["kind"] = "failure", ["severity"] = "Warning", ["text"] = new string('w', 1000) }, now);
            Assert.True(buffer.HasItems(key));

            var report = buffer.Take(key);
            Assert.Equal("Sample", report["model"]);
            Assert.Equal(UserDialogBuffer.Note, report["note"]);
            Assert.Equal(2, report["items"][0].Value<int>("count"));
            Assert.True(report["items"][1].Value<string>("text").Length <= UserDialogBuffer.MaxText);
            Assert.Null(buffer.Take(key));
            Assert.False(buffer.HasItems(key));

            for (int i = 0; i < UserDialogBuffer.MaxItems + 5; i++)
                buffer.Add(key, "Sample", new JObject { ["code"] = "c" + i, ["kind"] = "TaskDialog", ["text"] = new string('t', 300) }, now);
            var first = buffer.Take(key);
            Assert.Equal(5, first.Value<int>("omittedItems"));
            Assert.True(first.Value<int>("moreItems") > 0); // What does not fit stays for the next call.
            var second = buffer.Take(key);
            Assert.Equal(UserDialogBuffer.MaxItems, ((JArray)first["items"]).Count + ((JArray)second["items"]).Count);
        }

        [Fact]
        public void User_dialogs_follow_a_model_that_opens_under_another_path()
        {
            var buffer = new UserDialogBuffer();
            var central = UserDialogBuffer.Key(@"\\server\central.rvt", null);
            var local = UserDialogBuffer.Key(@"C:\Local\central_user.rvt", "central_user");
            buffer.Add(central, "central", new JObject { ["code"] = "TaskDialog_Test_Sample", ["kind"] = "TaskDialog" }, DateTimeOffset.UtcNow);
            buffer.Rekey(central, local, "central_user");
            Assert.False(buffer.HasItems(central));
            Assert.Equal("central_user", buffer.Take(local)["model"]);
        }

        [Fact]
        public void User_dialog_buffer_keeps_at_most_fifty_models()
        {
            var buffer = new UserDialogBuffer();
            for (int i = 0; i <= UserDialogBuffer.MaxModels; i++)
                buffer.Add("title:m" + i, "m" + i, new JObject { ["code"] = "c", ["kind"] = "TaskDialog" }, DateTimeOffset.UtcNow);
            Assert.False(buffer.HasItems("title:m0"));
            Assert.True(buffer.HasItems("title:m" + UserDialogBuffer.MaxModels));
        }

        [Fact]
        public void Previous_operations_attach_once_at_most_three_and_skip_ones_read_or_lost()
        {
            var tracker = new PreviousOperationTracker();
            var done = new List<TaskCompletionSource<string>>();
            for (int i = 0; i < 4; i++)
            {
                var execution = new TaskCompletionSource<string>();
                tracker.Remember("op" + i, "revit_open_model", execution.Task);
                done.Add(execution);
            }
            var running = new TaskCompletionSource<string>();
            tracker.Remember("running", "revit_open_model", running.Task);
            var lost = new TaskCompletionSource<string>();
            tracker.Remember("lost", "revit_open_model", lost.Task);
            lost.SetCanceled();

            var empty = new JObject();
            tracker.Attach(empty);
            Assert.Null(empty["_previousOperation"]);

            foreach (var execution in done)
                execution.SetResult(new JObject
                {
                    ["success"] = false, ["error"] = "REVIT_FAILURE_ROLLED_BACK",
                    ["_operation"] = new JObject { ["outcome"] = "rolled_back", ["mutationApplied"] = false },
                    ["_failure"] = new JObject { ["action"] = "rolled_back", ["gate"] = "open", ["messageCount"] = 1,
                        ["groups"] = new JArray(new JObject { ["failureDefinitionId"] = "TaskDialog_Test_Sample", ["severity"] = "TaskDialog",
                            ["count"] = 1, ["answeredBy"] = "user", ["description"] = new string('d', 500) }) }
                }.ToString());
            tracker.MarkRead("op3");

            var response = new JObject();
            tracker.Attach(response);
            var entries = (JArray)response["_previousOperation"];
            Assert.Equal(3, entries.Count);
            Assert.Equal("op0", entries[0]["operationId"]);
            Assert.Equal("revit_open_model", entries[0]["tool"]);
            Assert.Equal("rolled_back", entries[0]["outcome"]);
            Assert.Equal("user", entries[0]["_failure"]["groups"][0]["answeredBy"]);
            Assert.True(entries[0]["_failure"]["groups"][0].Value<string>("description").Length <= 200);

            var next = new JObject();
            tracker.Attach(next);
            Assert.Null(next["_previousOperation"]); // op3 was read; "running" has not finished; "lost" has no result.
            running.SetResult("{\"success\":true}");
            tracker.Attach(next);
            Assert.Equal("running", Assert.Single((JArray)next["_previousOperation"])["operationId"]);
        }

        [Theory]
        [InlineData("2022", "2024", 2022)]
        [InlineData("Autodesk Revit 2019", "2027", 2019)]
        [InlineData("2024", "2024", null)]
        [InlineData("2027", "2024", null)]
        [InlineData(null, "2024", null)]
        [InlineData("unknown", "2024", null)]
        [InlineData("20221", "2024", null)]
        public void Upgraded_from_names_only_an_older_saved_year(string saved, string running, int? expected)
        {
            Assert.Equal(expected, OpenModelLocalPolicy.UpgradedFrom(saved, running));
        }
    }
}
