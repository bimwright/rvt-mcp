using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Reports.Model;
using RvtMcp.Plugin.Reports.Presentation;
using Xunit;

namespace RvtMcp.Tests.Shared.Reports
{
    [Collection("L10n")]
    public class ReportsViewModelTests : IDisposable
    {
        public ReportsViewModelTests()
        {
            L.InitializeForTests(StringTable.Build("en", new Dictionary<string, string> {
                ["reports.empty.noActiveDocument"] = "No active model", ["reports.empty.noModelReports"] = "No reports for model; choose All",
                ["reports.empty.noReports"] = "No reports", ["reports.delete.button"] = "Delete", ["reports.delete.confirm"] = "Confirm delete",
                ["reports.status.deleted"] = "Deleted", ["reports.status.deleteFailed"] = "Delete failed", ["reports.status.loadFailed"] = "Load failed",
                ["reports.status.copyFailed"] = "Copy failed", ["reports.status.copied"] = "Copied",
                ["reports.navigation.selected"] = "Selected {selected} in {view}", ["reports.navigation.selectedSome"] = "Selected {selected}; missing {missing} of {total} in {view}",
                ["reports.navigation.viewActivated"] = "Opened {view}", ["reports.navigation.elementsGone"] = "Elements gone",
                ["reports.navigation.noGraphics"] = "No graphics", ["reports.navigation.noViewFound"] = "No view", ["reports.navigation.failed"] = "Navigation failed"
            }, null, null));
        }
        public void Dispose() { L.ResetForTests(); }

        [Fact]
        public async Task Filter_matches_any_document_updates_live_state_and_empty_messages()
        {
            var runtime = new FakeReportsRuntime { ActiveDocument = new ActiveDocumentIdentity { Token = "project" } };
            var multi = Report("multi", "family", "project");
            multi.Body = "[link](revit:#0)";
            multi.Links.Add(new ReportLink { Doc = "d1", Label = "link" });
            runtime.Reports = new[] { multi, Report("other", "other") };
            using (var vm = new ReportsViewModel(runtime, new FakeNavigation(), DisplayMode.Wide))
            {
                await vm.RefreshAsync();
                Assert.Equal("multi", Assert.Single(vm.Reports).ReportId);
                Assert.True(vm.SelectedDetail.Links[0].IsLive);
                vm.Filter = ReportFilter.All;
                var other = vm.Reports.Single(r => r.ReportId == "other");
                Assert.True(other.IsOtherModel);
                runtime.ActiveDocument = new ActiveDocumentIdentity { Token = "other" };
                runtime.RaiseActiveChanged();
                Assert.False(other.IsOtherModel);
                Assert.False(vm.SelectedDetail.Links[0].IsLive);
                vm.Filter = ReportFilter.ActiveModel;
                Assert.Equal("other", Assert.Single(vm.Reports).ReportId);
                Assert.Equal("other", vm.SelectedReport.ReportId);
                runtime.ActiveDocument = null;
                runtime.RaiseActiveChanged();
                Assert.Empty(vm.Reports);
                Assert.Equal("No active model", vm.EmptyText);
                runtime.ActiveDocument = new ActiveDocumentIdentity { Token = "absent" };
                runtime.RaiseActiveChanged();
                Assert.Equal("No reports for model; choose All", vm.EmptyText);
                vm.Filter = ReportFilter.All;
                runtime.Reports = new AgentReport[0];
                await vm.RefreshAsync();
                Assert.Equal("No reports", vm.EmptyText);
            }
        }

        [Fact]
        public async Task Modes_keep_filter_and_selection_and_compact_can_collapse()
        {
            var runtime = new FakeReportsRuntime { ActiveDocument = new ActiveDocumentIdentity { Token = "a" }, Reports = new[] { Report("one", "a"), Report("two", "a") } };
            using (var vm = new ReportsViewModel(runtime, new FakeNavigation(), DisplayMode.Wide))
            {
                await vm.RefreshAsync();
                vm.SelectedReport = vm.Reports[1];
                vm.Filter = ReportFilter.All;
                vm.Mode = DisplayMode.Compact;
                Assert.Equal("two", vm.SelectedReport.ReportId);
                Assert.Equal(ReportFilter.All, vm.Filter);
                vm.CompactChangesExpanded = true;
                vm.SelectReport(vm.SelectedReport);
                Assert.Null(vm.SelectedReport);
                Assert.False(vm.CompactChangesExpanded);
                vm.Mode = DisplayMode.Wide;
                Assert.Equal("one", vm.SelectedReport.ReportId);
            }
        }

        [Fact]
        public async Task Refresh_preserves_id_and_updates_detail_and_removal_selection_follows_mode()
        {
            var runtime = new FakeReportsRuntime { ActiveDocument = new ActiveDocumentIdentity { Token = "a" }, Reports = new[] { Report("one", "a"), Report("two", "a") } };
            using (var vm = new ReportsViewModel(runtime, new FakeNavigation(), DisplayMode.Wide))
            {
                await vm.RefreshAsync();
                vm.SelectedReport = vm.Reports[1];
                var replacement = Report("two", "a"); replacement.Summary = "Updated"; replacement.UpdatedUtc = "2026-10-28T10:00:00Z";
                runtime.Reports = new[] { Report("one", "a"), replacement };
                await vm.RefreshAsync();
                Assert.Equal("two", vm.SelectedReport.ReportId);
                Assert.Equal("Updated", vm.SelectedDetail.Summary);
                runtime.Reports = new[] { Report("one", "a") };
                await vm.RefreshAsync();
                Assert.Equal("one", vm.SelectedReport.ReportId);
                vm.Mode = DisplayMode.Compact;
                runtime.Reports = new AgentReport[0];
                await vm.RefreshAsync();
                Assert.Null(vm.SelectedReport);
            }
        }

        [Fact]
        public async Task Refresh_keeps_unchanged_reports_and_the_open_detail_without_resetting_the_list()
        {
            var runtime = new FakeReportsRuntime { ActiveDocument = new ActiveDocumentIdentity { Token = "a" }, Reports = new[] { Report("one", "a"), Report("two", "a") } };
            using (var vm = new ReportsViewModel(runtime, new FakeNavigation(), DisplayMode.Wide))
            {
                await vm.RefreshAsync();
                vm.SelectedReport = vm.Reports[1];
                var item = vm.SelectedReport;
                var detail = vm.SelectedDetail;
                var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
                vm.Reports.CollectionChanged += (s, e) => actions.Add(e.Action);
                runtime.Reports = new[] { Report("new", "a"), Report("one", "a"), Report("two", "a") };
                await vm.RefreshAsync();
                Assert.Same(item, vm.SelectedReport);
                Assert.Same(detail, vm.SelectedDetail);
                Assert.Equal(new[] { "new", "one", "two" }, vm.Reports.Select(r => r.ReportId));
                Assert.DoesNotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, actions);
            }
        }

        [Fact]
        public async Task Show_report_switches_filter_and_reloads_missing_report()
        {
            var runtime = new FakeReportsRuntime { ActiveDocument = new ActiveDocumentIdentity { Token = "a" }, Reports = new[] { Report("one", "a"), Report("two", "b") } };
            using (var vm = new ReportsViewModel(runtime, new FakeNavigation(), DisplayMode.Compact))
            {
                await vm.RefreshAsync();
                vm.ShowReport("one");
                Assert.Equal("one", vm.SelectedReport.ReportId);
                vm.ShowReport("two");
                Assert.Equal(ReportFilter.All, vm.Filter);
                Assert.Equal("two", vm.SelectedReport.ReportId);
                runtime.Reports = new[] { Report("later", "b") };
                vm.ShowReport("later");
                await vm.RefreshAsync();
                Assert.Equal("later", vm.SelectedReport.ReportId);
            }
        }

        [Fact]
        public async Task Show_report_keeps_pending_id_until_a_later_watcher_refresh_finds_it()
        {
            var runtime = new FakeReportsRuntime { ActiveDocument = new ActiveDocumentIdentity { Token = "a" }, Reports = new[] { Report("one", "a") } };
            using (var vm = new ReportsViewModel(runtime, new FakeNavigation(), DisplayMode.Wide))
            {
                await vm.RefreshAsync();
                vm.ShowReport("later");
                await vm.RefreshAsync();
                Assert.Equal("one", vm.SelectedReport.ReportId);
                runtime.Reports = new[] { Report("one", "a"), Report("later", "b") };
                runtime.RaiseReportsChanged();
                await vm.RefreshAsync();
                Assert.Equal("later", vm.SelectedReport.ReportId);
                Assert.Equal(ReportFilter.All, vm.Filter);
            }
        }

        [Fact]
        public async Task Delete_requires_confirmation_cancels_on_selection_mode_and_filter_and_preserves_on_error()
        {
            var runtime = new FakeReportsRuntime { ActiveDocument = new ActiveDocumentIdentity { Token = "a" }, Reports = new[] { Report("one", "a"), Report("two", "a") } };
            using (var vm = new ReportsViewModel(runtime, new FakeNavigation(), DisplayMode.Wide))
            {
                await vm.RefreshAsync();
                vm.DeleteCommand.Execute(null);
                Assert.Equal("Confirm delete", vm.DeleteLabel);
                Assert.Empty(runtime.Deleted);
                vm.SelectedReport = vm.Reports[1];
                Assert.False(vm.IsDeleteConfirmationPending);
                vm.DeleteCommand.Execute(null); vm.Mode = DisplayMode.Compact;
                Assert.False(vm.IsDeleteConfirmationPending);
                vm.DeleteCommand.Execute(null); vm.Filter = ReportFilter.All;
                Assert.False(vm.IsDeleteConfirmationPending);
                vm.DeleteCommand.Execute(null);
                runtime.DeleteError = new IOException("synthetic");
                vm.DeleteCommand.Execute(null);
                Assert.Equal("Delete failed", vm.StatusText);
                Assert.Equal(2, vm.Reports.Count);
                runtime.DeleteError = null;
                vm.DeleteCommand.Execute(null); vm.DeleteCommand.Execute(null);
                Assert.Equal("two", Assert.Single(runtime.Deleted));
                Assert.Single(vm.Reports);
                Assert.Equal("Deleted", vm.StatusText);
            }
        }

        [Fact]
        public async Task Copy_uses_stored_plain_text_and_reports_clipboard_failure()
        {
            var report = Report("one", "a"); report.Summary = "Summary"; report.Body = "See [item](revit:#0)";
            report.Links.Add(new ReportLink { Doc = "d0", Label = "item" });
            var runtime = new FakeReportsRuntime { ActiveDocument = new ActiveDocumentIdentity { Token = "a" }, Reports = new[] { report } };
            using (var vm = new ReportsViewModel(runtime, new FakeNavigation(), DisplayMode.Wide))
            {
                await vm.RefreshAsync();
                vm.CopyCommand.Execute(null);
                Assert.Equal("Summary\n\nSee item", runtime.Copied);
                Assert.DoesNotContain("revit:#", runtime.Copied);
                runtime.CopyResult = false;
                vm.CopyCommand.Execute(null);
                Assert.Equal("Copy failed", vm.StatusText);
                var replaced = Report("one", "a"); replaced.Summary = "Summary"; replaced.UpdatedUtc = "2026-10-28T10:00:00Z";
                runtime.Reports = new[] { replaced };
                await vm.RefreshAsync();
                vm.CopyCommand.Execute(null);
                Assert.Equal("Summary", runtime.Copied);
            }
        }

        [Theory]
        [InlineData(ReportNavigationOutcome.Selected, "Selected 2 in View A", ReportStatusTone.Success)]
        [InlineData(ReportNavigationOutcome.ViewActivated, "Opened View A", ReportStatusTone.Success)]
        [InlineData(ReportNavigationOutcome.ElementsGone, "Elements gone", ReportStatusTone.Warning)]
        [InlineData(ReportNavigationOutcome.NoGraphics, "No graphics", ReportStatusTone.Warning)]
        [InlineData(ReportNavigationOutcome.NoViewFound, "No view", ReportStatusTone.Warning)]
        [InlineData(ReportNavigationOutcome.Failed, "Navigation failed", ReportStatusTone.Error)]
        public void Navigation_results_update_status(ReportNavigationOutcome outcome, string expected, ReportStatusTone tone)
        {
            var nav = new FakeNavigation();
            using (var vm = new ReportsViewModel(new FakeReportsRuntime(), nav, DisplayMode.Wide))
            {
                nav.Complete(new ReportNavigationResult { Outcome = outcome, SelectedCount = 2, ViewName = "View A" });
                Assert.Equal(expected, vm.StatusText);
                Assert.Equal(tone, vm.StatusTone);
                nav.Complete(new ReportNavigationResult { Outcome = ReportNavigationOutcome.NotMatched });
                Assert.Equal(expected, vm.StatusText);
                Assert.Equal(tone, vm.StatusTone);
            }
        }

        [Fact]
        public async Task Failed_load_keeps_existing_list_and_dispose_unsubscribes()
        {
            var runtime = new FakeReportsRuntime { ActiveDocument = new ActiveDocumentIdentity { Token = "a" }, Reports = new[] { Report("one", "a") } };
            var nav = new FakeNavigation();
            var vm = new ReportsViewModel(runtime, nav, DisplayMode.Wide);
            await vm.RefreshAsync();
            runtime.LoadError = new IOException("synthetic");
            await vm.RefreshAsync();
            Assert.Equal("one", Assert.Single(vm.Reports).ReportId);
            Assert.Equal("Load failed", vm.StatusText);
            vm.Dispose();
            var loads = runtime.LoadCount;
            runtime.RaiseReportsChanged(); runtime.RaiseActiveChanged();
            nav.Complete(new ReportNavigationResult { Outcome = ReportNavigationOutcome.Failed });
            Assert.Equal(loads, runtime.LoadCount);
            Assert.Equal("Load failed", vm.StatusText);
            Assert.Equal(0, runtime.SubscriberCount);
            Assert.Equal(0, nav.SubscriberCount);
        }

        [Fact]
        public async Task Failed_delete_keeps_report_even_for_unexpected_runtime_exception()
        {
            var runtime = new FakeReportsRuntime { ActiveDocument = new ActiveDocumentIdentity { Token = "a" }, Reports = new[] { Report("one", "a") }, DeleteError = new InvalidOperationException("synthetic") };
            using (var vm = new ReportsViewModel(runtime, new FakeNavigation(), DisplayMode.Wide))
            {
                await vm.RefreshAsync();
                vm.DeleteCommand.Execute(null);
                vm.DeleteCommand.Execute(null);
                Assert.Single(vm.Reports);
                Assert.Equal("Delete failed", vm.StatusText);
            }
        }

        [Fact]
        public async Task Refresh_preserves_selection_and_status_when_collection_reset_clears_bound_selection()
        {
            var runtime = new FakeReportsRuntime { ActiveDocument = new ActiveDocumentIdentity { Token = "a" }, Reports = new[] { Report("one", "a"), Report("two", "a") } };
            var nav = new FakeNavigation();
            using (var vm = new ReportsViewModel(runtime, nav, DisplayMode.Wide))
            {
                await vm.RefreshAsync();
                vm.SelectedReport = vm.Reports[1];
                nav.Complete(new ReportNavigationResult { Outcome = ReportNavigationOutcome.NoGraphics });
                vm.Reports.CollectionChanged += (s, e) => { if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) vm.SelectedReport = null; };
                runtime.Reports = new[] { Report("one", "a"), Report("two", "a") };
                await vm.RefreshAsync();
                Assert.Equal("two", vm.SelectedReport.ReportId);
                Assert.Equal("No graphics", vm.StatusText);
            }
        }

        [Fact]
        public async Task Filter_republishes_retained_selection_after_collection_reset()
        {
            var runtime = new FakeReportsRuntime { ActiveDocument = new ActiveDocumentIdentity { Token = "a" }, Reports = new[] { Report("one", "a"), Report("two", "a") } };
            using (var vm = new ReportsViewModel(runtime, new FakeNavigation(), DisplayMode.Wide))
            {
                await vm.RefreshAsync();
                vm.SelectedReport = vm.Reports[1];
                var notified = false;
                vm.Reports.CollectionChanged += (s, e) => { if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) vm.SelectedReport = null; };
                vm.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(vm.SelectedReport)) notified = true; };
                vm.Filter = ReportFilter.All;
                Assert.Equal("two", vm.SelectedReport.ReportId);
                Assert.True(notified);
            }
        }

        [Fact]
        public async Task Refresh_applies_only_through_post_and_discards_a_stale_queued_callback()
        {
            var posted = new System.Collections.Concurrent.ConcurrentQueue<Action>();
            var firstPost = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondPost = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var count = 0;
            var runtime = new FakeReportsRuntime { ActiveDocument = new ActiveDocumentIdentity { Token = "a" },
                Loader = n => new[] { Report(n == 1 ? "old" : "new", "a") },
                PostAction = action => { posted.Enqueue(action); if (Interlocked.Increment(ref count) == 1) firstPost.TrySetResult(true); else secondPost.TrySetResult(true); } };
            using (var vm = new ReportsViewModel(runtime, new FakeNavigation(), DisplayMode.Wide))
            {
                var task = vm.RefreshAsync();
                await firstPost.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Empty(vm.Reports);
                runtime.RaiseReportsChanged();
                Assert.True(posted.TryDequeue(out var old)); old();
                await secondPost.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Empty(vm.Reports);
                Assert.True(posted.TryDequeue(out var latest)); latest();
                await task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("new", Assert.Single(vm.Reports).ReportId);
            }
        }

        [Fact]
        public async Task Delete_during_a_load_prevents_the_old_result_restoring_the_deleted_report()
        {
            var runtime = new FakeReportsRuntime { ActiveDocument = new ActiveDocumentIdentity { Token = "a" }, Reports = new[] { Report("one", "a") } };
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var release = new ManualResetEventSlim())
            using (var vm = new ReportsViewModel(runtime, new FakeNavigation(), DisplayMode.Wide))
            {
                await vm.RefreshAsync();
                runtime.Loader = n =>
                {
                    if (n == 2) { started.TrySetResult(true); release.Wait(TimeSpan.FromSeconds(10)); return new[] { Report("one", "a") }; }
                    return runtime.Reports;
                };
                try
                {
                    var loading = vm.RefreshAsync();
                    await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    vm.DeleteCommand.Execute(null); vm.DeleteCommand.Execute(null);
                    runtime.Reports = new AgentReport[0];
                    release.Set();
                    await loading.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.Empty(vm.Reports);
                    Assert.Equal("Deleted", vm.StatusText);
                }
                finally { release.Set(); }
            }
        }

        [Fact]
        public async Task Disposing_during_a_load_prevents_late_results_from_being_applied()
        {
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var release = new ManualResetEventSlim())
            {
                var runtime = new FakeReportsRuntime { ActiveDocument = new ActiveDocumentIdentity { Token = "a" },
                    Loader = n => { started.TrySetResult(true); release.Wait(TimeSpan.FromSeconds(10)); return new[] { Report("late", "a") }; } };
                var vm = new ReportsViewModel(runtime, new FakeNavigation(), DisplayMode.Wide);
                try
                {
                    var loading = vm.RefreshAsync();
                    await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    vm.Dispose();
                    release.Set();
                    await loading.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.Empty(vm.Reports);
                    Assert.Null(vm.SelectedReport);
                }
                finally { release.Set(); vm.Dispose(); }
            }
        }

        [Fact]
        public void Partial_navigation_reports_missing_and_selected_counts()
        {
            var nav = new FakeNavigation();
            using (var vm = new ReportsViewModel(new FakeReportsRuntime(), nav, DisplayMode.Wide))
            {
                nav.Complete(new ReportNavigationResult { Outcome = ReportNavigationOutcome.Selected, SelectedCount = 2, MissingCount = 3, ViewName = "View A" });
                Assert.Equal("Selected 2; missing 3 of 5 in View A", vm.StatusText);
                Assert.Equal(ReportStatusTone.Warning, vm.StatusTone);
            }
        }

        [Fact]
        public async Task Concurrent_refresh_coalesces_requests_and_discards_old_results()
        {
            var runtime = new FakeReportsRuntime { ActiveDocument = new ActiveDocumentIdentity { Token = "a" } };
            var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var releaseFirst = new ManualResetEventSlim())
            using (var releaseSecond = new ManualResetEventSlim())
            {
                runtime.Loader = call =>
                {
                    if (call == 1) { firstStarted.TrySetResult(true); releaseFirst.Wait(TimeSpan.FromSeconds(10)); return new[] { Report("old", "a") }; }
                    secondStarted.TrySetResult(true); releaseSecond.Wait(TimeSpan.FromSeconds(10)); return new[] { Report("new", "a") };
                };
                using (var vm = new ReportsViewModel(runtime, new FakeNavigation(), DisplayMode.Wide))
                {
                    try
                    {
                        var refresh = vm.RefreshAsync();
                        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        runtime.RaiseReportsChanged(); runtime.RaiseReportsChanged();
                        releaseFirst.Set();
                        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        Assert.Empty(vm.Reports);
                        releaseSecond.Set();
                        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
                        Assert.Equal(2, runtime.LoadCount);
                        Assert.Equal("new", Assert.Single(vm.Reports).ReportId);
                    }
                    finally { releaseFirst.Set(); releaseSecond.Set(); }
                }
            }
        }

        private static AgentReport Report(string id, params string[] tokens)
        {
            return new AgentReport { ReportId = id, TaskId = "task-" + id, Summary = id, Outcome = "completed", CreatedUtc = "2026-10-28T09:00:00Z",
                Documents = tokens.Select((token, index) => new ReportDocument { Ref = "d" + index, Token = token, Title = "Synthetic " + token }).ToList() };
        }
    }

    internal sealed class FakeReportsRuntime : IReportsRuntime
    {
        private EventHandler _reportsChanged, _activeChanged;
        private int _loadCount;
        public IReadOnlyList<AgentReport> Reports { get; set; } = new AgentReport[0];
        public Func<int, IReadOnlyList<AgentReport>> Loader { get; set; }
        public Exception LoadError { get; set; }
        public Exception DeleteError { get; set; }
        public int LoadCount => _loadCount;
        public List<string> Deleted { get; } = new List<string>();
        public int SubscriberCount => (_reportsChanged?.GetInvocationList().Length ?? 0) + (_activeChanged?.GetInvocationList().Length ?? 0);
        public IReadOnlyList<AgentReport> LoadReports() { var n = Interlocked.Increment(ref _loadCount); if (LoadError != null) throw LoadError; return Loader != null ? Loader(n) : Reports; }
        public void DeleteReport(string reportId) { if (DeleteError != null) throw DeleteError; Deleted.Add(reportId); }
        public event EventHandler ReportsChanged { add { _reportsChanged += value; } remove { _reportsChanged -= value; } }
        public ActiveDocumentIdentity ActiveDocument { get; set; }
        public event EventHandler ActiveDocumentChanged { add { _activeChanged += value; } remove { _activeChanged -= value; } }
        public Action<Action> PostAction { get; set; }
        public void Post(Action action) { if (PostAction != null) PostAction(action); else action(); }
        public bool CopyResult { get; set; } = true;
        public string Copied { get; private set; }
        public bool CopyText(string text) { Copied = text; return CopyResult; }
        public DateTime Now => new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Local);
        public void RaiseReportsChanged() { _reportsChanged?.Invoke(this, EventArgs.Empty); }
        public void RaiseActiveChanged() { _activeChanged?.Invoke(this, EventArgs.Empty); }
    }
}
