using System;
using RvtMcp.Plugin.Presentation;
using RvtMcp.Plugin.Reports.Model;

namespace RvtMcp.Plugin.Reports.Presentation
{
    public sealed class ReportListItemViewModel : ObservableObject
    {
        private readonly Func<ActiveDocumentIdentity> _activeDocument;
        private readonly Func<DateTime> _now;
        private bool _isOtherModel;
        public ReportListItemViewModel(AgentReport report, Func<ActiveDocumentIdentity> activeDocument, Func<DateTime> now)
        {
            Report = report ?? throw new ArgumentNullException(nameof(report));
            _activeDocument = activeDocument ?? throw new ArgumentNullException(nameof(activeDocument));
            _now = now ?? throw new ArgumentNullException(nameof(now));
            _isOtherModel = !ReportPresentationText.Matches(Report, _activeDocument());
        }
        public AgentReport Report { get; }
        public string ReportId => Report.ReportId;
        public string Summary => Report.Summary;
        public string Outcome => Report.Outcome;
        public string OutcomeLabel => ReportPresentationText.Outcome(Outcome);
        public string TimeText => ReportPresentationText.Time(Report.CreatedUtc, _now());
        public string ModelName => ReportPresentationText.ModelName(Report);
        public bool IsOtherModel => _isOtherModel;
        public void UpdateActiveDocument() => SetProperty(ref _isOtherModel,
            !ReportPresentationText.Matches(Report, _activeDocument()), nameof(IsOtherModel));
    }
}
