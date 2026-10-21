using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Presentation;
using RvtMcp.Plugin.Presentation.History;

namespace RvtMcp.Plugin.Views
{
    public sealed partial class HistoryWindow : Window, IDisposable
    {
        private readonly HistoryShellViewModel _vm;
        private readonly ActivityHistoryView _activitiesView;
        private readonly FailureIssuesView _issuesView;
        private readonly TabControl _tabs;
        private readonly TabItem _activityTab, _issuesTab;
        private readonly TextBlock _shellStatus;
        private readonly Button _closeButton;
        private bool _shutdown, _disposed;

        public HistoryWindow(IHistoryRuntime runtime, IHistoryDialogs dialogs = null)
            : this(runtime, dialogs, new FailureIssuesView()) { }

        internal HistoryWindow(IHistoryRuntime runtime, IHistoryDialogs dialogs, FailureIssuesView issuesView)
        {
            _issuesView = issuesView ?? throw new ArgumentNullException(nameof(issuesView));
            _vm = new HistoryShellViewModel(new ActivityHistoryViewModel(runtime,
                dialogs ?? new Infrastructure.UI.HistoryDialogs(this)), _issuesView.ViewModel);
            DataContext = _vm;
            var area = SystemParameters.WorkArea;
            MinWidth = Math.Min(960, area.Width);
            MinHeight = Math.Min(640, area.Height);
            MaxWidth = Math.Max(MinWidth, Math.Min(1400, area.Width));
            MaxHeight = Math.Max(MinHeight, Math.Min(900, area.Height));
            Width = Math.Min(Math.Max(1100, MinWidth), MaxWidth);
            Height = Math.Min(Math.Max(720, MinHeight), MaxHeight);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI");
            Resources.MergedDictionaries.Add(BimwrightStyles.Dictionary);
            Resources.MergedDictionaries.Add(HistoryChrome.TabDictionary());
            ScrollBarFadeBehavior.SetIsEnabled(this, true);
            _activitiesView = new ActivityHistoryView(_vm.Activities);
            _activityTab = new TabItem { Content = _activitiesView };
            _issuesTab = new TabItem { Content = _issuesView };
            _tabs = new TabControl();
            _tabs.Items.Add(_activityTab);
            _tabs.Items.Add(_issuesTab);
            _tabs.SetBinding(TabControl.SelectedIndexProperty, new Binding(nameof(HistoryShellViewModel.SelectedTab))
                { Source = _vm, Mode = BindingMode.TwoWay, Converter = new TabIndexConverter() });
            _shellStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(0, 0, 12, 0) };
            _shellStatus.SetBinding(TextBlock.TextProperty, new Binding(nameof(HistoryShellViewModel.StatusText)) { Source = _vm });
            _shellStatus.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(HistoryShellViewModel.StatusTone)) { Source = _vm, Converter = new ToneConverter() });
            _closeButton = new Button { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            _closeButton.Click += (s, e) => Close();
            var closePanel = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_closeButton, Dock.Right);
            closePanel.Children.Add(_closeButton);
            closePanel.Children.Add(_shellStatus);
            var closeBar = new Border { Background = FrozenBrush(0xF7, 0xF8, 0xFA), BorderBrush = FrozenBrush(0xE0, 0xE0, 0xE0),
                BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(8, 6, 8, 8), Child = closePanel };
            var shell = new Grid();
            shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            shell.Children.Add(_tabs);
            Grid.SetRow(closeBar, 1);
            shell.Children.Add(closeBar);
            Content = shell;
            _vm.PropertyChanged += OnShellChanged;
            _vm.Activities.PropertyChanged += OnActivitiesChanged;
            IsVisibleChanged += OnVisibleChanged;
            Closed += (s, e) => Dispose();
            ApplyLocalization();
        }

        public void ShowIssues()
        {
            if (_vm.SelectedTab == HistoryTab.Issues)
            {
                if (_issuesView.IsLoaded) _issuesView.Refresh();
            }
            else _vm.SelectedTab = HistoryTab.Issues;
        }
        private void OnActivitiesChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!_disposed && e.PropertyName == nameof(ActivityHistoryViewModel.LanguageVersion)) ApplyLocalization();
        }
        private void OnShellChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_disposed || e.PropertyName != nameof(HistoryShellViewModel.StatusText)) return;
            if (string.IsNullOrEmpty(_vm.StatusText)) _shellStatus.ToolTip = null;
            else HistoryChrome.Guide(_shellStatus, _vm.StatusText);
        }
        private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_disposed) return;
            if (IsVisible) { _vm.Activities.Activate(); ApplyLocalization(); }
            else _vm.Activities.Deactivate();
        }
        private void ApplyLocalization()
        {
            Title = L.T("history.window.title", ("brand", BrandAssets.Wordmark));
            _activityTab.Header = L.T("failure.tab.history");
            _issuesTab.Header = L.T("failure.tab.issues");
            _closeButton.Content = L.T("update.close");
        }
        public void CloseForShutdown() { _shutdown = true; Close(); }
        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_shutdown) { e.Cancel = true; Hide(); }
            base.OnClosing(e);
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            IsVisibleChanged -= OnVisibleChanged;
            _vm.PropertyChanged -= OnShellChanged;
            _vm.Activities.PropertyChanged -= OnActivitiesChanged;
            _activitiesView.Dispose();
            _issuesView.Dispose();
            BindingOperations.ClearAllBindings(_tabs);
            BindingOperations.ClearAllBindings(_shellStatus);
            DataContext = null;
            _vm.Dispose();
        }
        private static SolidColorBrush FrozenBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze(); return brush;
        }
        private sealed class TabIndexConverter : IValueConverter
        {
            public object Convert(object value, Type type, object parameter, System.Globalization.CultureInfo culture) => (int)(HistoryTab)value;
            public object ConvertBack(object value, Type type, object parameter, System.Globalization.CultureInfo culture) =>
                value is int index && index >= 0 && index <= 1 ? (object)(HistoryTab)index : Binding.DoNothing;
        }
        private sealed class ToneConverter : IValueConverter
        {
            public object Convert(object value, Type type, object parameter, System.Globalization.CultureInfo culture) => Equals(value, UiTone.Error) ? Brushes.Firebrick : Equals(value, UiTone.Review) ? FrozenBrush(0x9A, 0x34, 0x12) : Brushes.DimGray;
            public object ConvertBack(object value, Type type, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
        }
    }
}
