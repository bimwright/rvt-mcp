using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Reports.Presentation;
using RvtMcp.Plugin.Views;

namespace RvtMcp.Plugin.Reports.Views
{
    internal sealed class ReportCopyButton : Button, IDisposable
    {
        private readonly ReportsViewModel _vm;
        private readonly DispatcherTimer _timer;
        private bool _subscribed;
        public ReportCopyButton(ReportsViewModel vm)
        {
            _vm = vm;
            Style = (Style)ReportsStyles.Dictionary[BimwrightStyles.IconButtonKey];
            Command = vm.CopyCommand;
            Margin = new Thickness(3, 2, 0, 2);
            VerticalAlignment = VerticalAlignment.Center;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _timer.Tick += Reset;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            Reset(null, EventArgs.Empty);
        }
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_subscribed) return;
            _vm.PropertyChanged += OnChanged;
            _subscribed = true;
        }
        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_subscribed) _vm.PropertyChanged -= OnChanged;
            _subscribed = false;
            Reset(null, EventArgs.Empty);
        }
        private void OnChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ReportsViewModel.CopyAttempt)) return;
            _timer.Stop();
            Content = _vm.LastCopySucceeded ? "✓" : "!";
            FontFamily = ReportsStyles.Ui;
            Foreground = _vm.LastCopySucceeded ? ReportAssets.Green : ReportsStyles.WarningText;
            ToolTip = L.T(_vm.LastCopySucceeded ? "reports.status.copied" : "reports.status.copyFailed");
            AutomationProperties.SetName(this, (string)ToolTip);
            _timer.Start();
        }
        private void Reset(object sender, EventArgs e)
        {
            _timer.Stop();
            Content = "";
            ClearValue(FontFamilyProperty);
            ClearValue(ForegroundProperty);
            ToolTip = L.T("reports.copy.button");
            AutomationProperties.SetName(this, (string)ToolTip);
        }
        public void Dispose()
        {
            OnUnloaded(null, null);
            _timer.Tick -= Reset;
            Loaded -= OnLoaded;
            Unloaded -= OnUnloaded;
            Command = null;
        }
    }
}
