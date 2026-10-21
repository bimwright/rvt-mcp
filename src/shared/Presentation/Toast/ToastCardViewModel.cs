using System;
using System.Collections.Generic;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Views.Toast;

namespace RvtMcp.Plugin.Presentation.Toast
{
    /// <summary>Presentation of one immutable aggregator snapshot. Owns no timing, images or windows.</summary>
    internal sealed class ToastCardViewModel : ObservableObject, IDisposable
    {
        private ActivitySnapshot _snapshot;
        private Action<long> _dismiss, _click;
        private bool _disposed;
        private string _title, _body;
        private static readonly IReadOnlyList<ToastActivityEntry> Empty = Array.AsReadOnly(new ToastActivityEntry[0]);

        internal ToastCardViewModel(ActivitySnapshot snapshot, string identityTitle, Action<long> dismiss, Action<long> click)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            CardId = snapshot.CardId;
            IdentityTitle = identityTitle;
            _dismiss = dismiss; _click = click;
            DismissCommand = new RelayCommand(() => _dismiss?.Invoke(CardId), () => !_disposed);
            ClickCommand = new RelayCommand(() => _click?.Invoke(CardId), () => !_disposed);
            Apply(snapshot);
        }
        public long CardId { get; }
        public string IdentityTitle { get; }
        public McpToastViewModel Payload { get; private set; }
        public RelayCommand DismissCommand { get; }
        public RelayCommand ClickCommand { get; }
        public bool IsStatus => _snapshot?.IsStatus == true;
        public bool ShowCounters => !IsStatus;
        public string Title => _title;
        public string Body => _body;
        public bool HasFailure => _snapshot?.HasFailure == true;
        public string Icon => _snapshot?.LatestSuccess == true ? "\uE73E" : "\uE783";
        public int Succeeded => _snapshot?.Succeeded ?? 0;
        public int Failed => _snapshot?.Failed ?? 0;
        public int Images => _snapshot?.Images ?? 0;
        public string ThumbnailPath => IsStatus ? null : _snapshot?.ImagePath;
        public IReadOnlyList<ToastActivityEntry> RecentEntries => _snapshot?.RecentEntries ?? Empty;
        public string SuccessLabel => LocalizedOrFallback("toast.activity.success", "Success");
        public string FailedLabel => LocalizedOrFallback("toast.activity.failed", "Failed");
        public string OpenImageLabel => LocalizedOrFallback("toast.activity.open_image", "Open image");
        public string CaptureLabel => LocalizedOrFallback("toast.activity.capture", "Capture");
        public string CounterAutomationText => $"{Succeeded} {SuccessLabel}, {Failed} {FailedLabel}, {Images} {CaptureLabel}";
        public string ToolTip => IsStatus ? Title + "\n" + Body : RecentEntries.Count > 0
            ? RecentEntries[RecentEntries.Count - 1].TooltipText
            : ToastActivityEntry.Compact(_snapshot?.Title, ToastActivityEntry.TitleLimit) + "\n"
                + ToastActivityEntry.Compact(_snapshot?.Body, ToastActivityEntry.BodyLimit);

        public void Apply(ActivitySnapshot snapshot)
        {
            if (_disposed || snapshot == null || snapshot.CardId != CardId) return;
            _snapshot = snapshot;
            Payload = new McpToastViewModel { Title = snapshot.Title, Summary = snapshot.Body,
                Success = snapshot.LatestSuccess && !snapshot.HasFailure, Kind = ToolActivityKind.Read };
            RefreshLocalization();
        }
        public void RefreshLocalization()
        {
            if (_disposed || _snapshot == null) return;
            var text = IsStatus ? ResolveStatusText(_snapshot) : new ActivityStatusText(_snapshot.Title, _snapshot.Body);
            _title = text.Title ?? string.Empty;
            _body = text.Body ?? string.Empty;
            // Publish only after the whole projection is ready, so bindings see a coherent card.
            OnPropertyChanged(string.Empty);
        }
        private static ActivityStatusText ResolveStatusText(ActivitySnapshot snapshot)
        {
            if (snapshot.StatusTextProvider != null)
            {
                try
                {
                    var localized = snapshot.StatusTextProvider();
                    if (localized != null) return localized;
                }
                catch { /* Keep the captured copy if localization fails. */ }
            }
            return new ActivityStatusText(snapshot.Title, snapshot.Body);
        }
        private static string LocalizedOrFallback(string key, string fallback)
        {
            var value = L.T(key);
            return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal) ? fallback : value;
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _snapshot = null;
            _dismiss = _click = null;
            DismissCommand.RaiseCanExecuteChanged(); ClickCommand.RaiseCanExecuteChanged();
        }
    }
}
