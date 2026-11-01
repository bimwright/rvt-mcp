using System;
using System.Collections.Generic;
using System.Linq;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Presentation;
using RvtMcp.Plugin.Reports.Model;

namespace RvtMcp.Plugin.Reports.Presentation
{
    public sealed class ChangeSectionViewModel
    {
        private readonly ReportChanges _changes;

        public ChangeSectionViewModel(ReportChanges changes, IReadOnlyList<ReportDocument> documents,
            INavigationService navigation, Func<ActiveDocumentIdentity> activeDocument)
        {
            _changes = changes;
            var groups = new List<ChangeDocumentViewModel>();
            foreach (var change in changes?.Documents ?? new List<ReportDocumentChanges>())
            {
                if (change == null) continue;
                var doc = documents?.FirstOrDefault(d => d != null && d.Ref == change.Doc);
                groups.Add(new ChangeDocumentViewModel(doc, change, navigation, activeDocument));
                AddedCount += change.Added?.Count ?? 0;
                ModifiedCount += change.Modified?.Count ?? 0;
                DeletedCount += change.Deleted?.Count ?? 0;
            }
            Documents = groups;
        }

        public bool HasData => _changes != null;
        public int AddedCount { get; }
        public int ModifiedCount { get; }
        public int DeletedCount { get; }
        public IReadOnlyList<ChangeDocumentViewModel> Documents { get; }
        public string ScopeText => !HasData ? "" : L.T(_changes.Scope == ReportChanges.ProcessScope ? "reports.changes.scopeProcess"
            : _changes.Scope == "client" ? "reports.changes.scopeClient" : _changes.Scope == "calls" ? "reports.changes.scopeCalls" : "reports.changes.scopeUnknown");
        public string NoteText => !HasData ? L.T("reports.changes.noData")
            : AddedCount + ModifiedCount + DeletedCount == 0 ? L.T("reports.changes.empty") : "";
        public string IncompleteText => HasData && !_changes.Complete ? L.T("reports.changes.incomplete") : "";
        public string TruncatedText => HasData && _changes.Truncated ? L.T("reports.changes.truncated") : "";
        public string ChipText => !HasData ? "" : L.T("reports.changes.chip", ("added", AddedCount),
            ("modified", ModifiedCount), ("deleted", DeletedCount));
        public void UpdateActiveDocument() { foreach (var document in Documents) document.UpdateActiveDocument(); }
    }

    public sealed class ChangeDocumentViewModel : ObservableObject
    {
        private readonly ReportDocument _document;
        private readonly Func<ActiveDocumentIdentity> _activeDocument;
        private bool _isLive;

        public ChangeDocumentViewModel(ReportDocument document, ReportDocumentChanges changes,
            INavigationService navigation, Func<ActiveDocumentIdentity> activeDocument)
        {
            _document = document;
            _activeDocument = activeDocument ?? throw new ArgumentNullException(nameof(activeDocument));
            _isLive = ReportDocumentMatch.Matches(document, activeDocument());
            Added = CreateItems(changes.Added, false, navigation);
            Modified = CreateItems(changes.Modified, false, navigation);
            Deleted = CreateItems(changes.Deleted, true, navigation);
        }

        public string Title => _document?.Title ?? "";
        public bool IsLive => _isLive;
        public IReadOnlyList<ChangeItemViewModel> Added { get; }
        public IReadOnlyList<ChangeItemViewModel> Modified { get; }
        public IReadOnlyList<ChangeItemViewModel> Deleted { get; }
        public void UpdateActiveDocument()
        {
            SetProperty(ref _isLive, ReportDocumentMatch.Matches(_document, _activeDocument()), nameof(IsLive));
            foreach (var item in Added) item.UpdateActiveDocument();
            foreach (var item in Modified) item.UpdateActiveDocument();
            foreach (var item in Deleted) item.UpdateActiveDocument();
        }
        private IReadOnlyList<ChangeItemViewModel> CreateItems(List<ReportChangedElement> elements, bool deleted, INavigationService navigation)
        {
            return (elements ?? new List<ReportChangedElement>()).Where(e => e != null)
                .Select(e => new ChangeItemViewModel(_document, e, deleted, navigation, _activeDocument)).ToList();
        }
    }

    public sealed class ChangeItemViewModel : ObservableObject
    {
        private readonly ReportLinkViewModel _link;
        public ChangeItemViewModel(ReportDocument document, ReportChangedElement element, bool deleted,
            INavigationService navigation, Func<ActiveDocumentIdentity> activeDocument)
        {
            Id = element.Id;
            Category = deleted ? null : element.Category;
            IsDeleted = deleted;
            if (!deleted)
            {
                _link = new ReportLinkViewModel(document, new ReportLink { Elements = new List<ReportElement> {
                    new ReportElement { Id = element.Id, UniqueId = element.UniqueId, Category = element.Category, Kind = ReportElementKinds.Element } } }, navigation, activeDocument);
                _link.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(ReportLinkViewModel.IsLive)) OnPropertyChanged(nameof(IsLive)); };
            }
            OpenCommand = deleted ? new RelayCommand(() => { }, () => false) : _link.OpenCommand;
        }
        public long Id { get; }
        public string Category { get; }
        public bool IsDeleted { get; }
        public bool IsLive => _link != null && _link.IsLive;
        public RelayCommand OpenCommand { get; }
        public void UpdateActiveDocument() { _link?.UpdateActiveDocument(); }
    }
}
