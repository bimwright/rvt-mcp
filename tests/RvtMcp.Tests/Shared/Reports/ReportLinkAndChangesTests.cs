using System;
using System.Collections.Generic;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Reports.Model;
using RvtMcp.Plugin.Reports.Presentation;
using Xunit;

namespace RvtMcp.Tests.Shared.Reports
{
    [Collection("L10n")]
    public class ReportLinkAndChangesTests
    {
        [Fact]
        public void Link_is_inert_when_dim_and_live_navigation_preserves_targets()
        {
            var active = new ActiveDocumentIdentity { Token = "other" };
            var document = new ReportDocument { Ref = "d0", Token = "token", Title = "Synthetic model" };
            var element = new ReportElement { Id = 12, UniqueId = "uid-12", Kind = ReportElementKinds.Sheet };
            var view = new ReportView { Id = 9, UniqueId = "view-9" };
            var nav = new FakeNavigation();
            var link = new ReportLinkViewModel(document, new ReportLink { Label = "Open", Elements = new List<ReportElement> { element }, View = view }, nav, () => active);
            Assert.False(link.IsLive);
            Assert.Equal("Synthetic model", link.DimTooltip);
            Assert.Equal(1, link.ElementCount);
            Assert.Same(element, Assert.Single(link.Elements));
            Assert.Equal(9, link.ViewHintId);
            Assert.Equal(ReportLinkTargetKind.Sheet, link.TargetKind);
            link.OpenCommand.Execute(null);
            Assert.Null(nav.LastRequest);

            active = new ActiveDocumentIdentity { Token = "token" };
            var changed = false;
            link.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(link.IsLive)) changed = true; };
            link.UpdateActiveDocument();
            Assert.True(changed);
            Assert.True(link.IsLive);
            link.OpenCommand.Execute(null);
            Assert.Same(document, nav.LastRequest.Document);
            Assert.Same(element, Assert.Single(nav.LastRequest.Elements));
            Assert.Same(view, nav.LastRequest.ViewHint);
        }

        [Fact]
        public void Changes_distinguish_missing_empty_and_incomplete_truncated_data()
        {
            var nav = new FakeNavigation();
            var docs = new[] { new ReportDocument { Ref = "d0", Token = "token", Title = "Synthetic model" } };
            var none = new ChangeSectionViewModel(null, docs, nav, () => null);
            Assert.False(none.HasData);
            Assert.Equal(L.T("reports.changes.noData"), none.NoteText);
            Assert.Equal("", none.ChipText);
            Assert.Equal("", none.ScopeText);
            var empty = new ChangeSectionViewModel(new ReportChanges { Complete = true }, docs, nav, () => null);
            Assert.True(empty.HasData);
            Assert.Equal(L.T("reports.changes.empty"), empty.NoteText);
            Assert.Equal(L.T("reports.changes.scopeProcess"), empty.ScopeText);
            var unknown = new ChangeSectionViewModel(new ReportChanges { Scope = "unknown" }, docs, nav, () => null);
            Assert.Equal(L.T("reports.changes.scopeUnknown"), unknown.ScopeText);
            var incomplete = new ChangeSectionViewModel(new ReportChanges { Complete = false, Truncated = true }, docs, nav, () => null);
            Assert.Equal(L.T("reports.changes.incomplete"), incomplete.IncompleteText);
            Assert.Equal(L.T("reports.changes.truncated"), incomplete.TruncatedText);
        }

        [Fact]
        public void Changes_group_counts_and_navigate_only_live_added_or_modified_elements()
        {
            var active = new ActiveDocumentIdentity { Token = "other" };
            var document = new ReportDocument { Ref = "d0", Token = "token", Title = "Synthetic model" };
            var changes = new ReportChanges { Complete = true, Documents = new List<ReportDocumentChanges> {
                new ReportDocumentChanges { Doc = "d0", Added = new List<ReportChangedElement> { new ReportChangedElement { Id = 17, UniqueId = "uid-17", Category = "Pipe" } },
                    Modified = new List<ReportChangedElement> { new ReportChangedElement { Id = 18, UniqueId = "uid-18", Category = "Wall" } },
                    Deleted = new List<ReportChangedElement> { new ReportChangedElement { Id = 19 } } } } };
            var nav = new FakeNavigation();
            var vm = new ChangeSectionViewModel(changes, new[] { document }, nav, () => active);
            Assert.Equal(1, vm.AddedCount);
            Assert.Equal(1, vm.ModifiedCount);
            Assert.Equal(1, vm.DeletedCount);
            var group = Assert.Single(vm.Documents);
            Assert.Equal("Synthetic model", group.Title);
            Assert.False(group.IsLive);
            var groupChanged = false;
            var itemChanged = false;
            group.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(group.IsLive)) groupChanged = true; };
            group.Added[0].PropertyChanged += (s, e) => { if (e.PropertyName == nameof(ChangeItemViewModel.IsLive)) itemChanged = true; };
            group.Added[0].OpenCommand.Execute(null);
            Assert.Null(nav.LastRequest);
            active = new ActiveDocumentIdentity { Token = "token" };
            vm.UpdateActiveDocument();
            Assert.True(group.IsLive);
            Assert.True(groupChanged);
            Assert.True(itemChanged);
            group.Added[0].OpenCommand.Execute(null);
            Assert.Equal("uid-17", Assert.Single(nav.LastRequest.Elements).UniqueId);
            Assert.Equal(17, nav.LastRequest.Elements[0].Id);
            Assert.Same(document, nav.LastRequest.Document);
            group.Modified[0].OpenCommand.Execute(null);
            Assert.Equal("uid-18", Assert.Single(nav.LastRequest.Elements).UniqueId);
            nav.LastRequest = null;
            group.Deleted[0].OpenCommand.Execute(null);
            Assert.Null(nav.LastRequest);
        }
    }

    internal sealed class FakeNavigation : INavigationService
    {
        private EventHandler<ReportNavigationResult> _completed;
        public event EventHandler<ReportNavigationRequest> Started;
        public int StartedSubscriberCount => Started?.GetInvocationList().Length ?? 0;
        public ReportNavigationRequest LastRequest { get; set; }
        public event EventHandler<ReportNavigationResult> Completed { add { _completed += value; } remove { _completed -= value; } }
        public int SubscriberCount => _completed?.GetInvocationList().Length ?? 0;
        public void Navigate(ReportNavigationRequest request) { LastRequest = request; Started?.Invoke(this, request); }
        public void Complete(ReportNavigationResult result) { _completed?.Invoke(this, result); }
    }
}
