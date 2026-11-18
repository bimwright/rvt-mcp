using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RvtMcp.Failures;
using RvtMcp.Plugin;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    // Issue-16 phase C: windows that wait for the user without a Revit event.
    [Collection("ServerStateConfig")]
    public class WindowDialogTests
    {
        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);
        private const long Dialog = WindowModalTracker.WsPopup | WindowModalTracker.WsCaption;

        private static WindowSighting Box(long handle, string title = "Box") => new WindowSighting
        { Handle = handle, ClassName = "#32770", Title = title, Style = Dialog, ExStyle = 0x10101, SameUiThread = true, Visible = true, Enabled = true };

        private static WindowModalTransitions Tick(WindowModalTracker tracker, double seconds, params WindowSighting[] windows)
            => tracker.Tick(T0.AddSeconds(seconds), false, windows, _ => true);

        [Fact]
        public void A_box_is_confirmed_after_three_sightings_one_second_apart()
        {
            var tracker = new WindowModalTracker();
            Assert.Single(Tick(tracker, 0, Box(1)).New);
            Assert.Empty(Tick(tracker, 0.25, Box(1)).Confirmed);
            Assert.Empty(Tick(tracker, 1.0, Box(1)).Confirmed);
            Assert.Empty(Tick(tracker, 1.75, Box(1)).Confirmed);
            var confirmed = Tick(tracker, 2.0, Box(1));
            Assert.Single(confirmed.Confirmed);
            Assert.Equal("window", confirmed.Confirmed[0].Kind);
            Assert.Single(confirmed.Open);
            Assert.Empty(Tick(tracker, 3.0, Box(1)).Confirmed); // reported once per appearance
        }

        [Fact]
        public void A_window_whose_thread_does_not_pump_is_never_confirmed()
        {
            var tracker = new WindowModalTracker();
            for (var second = 0; second <= 6; second++)
                Assert.Empty(tracker.Tick(T0.AddSeconds(second), false, new[] { Box(1) }, _ => false).Confirmed);
            // Pumping again starts the count over.
            Assert.Empty(Tick(tracker, 7, Box(1)).Confirmed);
            Assert.Empty(Tick(tracker, 8, Box(1)).Confirmed);
            Assert.Single(Tick(tracker, 9, Box(1)).Confirmed);
        }

        [Fact]
        public void A_window_with_a_progress_bar_is_progress_and_never_confirmed()
        {
            var tracker = new WindowModalTracker();
            var progress = Box(1, "Working"); progress.HasProgressBar = true;
            Assert.Equal("progress", Tick(tracker, 0, progress).New.Single().Kind);
            for (var second = 1; second <= 5; second++) Assert.Empty(Tick(tracker, second, progress).Confirmed);
        }

        [Fact]
        public void An_enabled_main_window_means_no_modal_and_closes_appearances()
        {
            var tracker = new WindowModalTracker();
            Tick(tracker, 0, Box(1)); Tick(tracker, 1, Box(1)); Tick(tracker, 2, Box(1));
            var closed = tracker.Tick(T0.AddSeconds(2.25), true, new[] { Box(1) }, _ => true);
            Assert.Single(closed.Closed);
            Assert.Empty(closed.Open);
            Assert.Empty(tracker.OpenAppearances);
        }

        [Theory]
        [InlineData("tool")]
        [InlineData("own")]
        [InlineData("thread")]
        [InlineData("style")]
        [InlineData("hidden")]
        [InlineData("disabled")]
        public void Only_dialog_like_windows_of_the_UI_thread_are_candidates(string variant)
        {
            var window = Box(1);
            switch (variant)
            {
                case "tool": window.ExStyle = 0x90180; break;      // the hidden "...Monitor" window of the spike
                case "own": window.IsOwn = true; break;
                case "thread": window.SameUiThread = false; break;
                case "style": window.Style = 0x06C80000 & ~WindowModalTracker.WsCaption; break;
                case "hidden": window.Visible = false; break;
                case "disabled": window.Enabled = false; break;
            }
            Assert.True(WindowModalTracker.IsCandidate(Box(2)));
            Assert.False(WindowModalTracker.IsCandidate(window));
        }

        [Fact]
        public void A_TaskDialog_with_an_event_then_a_MessageBox_in_one_command_reports_only_the_MessageBox()
        {
            var tracker = new WindowModalTracker();
            tracker.NoteEvent(T0, taskDialog: true);
            var taskWindow = Box(1, "Revit");
            taskWindow.HasTaskDialogFrame = true;
            var taskDialog = Tick(tracker, 0.1, taskWindow).New.Single();
            Assert.Equal("event", taskDialog.Kind);
            for (var second = 1; second <= 4; second++) Assert.Empty(Tick(tracker, second, taskWindow).Confirmed);
            Assert.Single(Tick(tracker, 4.5).Closed); // the user closed the TaskDialog; the command continues
            Assert.Equal("window", Tick(tracker, 4.75, Box(2, "Add-in")).New.Single().Kind);
            Tick(tracker, 5.75, Box(2, "Add-in"));
            Assert.Equal("Add-in", Tick(tracker, 6.75, Box(2, "Add-in")).Confirmed.Single().Title);
        }

        [Fact]
        public void Unseen_TaskDialog_followed_by_a_MessageBox_does_not_consume_its_window()
        {
            var tracker = new WindowModalTracker();
            tracker.NoteEvent(T0, taskDialog: true);
            var second = Box(2, "Same caption");
            Tick(tracker, 0.1, second);
            Tick(tracker, 1.1, second);
            Assert.Equal("Same caption", Tick(tracker, 2.1, second).Confirmed.Single().Title);
        }

        [Fact]
        public void Two_MessageBoxes_in_a_row_are_two_appearances_and_both_are_reported()
        {
            var tracker = new WindowModalTracker();
            Tick(tracker, 0, Box(1, "First")); Tick(tracker, 1, Box(1, "First"));
            var first = Tick(tracker, 2, Box(1, "First")).Confirmed.Single();
            Assert.Single(Tick(tracker, 2.5).Closed);
            // Windows may reuse the handle: it is still a new appearance.
            Tick(tracker, 2.75, Box(1, "Second")); Tick(tracker, 3.75, Box(1, "Second"));
            var second = Tick(tracker, 4.75, Box(1, "Second")).Confirmed.Single();
            Assert.NotEqual(first.AppearanceId, second.AppearanceId);
            Assert.Equal("Second", second.Title);
        }

        [Fact]
        public void An_answered_or_expired_event_does_not_hide_the_next_window()
        {
            var tracker = new WindowModalTracker();
            var answered = tracker.NoteEvent(T0);
            answered(); // OverrideResult: this dialog never shows
            Assert.Equal("window", Tick(tracker, 0.1, Box(1)).New.Single().Kind);

            var late = new WindowModalTracker();
            late.NoteEvent(T0);
            Assert.Equal("window", Tick(late, 6, Box(1)).New.Single().Kind);
        }

        [Fact]
        public void Reset_returns_confirmed_appearances_so_their_state_is_released()
        {
            var tracker = new WindowModalTracker();
            Tick(tracker, 0, Box(1)); Tick(tracker, 1, Box(1)); Tick(tracker, 2, Box(1)); Tick(tracker, 2.25, Box(1), Box(2));
            var released = tracker.Reset();
            Assert.Single(released);
            Assert.Empty(tracker.OpenAppearances);
        }

        [Fact]
        public void Window_close_releases_host_wait_only_for_the_current_appearance()
        {
            var execution = new TaskCompletionSource<string>();
            var life = PendingRequestLifecycle.For(execution);
            var frames = new List<JObject>();
            life.Configure("req", null, line => frames.Add(JObject.Parse(line)));
            Assert.True(life.TryStart());
            void Wait(string id) => life.EmitDialogWait(new JObject { ["dialog"] = new JObject { ["kind"] = "window", ["appearanceId"] = id } });
            try
            {
                Wait("one");
                Wait("two");
                life.WindowClosed("one");
                Assert.True(PendingRequestLifecycle.IsHostWaitingForUser);
                life.WindowClosed("two");
                Assert.False(PendingRequestLifecycle.IsHostWaitingForUser);
                Assert.Equal("window_closed", frames.Last()["event"]);
                Assert.Equal("two", frames.Last()["appearanceId"]);
                Assert.False(execution.Task.IsCompleted);
                Wait("three");
                life.EmitDialogWait(new JObject { ["dialog"] = new JObject { ["kind"] = "TaskDialog" } });
                life.WindowClosed("three");
                Assert.True(PendingRequestLifecycle.IsHostWaitingForUser);
            }
            finally { execution.SetResult("{}"); }
        }

        [Fact]
        public void Window_state_expires_and_is_cleared_only_by_its_own_appearance()
        {
            long now = 1000;
            var clock = PendingRequestLifecycle.Clock;
            PendingRequestLifecycle.Clock = () => now;
            try
            {
                PendingRequestLifecycle.SetWindowModal("a1", "Add-in message");
                Assert.True(PendingRequestLifecycle.IsHostWaitingForUser);
                var window = PendingRequestLifecycle.WindowModal;
                Assert.Equal("window", window["kind"]);
                Assert.Equal("Add-in message", window["title"]);
                Assert.Equal(2, window.Count); // title only, by owner decision

                PendingRequestLifecycle.ClearWindowModal("other");
                Assert.NotNull(PendingRequestLifecycle.WindowModal);
                PendingRequestLifecycle.ClearWindowModal("a1");
                Assert.Null(PendingRequestLifecycle.WindowModal);
                Assert.False(PendingRequestLifecycle.IsHostWaitingForUser);

                // A probe that stops refreshing (crashed thread) cannot leave Revit busy.
                PendingRequestLifecycle.SetWindowModal("a2", "Stale");
                now += (long)(PendingRequestLifecycle.WindowModalLifetime.TotalSeconds * Stopwatch.Frequency) + 1;
                Assert.Null(PendingRequestLifecycle.WindowModal);
                Assert.False(PendingRequestLifecycle.IsHostWaitingForUser);
            }
            finally { PendingRequestLifecycle.Clock = clock; PendingRequestLifecycle.ClearWindowModal("a1"); PendingRequestLifecycle.ClearWindowModal("a2"); }
        }

        [Fact]
        public void A_confirmed_window_answers_queued_requests_at_once_with_its_title()
        {
            var queued = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var waiting = PendingRequestLifecycle.For(queued);
            var started = PendingRequestLifecycle.For(running);
            waiting.Configure("q-1", Guid.NewGuid().ToString("N"), _ => { });
            started.Configure("r-1", Guid.NewGuid().ToString("N"), _ => { });
            Assert.True(started.TryStart());
            try
            {
                PendingRequestLifecycle.SetWindowModal("a3", "Select file");
                Assert.True(PendingRequestLifecycle.FailQueued() >= 1);
                Assert.True(queued.Task.IsCompleted);
                var response = JObject.Parse(queued.Task.Result);
                Assert.Equal("REVIT_REQUEST_NOT_STARTED", response["error"]);
                Assert.Equal("not_started", response["_operation"]["outcome"]);
                Assert.True(response["_operation"].Value<bool>("blockedByWindow"));
                Assert.Equal("Select file", response["_operation"]["window"]["title"]);
                Assert.Equal(FailureCapture.WindowBlockedNextStep, response["_operation"]["nextStep"]);
                Assert.False(waiting.TryStart());
                Assert.False(running.Task.IsCompleted); // a started request keeps its own outcome
            }
            finally { PendingRequestLifecycle.ClearWindowModal("a3"); running.TrySetResult("{}"); }
        }

        [Fact]
        public void A_window_wait_carries_its_own_next_step()
        {
            var execution = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var lifecycle = PendingRequestLifecycle.For(execution);
            string emitted = null;
            lifecycle.Configure("w-1", Guid.NewGuid().ToString("N"), frame => emitted = frame);
            try
            {
                Assert.True(lifecycle.TryStart());
                lifecycle.EmitDialogWait(new JObject { ["dialogId"] = "", ["manual"] = true, ["dialog"] = new JObject
                    { ["kind"] = "window", ["title"] = "Spike", ["text"] = "Click OK", ["buttons"] = new JArray(new JObject { ["text"] = "OK" }), ["source"] = "win32" } });
                var frame = JObject.Parse(emitted);
                Assert.Equal("window", frame["_operation"]["dialog"]["kind"]);
                Assert.Equal(FailureCapture.WindowWaitingNextStep, frame["_operation"]["nextStep"]);
                var pending = new GatewayPendingRequest(frame.Value<string>("operationId"), 1);
                Assert.False(pending.Route(frame));
                var receipt = JObject.Parse(pending.Caller.Task.Result);
                Assert.Equal("REVIT_OPERATION_REQUIRES_USER", receipt["error"]);
                Assert.Equal("Click OK", receipt["_operation"]["dialog"]["text"]);
                Assert.Equal("Spike", pending.BlockedBy()["dialog"]["title"]);
            }
            finally { execution.TrySetResult("{}"); }
        }

        [Fact]
        public async Task A_new_request_is_refused_at_once_with_the_window_title_only()
        {
            PendingRequestLifecycle.SetWindowModal("a4", "Add-in question");
            try
            {
                using var stream = new MemoryStream();
                using var writer = new TransportResponseWriter(new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true });
                var request = new JObject { ["id"] = "blocked", ["operation_id"] = "new-op", ["command"] = "get_current_view_info" };
                writer.Dispatch(request, request.ToString(), (_, __) => throw new Exception("A blocking window must not enqueue Revit work."));
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (stream.Length == 0 && DateTime.UtcNow < deadline) await Task.Delay(5);
                var response = JObject.Parse(Encoding.UTF8.GetString(stream.ToArray()));
                Assert.StartsWith("REVIT_BUSY", response.Value<string>("error"));
                Assert.True(response["_operation"].Value<bool>("blockedByWindow"));
                Assert.Equal("Add-in question", response["_operation"]["window"]["title"]);
                Assert.Null(response["_operation"]["window"]["text"]);
                Assert.Equal("not_started", response["_operation"]["outcome"]);
            }
            finally { PendingRequestLifecycle.ClearWindowModal("a4"); }
        }

        [Fact]
        public void The_ledger_reports_a_window_wait_while_the_window_is_open()
        {
            var root = Path.Combine(Path.GetTempPath(), "rvtmcp-window-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FailureLedgerStore(Path.Combine(root, "ledger.db"));
                store.Initialize();
                var operation = new FailureOperation { Tool = "send_code_to_revit" };
                Assert.True(store.SaveOperation(operation).Recorded);
                var dialog = new JObject { ["kind"] = "window", ["title"] = "Spike", ["text"] = "Click OK", ["appearanceId"] = "app-1" };

                Assert.True(store.MarkWindowWait(operation.OperationId, dialog));
                var waiting = store.GetOperation(operation.OperationId);
                Assert.Equal("waiting_for_user", waiting.Execution);
                Assert.Equal("Spike", waiting.WindowDialog["title"]);

                Assert.False(store.ClearWindowWait(operation.OperationId, "app-2")); // another window does not clear it
                Assert.Equal("waiting_for_user", store.GetOperation(operation.OperationId).Execution);
                Assert.True(store.ClearWindowWait(operation.OperationId, "app-1"));
                var running = store.GetOperation(operation.OperationId);
                Assert.Equal("running", running.Execution);
                Assert.Null(running.WindowDialog);

                // A finished operation is never reopened by a late window update.
                operation.Execution = "completed"; operation.UpdatedUtc = DateTimeOffset.UtcNow.AddSeconds(1);
                Assert.True(store.SaveOperation(operation).Recorded);
                Assert.False(store.MarkWindowWait(operation.OperationId, dialog));
                Assert.Equal("completed", store.GetOperation(operation.OperationId).Execution);
                Assert.False(store.MarkWindowWait(Guid.NewGuid().ToString("N"), dialog));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public void A_final_save_prepared_before_a_window_update_still_wins()
        {
            var root = Path.Combine(Path.GetTempPath(), "rvtmcp-window-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FailureLedgerStore(Path.Combine(root, "ledger.db"));
                store.Initialize();
                var operation = new FailureOperation { Tool = "send_code_to_revit", UpdatedUtc = DateTimeOffset.UtcNow.AddSeconds(-5) };
                Assert.True(store.SaveOperation(operation).Recorded);
                // UI thread prepares the final result at T1 ...
                var final = new FailureOperation { OperationId = operation.OperationId, Tool = operation.Tool,
                    Execution = "completed", Outcome = "committed", UpdatedUtc = DateTimeOffset.UtcNow.AddSeconds(-1) };
                // ... the probe records a window at T2 > T1 ...
                Assert.True(store.MarkWindowWait(operation.OperationId, new JObject { ["kind"] = "window", ["title"] = "Late", ["appearanceId"] = "app-9" }));
                // ... and the final save at T1 must still be the stored result.
                Assert.True(store.SaveOperation(final).Recorded);
                var stored = store.GetOperation(operation.OperationId);
                Assert.Equal("completed", stored.Execution);
                Assert.Equal("committed", stored.Outcome);
                Assert.Null(stored.WindowDialog);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Theory]
        [InlineData("msctls_progress32", "#32770", true)]
        [InlineData("WindowsForms10.msctls_progress32.app.0.1", "WindowsForms10.Window.8.app.0.1", true)]
        [InlineData("msctls_progress32", "Address Band Root", false)] // Explorer address bar inside the common file dialogs (LC-3)
        [InlineData("Static", "#32770", false)]
        [InlineData("", "#32770", false)]
        public void Only_a_real_progress_control_marks_a_progress_window(string className, string parentClassName, bool expected)
            => Assert.Equal(expected, WindowModalTracker.IsProgressControl(className, parentClassName));

        [Fact]
        public void A_progress_bar_that_appears_late_still_prevents_confirmation()
        {
            var tracker = new WindowModalTracker();
            Assert.Equal("window", Tick(tracker, 0, Box(1, "Exporting")).New.Single().Kind);
            var later = Box(1, "Exporting"); later.HasProgressBar = true;
            for (var second = 1; second <= 5; second++) Assert.Empty(Tick(tracker, second, later).Confirmed);
            Assert.Equal("progress", tracker.OpenAppearances.Single().Kind);
        }

        [Theory]
        [InlineData(0, 200u)]
        [InlineData(150, 150u)]
        [InlineData(299, 1u)]
        [InlineData(300, 0u)]
        [InlineData(450, 0u)]
        public void Each_message_wait_gets_only_the_rest_of_the_read_budget(long elapsed, uint expected)
            => Assert.Equal(expected, WindowModalTracker.RemainingTimeout(elapsed, 300, 200));

        [Fact]
        public void A_window_is_attributed_only_to_the_model_of_an_unchanged_context()
        {
            Assert.True(WindowModalTracker.ContextUnchanged(5, 5, 5));
            Assert.False(WindowModalTracker.ContextUnchanged(4, 5, 5)); // snapshot taken before the window's context
            Assert.False(WindowModalTracker.ContextUnchanged(5, 5, 6)); // a model opened or the view changed since

            var log = new WindowOperationLog();
            var box = new JObject { ["kind"] = "window", ["title"] = "Box", ["text"] = "Text" };
            // Shown in model A, closed after send_code opened model B: no model, never A.
            var movedAway = log.Add(box, 5, "model-A");
            log.Close(movedAway, WindowModalTracker.ContextUnchanged(5, 5, 6));
            // Shown and closed in model A with nothing changing: model A.
            var stayed = log.Add(box, 5, "model-A");
            log.Close(stayed, WindowModalTracker.ContextUnchanged(5, 5, 5));
            // Shown while the context was uncertain: no model.
            log.Add(box, 5, null);
            // Still open when the operation ends: certain only if nothing changed since it was seen.
            log.Add(box, 7, "model-B");
            log.Finish(8, out var withModel, out var withoutModel);
            Assert.Equal(new[] { "model-A" }, withModel.Select(p => p.Value));
            Assert.Equal(3, withoutModel.Count);
            log.Finish(5, out withModel, out _);
            Assert.Single(withModel); // the open model-B window was seen at 7, not 5
        }

        [Fact]
        public void Windows_past_the_cap_are_counted_not_dropped_silently()
        {
            var log = new WindowOperationLog();
            var tokens = Enumerable.Range(0, WindowOperationLog.MaxWindows + 2)
                .Select(i => log.Add(new JObject { ["title"] = "Box " + i }, 1, "model")).ToList();
            Assert.Equal(WindowOperationLog.MaxWindows, tokens.Count(t => t != null));
            Assert.Equal(2, log.Omitted);
            log.Finish(1, out var withModel, out _);
            Assert.Equal(WindowOperationLog.MaxWindows, withModel.Count);
        }

        [Fact]
        public void A_title_only_window_reaches_the_user_dialog_report()
        {
            var buffer = new UserDialogBuffer();
            var key = UserDialogBuffer.Key(@"C:\Models\Sample.rvt", "Sample");
            buffer.Add(key, "Sample", new JObject { ["kind"] = "window", ["title"] = "Add-in settings" }, T0);
            buffer.Add(key, "Sample", new JObject { ["kind"] = "window", ["title"] = "Add-in settings" }, T0);
            buffer.Add(key, "Sample", new JObject { ["kind"] = "window", ["title"] = " " }, T0);
            buffer.Add(key, "Sample", new JObject { ["kind"] = "TaskDialog", ["code"] = "0" }, T0); // unchanged: nothing usable
            var items = (JArray)buffer.Take(key)["items"];
            var item = (JObject)items.Single();
            Assert.Equal("window", item["kind"]);
            Assert.Equal("Add-in settings", item["title"]);
            Assert.Equal(2, item.Value<int>("count"));
        }
    }
}
