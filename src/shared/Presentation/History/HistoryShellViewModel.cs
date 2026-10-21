using System;
using System.ComponentModel;

namespace RvtMcp.Plugin.Presentation.History
{
    public enum HistoryTab { Activities, Issues }

    public sealed class HistoryShellViewModel : ObservableObject, IDisposable
    {
        private HistoryTab _selectedTab;
        private bool _disposed;
        public HistoryShellViewModel(ActivityHistoryViewModel activities, IHistoryTabState issues)
        {
            Activities = activities ?? throw new ArgumentNullException(nameof(activities));
            Issues = issues ?? throw new ArgumentNullException(nameof(issues));
            Activities.PropertyChanged += OnChildChanged;
            Issues.PropertyChanged += OnChildChanged;
        }
        public ActivityHistoryViewModel Activities { get; }
        public IHistoryTabState Issues { get; }
        public HistoryTab SelectedTab
        {
            get => _selectedTab;
            set { if (SetProperty(ref _selectedTab, value)) NotifyStatus(); }
        }
        private IHistoryTabState Active => SelectedTab == HistoryTab.Issues ? Issues : Activities;
        public string StatusText => Active.StatusText;
        public UiTone StatusTone => Active.StatusTone;
        private void OnChildChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!_disposed && ReferenceEquals(sender, Active) && (e.PropertyName == nameof(StatusText) || e.PropertyName == nameof(StatusTone))) NotifyStatus();
        }
        private void NotifyStatus() { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusTone)); }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Activities.PropertyChanged -= OnChildChanged;
            Issues.PropertyChanged -= OnChildChanged;
            Activities.Dispose();
            Issues.Dispose();
        }
    }
}
