using System;
using System.Collections.Generic;
using RvtMcp.Plugin.Presentation;
using RvtMcp.Plugin.Reports.Model;

namespace RvtMcp.Plugin.Reports.Presentation
{
    public enum ReportLinkTargetKind { Element, View, Sheet }

    public sealed class ReportLinkViewModel : ObservableObject
    {
        private readonly ReportDocument _document;
        private readonly ReportLink _link;
        private readonly INavigationService _navigation;
        private readonly Func<ActiveDocumentIdentity> _activeDocument;
        private bool _isLive;

        public ReportLinkViewModel(ReportDocument document, ReportLink link, INavigationService navigation,
            Func<ActiveDocumentIdentity> activeDocument)
        {
            _document = document;
            _link = link ?? throw new ArgumentNullException(nameof(link));
            _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
            _activeDocument = activeDocument ?? throw new ArgumentNullException(nameof(activeDocument));
            _isLive = ReportDocumentMatch.Matches(_document, _activeDocument());
            OpenCommand = new RelayCommand(Open, () => IsLive);
        }

        public string Label => _link.Label;
        public bool IsLive => _isLive;
        public string DimTooltip => _document?.Title ?? "";
        public int ElementCount => _link.Elements?.Count ?? 0;
        public IReadOnlyList<ReportElement> Elements => _link.Elements ?? (IReadOnlyList<ReportElement>)new ReportElement[0];
        public long? ViewHintId => _link.View?.Id;
        public ReportLinkTargetKind TargetKind
        {
            get
            {
                var kind = _link.Elements != null && _link.Elements.Count > 0 ? _link.Elements[0]?.Kind : null;
                return kind == ReportElementKinds.View ? ReportLinkTargetKind.View
                    : kind == ReportElementKinds.Sheet ? ReportLinkTargetKind.Sheet : ReportLinkTargetKind.Element;
            }
        }
        public RelayCommand OpenCommand { get; }

        public void UpdateActiveDocument()
        {
            if (SetProperty(ref _isLive, ReportDocumentMatch.Matches(_document, _activeDocument()), nameof(IsLive)))
                OpenCommand.RaiseCanExecuteChanged();
        }

        private void Open()
        {
            if (!ReportDocumentMatch.Matches(_document, _activeDocument())) return;
            _navigation.Navigate(new ReportNavigationRequest
            {
                Document = _document, Elements = _link.Elements ?? new List<ReportElement>(), ViewHint = _link.View
            });
        }
    }
}
