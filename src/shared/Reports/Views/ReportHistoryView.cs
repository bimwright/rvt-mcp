using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Views;
using RvtMcp.Plugin.Reports.Presentation;

namespace RvtMcp.Plugin.Reports.Views
{
    /// <summary>Lazy, bounded read-only History pages. SQLite work never blocks the Revit dispatcher.</summary>
    internal sealed class ReportHistoryView : Expander, IDisposable
    {
        private readonly ReportsViewModel _vm;
        private readonly StackPanel _content = new StackPanel();
        private ReportDetailViewModel _detail;
        private int _generation;
        private bool _disposed, _loading;
        internal ReportHistoryView(ReportsViewModel vm)
        {
            _vm = vm;
            Style = (Style)ReportsStyles.Dictionary[ReportsStyles.ExpanderKey];
            Header = L.T("reports.history.title"); Tag = "ReportHistory";
            Content = _content; Margin = new Thickness(0, 10, 0, 0);
            Expanded += OnExpanded; Collapsed += OnCollapsed;
            Loaded += OnLoaded; Unloaded += OnUnloaded;
        }
        internal void SetDetail(ReportDetailViewModel detail)
        {
            _generation++; _loading = false; _detail = detail; _content.Children.Clear();
            IsExpanded = detail?.HistoryExpanded == true;
            if (IsExpanded && IsLoaded) Load();
        }
        private void OnExpanded(object sender, RoutedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, this)) return;
            if (_detail != null) _detail.HistoryExpanded = true;
            if (IsLoaded) Load();
        }
        private void OnCollapsed(object sender, RoutedEventArgs e) { if (_detail != null) _detail.HistoryExpanded = false; }
        private void OnLoaded(object sender, RoutedEventArgs e) { if (IsExpanded) Load(); }
        private void OnUnloaded(object sender, RoutedEventArgs e) { _generation++; _loading = false; }
        private async void Load()
        {
            if (_disposed || _loading || _detail == null) return;
            _loading = true; var generation = ++_generation; var detail = _detail;
            var dispatcher = Dispatcher;
            var reportId = detail.Report.ReportId;
            var callOffset = detail.HistoryCallOffset; var elementOffset = detail.HistoryElementOffset;
            _content.Children.Clear(); Text(L.T("reports.history.loading"));
            JObject page;
            try { page = await Task.Run(() => _vm.LoadHistory(reportId, callOffset, elementOffset)).ConfigureAwait(false); }
            catch { page = new JObject { ["status"] = "unavailable" }; }
            if (dispatcher.HasShutdownStarted) return;
            _ = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_disposed || generation != _generation || !IsLoaded) return;
                _loading = false;
                Render(page);
            }));
        }
        private void Render(JObject page)
        {
            _content.Children.Clear();
            var calls = page?["calls"] as JArray;
            if (calls == null || calls.Count == 0) Text(Status(page?.Value<string>("status")));
            else foreach (var token in calls)
            {
                var call = token as JObject;
                if (call == null) continue;
                Text((call.Value<string>("tool") ?? "") + " · " + call.Value<string>("call_id"));
                Text(Status(call.Value<string>("status")));
                if (call["model_key"]?.Type == JTokenType.String) Text(L.T("reports.history.model") + ": " + call.Value<string>("model_key"));
                if (call.Value<string>("status") != "available") continue;
                if (call.Value<bool?>("complete") == false) Text(L.T("reports.changes.incomplete"));
                if (call.Value<bool?>("success") == false) Text(L.T("reports.history.callFailed"));
                Text(L.T("reports.history.record") + ": " + (call.Value<string>("record_id") ?? "—"));
                Text(L.T("reports.history.reason") + ": " + Status(call.Value<string>("reason_status")));
                if (call["reason"] != null && call["reason"].Type != JTokenType.Null) Text(Value(call["reason"]));
                foreach (var element in call["elements"] as JArray ?? new JArray())
                {
                    Text(element.Value<string>("kind") + " · " + element.Value<long>("id") + " · " + element.Value<string>("category"));
                    Text(L.T("reports.history.before") + ": " + Value(element["before"]));
                    Text(L.T("reports.history.after") + ": " + Value(element["after"]));
                }
            }
            var actions = new WrapPanel(); _content.Children.Add(actions);
            Add(actions, "reports.history.refresh", true, Load);
            Add(actions, "reports.history.previousCall", _detail.HistoryCallOffset > 0, () => { _detail.HistoryCallOffset--; _detail.HistoryElementOffset = 0; Load(); });
            Add(actions, "reports.history.nextCall", page?["next_call_offset"]?.Type == JTokenType.Integer,
                () => { _detail.HistoryCallOffset++; _detail.HistoryElementOffset = 0; Load(); });
            Add(actions, "reports.history.previousElements", _detail.HistoryElementOffset > 0,
                () => { _detail.HistoryElementOffset = Math.Max(0, _detail.HistoryElementOffset - 10); Load(); });
            Add(actions, "reports.history.nextElements", calls?.First?["next_element_offset"]?.Type == JTokenType.Integer,
                () => { _detail.HistoryElementOffset += 10; Load(); });
        }
        private static string Status(string code) => L.T("reports.history.status." + (code ?? "unavailable"));
        private static string Value(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return L.T("reports.history.notCaptured");
            var text = value.ToString(Formatting.None);
            return text.Length <= 2048 ? text : text.Substring(0, 2048) + "… " + L.T("reports.history.valueTruncated");
        }
        private void Text(string value) => _content.Children.Add(new TextBlock { Text = value ?? "", TextWrapping = TextWrapping.Wrap,
            Foreground = ReportsStyles.TextSecondary, FontSize = 12, Margin = new Thickness(4, 4, 4, 0) });
        private static void Add(Panel panel, string key, bool enabled, Action action)
        {
            var button = new Button { Style = (Style)ReportsStyles.Dictionary[BimwrightStyles.ToolbarButtonKey], Content = L.T(key), IsEnabled = enabled, Margin = new Thickness(3), Padding = new Thickness(6, 3, 6, 3) };
            button.Click += (s, e) => action(); panel.Children.Add(button);
        }
        public void Dispose()
        {
            _disposed = true; _generation++; _detail = null;
            Expanded -= OnExpanded; Collapsed -= OnCollapsed; Loaded -= OnLoaded; Unloaded -= OnUnloaded;
        }
    }
}
