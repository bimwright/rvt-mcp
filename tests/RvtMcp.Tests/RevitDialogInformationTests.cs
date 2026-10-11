using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
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
            buffer.Add(key, "Sample", new JObject { ["code"] = "TaskDialog_Test_Sample", ["kind"] = "TaskDialog", ["text"] = "Same", ["title"] = "Test",
                ["buttons"] = new JArray(new JObject { ["id"] = "1001", ["text"] = "First choice" }), ["commonButtons"] = "Close", ["defaultButton"] = "1001" }, now);
            buffer.Add(key, "Sample", new JObject { ["code"] = "TaskDialog_Test_Sample", ["kind"] = "TaskDialog", ["text"] = "Same" }, now);
            buffer.Add(key, "Sample", new JObject { ["code"] = FailureGuid, ["kind"] = "failure", ["severity"] = "Warning", ["text"] = new string('w', 1000) }, now);
            Assert.True(buffer.HasItems(key));

            var report = buffer.Take(key);
            Assert.Equal("Sample", report["model"]);
            Assert.Equal(UserDialogBuffer.Note, report["note"]);
            Assert.Equal(2, report["items"][0].Value<int>("count"));
            // A closed dialog keeps what it said, not the choices it offered.
            Assert.Equal("Test", report["items"][0]["title"]);
            Assert.Null(report["items"][0]["buttons"]);
            Assert.Null(report["items"][0]["commonButtons"]);
            Assert.Null(report["items"][0]["defaultButton"]);
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
        public void User_failures_are_read_up_to_the_limit_and_the_rest_reported_as_omitted()
        {
            var buffer = new UserDialogBuffer();
            var now = DateTimeOffset.UtcNow;
            JObject Failure(string code) => new JObject { ["kind"] = "failure", ["code"] = code, ["severity"] = "Warning", ["text"] = "t" + code };

            // Many repeats of one warning do not hide a different warning that follows them.
            var repeated = Enumerable.Repeat("same", 30).Append("other").ToList();
            buffer.AddFailures("title:m", "m", repeated, Failure, now);
            var report = buffer.Take("title:m");
            Assert.Equal(30, report["items"][0].Value<int>("count"));
            Assert.Equal("other", report["items"][1].Value<string>("code"));
            Assert.Null(report["omittedItems"]);

            // Past the read limit, failures are counted without being read.
            int reads = 0;
            var many = Enumerable.Repeat("same", UserDialogBuffer.MaxFailuresRead + 5).ToList();
            buffer.AddFailures("title:m", "m", many, code => { reads++; return Failure(code); }, now);
            report = buffer.Take("title:m");
            Assert.Equal(UserDialogBuffer.MaxFailuresRead, reads);
            Assert.Equal(5, report.Value<int>("omittedItems"));

            // Distinct failures past the buffer bound are counted too.
            buffer.AddFailures("title:m", "m", Enumerable.Range(0, UserDialogBuffer.MaxItems + 5).Select(i => "c" + i).ToList(), Failure, now);
            report = buffer.Take("title:m");
            Assert.Equal(5, report.Value<int>("omittedItems"));
        }

        [Fact]
        public void User_dialog_buffer_skips_dialogs_with_no_id_and_no_text()
        {
            var buffer = new UserDialogBuffer();
            buffer.Add("title:m", "m", new JObject { ["code"] = "0", ["kind"] = "Dialog", ["inCatalog"] = false }, DateTimeOffset.UtcNow);
            buffer.Add("title:m", "m", new JObject { ["code"] = "", ["kind"] = "Dialog", ["text"] = " " }, DateTimeOffset.UtcNow);
            Assert.False(buffer.HasItems("title:m"));
            buffer.Add("title:m", "m", new JObject { ["code"] = "0", ["kind"] = "MessageBox", ["text"] = "Shown by another add-in" }, DateTimeOffset.UtcNow);
            Assert.True(buffer.HasItems("title:m"));
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

        private static RvtMcpConfig SmallLimits => new RvtMcpConfig
        {
            ResponseWarnBytes = 2048, ResponseStrongWarnBytes = 4096, ResponseBudgetBytes = 8192, MaxResponseBytes = 32768
        };

        private static PreviousOperationTracker Finished(string operationId, JObject final)
        {
            var tracker = new PreviousOperationTracker();
            var execution = new TaskCompletionSource<string>();
            execution.SetResult(final.ToString());
            tracker.Remember(operationId, "revit_open_model", execution.Task);
            return tracker;
        }

        // What the agent receives: the tool result after the call filter.
        private static CallToolResult Delivered(string tool, string text, PreviousOperationTracker tracker,
            RvtMcpConfig config = null, ResponseSpillWriter writer = null) =>
            RuntimeToolFilter.Apply(new CallToolRequestParams { Name = tool },
                new CallToolResult { Content = new[] { new TextContentBlock { Text = text } } }, config ?? new RvtMcpConfig(),
                writer: writer, previousOperations: tracker);

        private static JObject Json(CallToolResult result) => JObject.Parse(((TextContentBlock)result.Content[0]).Text);

        [Fact]
        public void Finished_operation_reaches_the_next_structured_response_of_any_tool_once()
        {
            var tracker = Finished("op1", new JObject { ["success"] = true, ["data"] = new JObject { ["opened"] = true } });
            // A plain-text error carries nothing and does not use up the delivery.
            Assert.DoesNotContain("_previousOperation", ((TextContentBlock)Delivered("revit_open_model", "Error: sample", tracker).Content[0]).Text);
            // A tool that never calls Revit carries it too.
            var lookup = Json(Delivered("revit_lookup_message", "{\"code\":\"TaskDialog_Test_Sample\",\"found\":false}", tracker));
            Assert.Equal("op1", Assert.Single((JArray)lookup["_previousOperation"])["operationId"]);
            Assert.Null(Json(Delivered("revit_get_current_target", "{\"target\":\"auto\"}", tracker))["_previousOperation"]);

            // An operation that finished before its own pending receipt left the server is carried by that receipt, once.
            tracker = Finished("op2", new JObject { ["success"] = true });
            var receipt = new JObject { ["success"] = false, ["error"] = "REVIT_OPERATION_REQUIRES_USER",
                ["_operation"] = new JObject { ["operationId"] = "op2", ["execution"] = "waiting_for_user", ["complete"] = false } };
            Assert.Equal("op2", Assert.Single((JArray)Json(Delivered("revit_open_model", receipt.ToString(), tracker))["_previousOperation"])["operationId"]);
            Assert.Null(Json(Delivered("revit_get_current_target", "{\"target\":\"auto\"}", tracker))["_previousOperation"]);
        }

        [Fact]
        public void Finished_operation_waits_for_a_response_with_room_for_it()
        {
            // The summary keeps up to 8 KiB of the command's data: more than this response cap has room for.
            var tracker = Finished("op1", new JObject { ["success"] = true, ["data"] = new JObject { ["rows"] = new string('r', 8150) } });
            var full = Delivered("revit_get_element_details", "{\"count\":1}", tracker, SmallLimits);
            Assert.Null(Json(full)["_previousOperation"]);
            Assert.True(Encoding.UTF8.GetByteCount(RuntimeToolFilter.Serialize(full)) <= 8192);
            var next = Json(Delivered("revit_get_element_details", "{\"count\":1}", tracker));
            Assert.Equal("op1", Assert.Single((JArray)next["_previousOperation"])["operationId"]);
        }

        [Fact]
        public void Finished_operation_carries_the_user_dialogs_its_final_took_once()
        {
            // The final took the report from the add-in's buffer; the agent never read status.
            var report = new JObject { ["model"] = "Sample", ["items"] = new JArray(new JObject { ["kind"] = "failure", ["code"] = FailureGuid, ["count"] = 1 }),
                ["note"] = UserDialogBuffer.Note };
            var tracker = Finished("op1", new JObject { ["success"] = true, ["data"] = new JObject { ["opened"] = true }, ["_userDialogs"] = report });
            var next = Json(Delivered("revit_get_current_view_info", "{\"viewName\":\"Other\"}", tracker));
            var entry = Assert.Single((JArray)next["_previousOperation"]);
            Assert.Equal("Sample", entry["_userDialogs"]["model"]);
            Assert.Equal(FailureGuid, entry["_userDialogs"]["items"][0]["code"]);
            Assert.Null(next["_userDialogs"]); // Kept with the operation it came from, not shown as this call's own.
            Assert.Null(Json(Delivered("revit_get_current_view_info", "{\"viewName\":\"Other\"}", tracker))["_previousOperation"]);

            report["items"] = new JArray(Enumerable.Range(0, 40).Select(i => new JObject { ["code"] = "c" + i, ["text"] = new string('t', 300) }));
            tracker = Finished("op2", new JObject { ["success"] = true, ["_userDialogs"] = report });
            var omitted = Assert.Single((JArray)Json(Delivered("revit_get_current_view_info", "{}", tracker))["_previousOperation"])["_userDialogs"];
            Assert.True(omitted.Value<bool>("omitted"));
            Assert.Equal("Sample", omitted["model"]);
            Assert.Null(omitted["items"]);
        }

        [Fact]
        public void Dialog_group_names_who_closed_each_occurrence()
        {
            JObject Group(int count, params JObject[] ruleAnswers)
            {
                var group = new JObject { ["failureDefinitionId"] = "TaskDialog_Test_Sample", ["severity"] = "TaskDialog", ["count"] = count };
                FailureCapture.DescribeAnswers(group, ruleAnswers.Length == 0 ? null : ruleAnswers);
                return group;
            }
            JObject Answer(string button) => new JObject { ["button"] = button, ["meaning"] = "sample" };

            var user = Group(2);
            Assert.Equal("user", user["answeredBy"]);
            Assert.Null(user["answer"]);

            var rule = Group(2, Answer("1001"), Answer("1001"));
            Assert.Equal("rule", rule["answeredBy"]);
            Assert.Equal("1001", rule["answer"]["button"]);

            // A rule closed the first occurrence and the user the second: one actor or button must not stand for both.
            var mixed = Group(2, Answer("1001"));
            Assert.Equal("mixed", mixed["answeredBy"]);
            Assert.Equal(1, mixed["answeredCount"].Value<int>("rule"));
            Assert.Equal(1, mixed["answeredCount"].Value<int>("user"));
            Assert.Null(mixed["answer"]);

            var differentButtons = Group(2, Answer("1001"), Answer("1002"));
            Assert.Equal("rule", differentButtons["answeredBy"]);
            Assert.Null(differentButtons["answer"]);

            // The summary of a late final keeps the split.
            var tracker = Finished("op1", new JObject { ["success"] = true,
                ["_failure"] = new JObject { ["action"] = "committed", ["groups"] = new JArray(mixed) } });
            var response = new JObject();
            tracker.Attach(response);
            var group = response["_previousOperation"][0]["_failure"]["groups"][0];
            Assert.Equal("mixed", group["answeredBy"]);
            Assert.Equal(1, group["answeredCount"].Value<int>("user"));
        }

        [Fact]
        public void Once_only_reports_survive_a_read_rejected_for_size_and_a_spilled_result()
        {
            var tracker = Finished("op1", new JObject { ["success"] = true });
            // A read has no _operation receipt; its _userDialogs were already taken from the add-in's buffer.
            var read = new JObject { ["detail"] = new string('x', 12000),
                ["_userDialogs"] = new JObject { ["model"] = "Sample", ["items"] = new JArray(), ["note"] = UserDialogBuffer.Note } };
            var rejected = Delivered("revit_get_element_details", read.ToString(), tracker, SmallLimits);
            Assert.True(rejected.IsError);
            Assert.Contains("RESPONSE_TOO_LARGE", ((TextContentBlock)rejected.Content[0]).Text);
            Assert.Equal("Sample", Json(rejected)["_userDialogs"]["model"]);
            Assert.Equal("op1", Assert.Single((JArray)Json(rejected)["_previousOperation"])["operationId"]);

            tracker = Finished("op2", new JObject { ["success"] = true });
            var spilled = Json(Delivered("revit_send_code_to_revit", new JObject { ["result"] = new string('x', 30000) }.ToString(), tracker,
                SmallLimits, new ResponseSpillWriter(Path.Combine(_root, "spill"))));
            Assert.NotNull(spilled["path"]);
            Assert.Equal("op2", Assert.Single((JArray)spilled["_previousOperation"])["operationId"]);
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
