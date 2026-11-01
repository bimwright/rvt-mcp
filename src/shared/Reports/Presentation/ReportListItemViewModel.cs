using System;
using System.Globalization;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Presentation;
using RvtMcp.Plugin.Reports.Model;

namespace RvtMcp.Plugin.Reports.Presentation
{
    public sealed class ReportListItemViewModel : ObservableObject
    {
        private readonly Func<ActiveDocumentIdentity> _activeDocument;
        private readonly Func<DateTime> _now;
        private bool _isOtherModel;
        private string _dayHeading = "", _compactTime = "", _timeTooltip = "", _updatedText = "";
        public ReportListItemViewModel(AgentReport report, Func<ActiveDocumentIdentity> activeDocument, Func<DateTime> now)
        {
            Report = report ?? throw new ArgumentNullException(nameof(report));
            _activeDocument = activeDocument ?? throw new ArgumentNullException(nameof(activeDocument));
            _now = now ?? throw new ArgumentNullException(nameof(now));
            _isOtherModel = !ReportPresentationText.Matches(Report, _activeDocument());
            CreatedInstant = ReportCalendar.Parse(report.CreatedUtc);
            RefreshCalendar(_now(), TimeZoneInfo.Local);
        }
        public AgentReport Report { get; }
        public string ReportId => Report.ReportId;
        public string Summary => Report.Summary;
        public string Outcome => Report.Outcome;
        public string OutcomeLabel => ReportPresentationText.Outcome(Outcome);
        public string TimeText => ReportPresentationText.Time(Report.CreatedUtc, _now());
        internal DateTimeOffset? CreatedInstant { get; }
        internal DateTime? LocalDay { get; private set; }
        public string DayHeading => _dayHeading;
        public bool HasDayHeading => !string.IsNullOrEmpty(_dayHeading);
        public string CompactTimeText => _compactTime;
        public string TimeTooltip => _timeTooltip;
        public string UpdatedText => _updatedText;
        public bool IsUpdated => !string.IsNullOrEmpty(_updatedText);
        internal void SetDayHeading(string text)
        {
            if (SetProperty(ref _dayHeading, text, nameof(DayHeading))) OnPropertyChanged(nameof(HasDayHeading));
        }
        internal void RefreshCalendar(DateTime now, TimeZoneInfo zone)
        {
            var created = ReportCalendar.Local(CreatedInstant, zone);
            LocalDay = created?.Date;
            SetProperty(ref _compactTime, created?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "—", nameof(CompactTimeText));
            SetProperty(ref _timeTooltip, ReportCalendar.Tooltip(CreatedInstant, zone), nameof(TimeTooltip));
            var updated = ReportCalendar.Parse(Report.UpdatedUtc);
            var text = CreatedInstant.HasValue && updated.HasValue && updated.Value != CreatedInstant.Value
                ? L.T("reports.time.updated", ("time", ReportCalendar.Local(updated, zone).Value.ToString("g", ReportCalendar.Culture))) : "";
            if (SetProperty(ref _updatedText, text, nameof(UpdatedText))) OnPropertyChanged(nameof(IsUpdated));
            OnPropertyChanged(nameof(TimeText));
        }
        public string ModelName => ReportPresentationText.ModelName(Report);
        public bool IsOtherModel => _isOtherModel;
        public void UpdateActiveDocument() => SetProperty(ref _isOtherModel,
            !ReportPresentationText.Matches(Report, _activeDocument()), nameof(IsOtherModel));
    }
}
