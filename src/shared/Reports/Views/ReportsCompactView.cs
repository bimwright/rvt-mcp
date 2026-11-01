using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Reports.Presentation;
using RvtMcp.Plugin.Views;

namespace RvtMcp.Plugin.Reports.Views
{
    // The plugin shells also compile RvtMcp.Plugin.Reports.Binding; name the WPF type explicitly.
    using Binding = System.Windows.Data.Binding;

    /// <summary>
    /// Compact mode: one column of report cards; the selected card opens in place. Only the open card
    /// has a body, and that body is kept for its detail object so scrolling it out of view and back
    /// does not rebuild the document.
    /// </summary>
    internal sealed class ReportsCompactView : DockPanel, IDisposable
    {
        public static readonly DependencyProperty OwnerProperty = DependencyProperty.RegisterAttached("Owner", typeof(ReportsCompactView),
            typeof(ReportsCompactView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

        private readonly ReportsViewModel _vm;
        private readonly ComboBox _filter;
        private readonly TextBlock _count, _empty;
        private readonly ListBox _feed;
        private readonly Button _newReportsButton;
        private readonly DispatcherTimer _calendarTimer;
        private readonly HashSet<string> _newReports = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<ReportCard> _cards = new HashSet<ReportCard>();
        private CompactCardBody _body;
        private ReadingPosition _reading;
        private HashSet<string> _beforeIds;
        private int _restoreVersion;
        private bool _syncingFilter, _disposed;

        private sealed class ReadingPosition
        {
            public string Id;
            public double Top, Offset;
            public bool AtTop, Body;
            public List<string> Neighbours;
            public IInputElement Focused;
            public string FocusedReport;
        }

        public ReportsCompactView(ReportsViewModel viewModel)
        {
            _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _filter = new ComboBox { FontSize = 12, MinWidth = 140, VerticalAlignment = VerticalAlignment.Center };
            _filter.Items.Add(L.T("reports.filter.activeModel"));
            _filter.Items.Add(L.T("reports.filter.all"));
            AutomationProperties.SetName(_filter, L.T("reports.filter.label"));
            _filter.SelectionChanged += OnFilterSelected;
            _count = new TextBlock { FontSize = 11.5, Foreground = ReportsStyles.TextSecondary, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(_count, Dock.Right);
            var headPanel = new DockPanel();
            headPanel.Children.Add(_count);
            headPanel.Children.Add(new Border { Child = _filter, HorizontalAlignment = HorizontalAlignment.Left });
            var head = new Border { Padding = new Thickness(10, 8, 10, 8), Background = ReportsStyles.Side, BorderBrush = ReportsStyles.BorderBrush,
                BorderThickness = new Thickness(0, 0, 0, 1), Child = headPanel };
            SetDock(head, Dock.Top);
            Children.Add(head);

            var template = new FrameworkElementFactory(typeof(StackPanel));
            template.AppendChild(new FrameworkElementFactory(typeof(ReportDayHeader)));
            template.AppendChild(new FrameworkElementFactory(typeof(ReportCard)));
            _feed = new ListBox
            {
                ItemsSource = _vm.Reports, Background = ReportsStyles.Feed, BorderThickness = new Thickness(0), Padding = new Thickness(8),
                ItemContainerStyle = (Style)ReportsStyles.Dictionary[ReportsStyles.FeedItemKey],
                ItemTemplate = new DataTemplate { VisualTree = template },
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Focusable = false
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(_feed, ScrollBarVisibility.Disabled);
            ScrollViewer.SetCanContentScroll(_feed, true);
            VirtualizingPanel.SetIsVirtualizing(_feed, true);
            VirtualizingPanel.SetScrollUnit(_feed, ScrollUnit.Pixel);
            KeyboardNavigation.SetTabNavigation(_feed, KeyboardNavigationMode.Continue);
            AutomationProperties.SetName(_feed, L.T("reports.list.label"));
            _feed.SetValue(OwnerProperty, this);
            _empty = new TextBlock { Margin = new Thickness(16, 24, 16, 16), Foreground = ReportsStyles.TextSecondary, TextWrapping = TextWrapping.Wrap };
            var feedArea = new Grid { Background = ReportsStyles.Feed };
            feedArea.Children.Add(ReportsScrolling.Smooth(_feed, 0.5));
            feedArea.Children.Add(_empty);
            _newReportsButton = new Button { Style = (Style)ReportsStyles.Dictionary[ReportsStyles.ChipButtonKey],
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8, 6, 8, 0), Visibility = Visibility.Collapsed, Tag = "NewReportsButton" };
            _newReportsButton.Click += OnNewReportsClicked;
            // Overlay the feed: showing the notice must not resize the reading viewport.
            feedArea.Children.Add(_newReportsButton);
            Children.Add(feedArea);

            _vm.PropertyChanged += OnViewModelChanged;
            _vm.Reports.CollectionChanged += OnReportsChanged;
            _vm.ListUpdating += OnListUpdating;
            _vm.ListUpdated += OnListUpdated;
            _vm.RevealRequested += OnRevealRequested;
            _feed.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
            _calendarTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _calendarTimer.Tick += OnCalendarTick;
            Loaded += OnLoaded;
            IsVisibleChanged += OnVisibleChanged;
            SyncFilter();
            SyncList();
        }

        internal ReportsViewModel ViewModel => _vm;
        internal ComboBox Filter => _filter;
        internal ListBox Feed => _feed;
        internal TextBlock CountText => _count;
        internal TextBlock EmptyTextBlock => _empty;
        internal CompactCardBody OpenBody => _body;

        internal void CaptureReadingPosition()
        {
            if (_disposed) return;
            if (IsLoaded && _reading == null) OnListUpdating(this, new ReportListUpdateEventArgs(false));
            if (_reading != null)
                _vm.CompactReadingPosition = new ReportReadingPosition { SelectedId = _body?.Detail.Report.ReportId,
                    AnchorId = _reading.Id, Top = _reading.Top, Offset = _reading.Offset, Body = _reading.Body,
                    AtTop = _reading.AtTop, Neighbours = _reading.Neighbours.ToList() };
        }

        public void Dispose()
        {
            if (_disposed) return;
            CaptureReadingPosition();
            _disposed = true;
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.Reports.CollectionChanged -= OnReportsChanged;
            _vm.ListUpdating -= OnListUpdating;
            _vm.ListUpdated -= OnListUpdated;
            _vm.RevealRequested -= OnRevealRequested;
            _calendarTimer.Stop();
            _calendarTimer.Tick -= OnCalendarTick;
            _newReportsButton.Click -= OnNewReportsClicked;
            _feed.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
            _restoreVersion++;
            _filter.SelectionChanged -= OnFilterSelected;
            Loaded -= OnLoaded;
            IsVisibleChanged -= OnVisibleChanged;
            _feed.ItemsSource = null;
            _feed.ClearValue(OwnerProperty);
            _cards.Clear();
            _body?.Dispose();
            _body = null;
        }

        internal void Register(ReportCard card) { if (!_disposed) _cards.Add(card); }
        internal void Unregister(ReportCard card) { _cards.Remove(card); }

        /// <summary>Body of the open card; one instance per detail object, moved between card containers as needed.</summary>
        internal CompactCardBody BodyFor(ReportDetailViewModel detail)
        {
            if (_disposed || detail == null) return null;
            if (_body != null && ReferenceEquals(_body.Detail, detail)) return _body;
            _body?.Dispose();
            _body = new CompactCardBody(_vm, detail);
            return _body;
        }

        /// <summary>Card header click, Enter or Space: open the card, or collapse it when it is already open.</summary>
        internal void Toggle(ReportListItemViewModel item)
        {
            if (_disposed || item == null) return;
            _vm.SelectReport(item);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (IsVisible) _calendarTimer.Start();
            var saved = _vm.CompactReadingPosition;
            if (_reading == null && saved != null && saved.SelectedId == _vm.SelectedReport?.ReportId)
                _reading = new ReadingPosition { Id = saved.AnchorId, Top = saved.Top, Offset = saved.Offset,
                    Body = saved.Body, AtTop = saved.AtTop, Neighbours = saved.Neighbours.ToList() };
            if (_reading == null) ScrollToSelection();
            else QueueRestore();
        }

        private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible && IsLoaded)
            {
                _calendarTimer.Start();
                if (_reading != null) QueueRestore();
            }
            else _calendarTimer.Stop();
        }

        private void OnCalendarTick(object sender, EventArgs e) { if (!_disposed && IsVisible) _vm.RefreshDates(); }
        private void OnRevealRequested(object sender, EventArgs e)
        {
            ScrollToSelection();
            if (_vm.SelectedReport != null) Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                new Action(() => { if (!_disposed) { var card = FindCard(_vm.SelectedReport); if (card != null) ReportMotion.FadeIn(card); } }));
        }

        private void OnViewModelChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_disposed) return;
            switch (e.PropertyName)
            {
                case nameof(ReportsViewModel.Filter): SyncFilter(); break;
                case nameof(ReportsViewModel.EmptyText): SyncList(); break;
                case nameof(ReportsViewModel.SelectedReport):
                case nameof(ReportsViewModel.SelectedDetail):
                    RefreshCards();
                    break;
                case nameof(ReportsViewModel.CompactChangesExpanded):
                case nameof(ReportsViewModel.IsDeleteConfirmationPending):
                case nameof(ReportsViewModel.DeleteLabel):
                    _body?.Sync();
                    break;
            }
        }

        private void OnReportsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (!_disposed) SyncList();
        }

        private void OnFilterSelected(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingFilter || _filter.SelectedIndex < 0) return;
            _vm.Filter = _filter.SelectedIndex == 1 ? ReportFilter.All : ReportFilter.ActiveModel;
        }

        private void SyncFilter()
        {
            _syncingFilter = true;
            try { _filter.SelectedIndex = _vm.Filter == ReportFilter.All ? 1 : 0; }
            finally { _syncingFilter = false; }
        }

        private void SyncList()
        {
            var empty = _vm.Reports.Count == 0;
            _count.Text = L.T("reports.list.count", ("count", _vm.Reports.Count));
            _empty.Text = empty ? _vm.EmptyText : "";
            _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            _feed.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        private void RefreshCards()
        {
            foreach (var card in new List<ReportCard>(_cards)) card.Refresh();
            if (_vm.SelectedDetail == null && _body != null)
            {
                _body.Dispose();
                _body = null;
            }
        }

        private void OnListUpdating(object sender, ReportListUpdateEventArgs e)
        {
            if (_disposed || !IsLoaded) return;
            _beforeIds = new HashSet<string>(_vm.Reports.Select(r => r.ReportId), StringComparer.Ordinal);
            if (_reading != null) return;
            var viewer = HistoryChrome.FindScrollViewer(_feed);
            if (viewer == null) return;
            HistoryChrome.StopWheel(viewer);
            var card = _cards.Where(c => c.IsLoaded && c.ActualHeight > 0)
                .Select(c => new { Card = c, Top = c.TranslatePoint(new Point(), viewer).Y })
                .Where(c => c.Top < viewer.ViewportHeight && c.Top + c.Card.ActualHeight > 0)
                .OrderBy(c => c.Top).FirstOrDefault()?.Card;
            var item = card?.DataContext as ReportListItemViewModel;
            var body = item != null && ReferenceEquals(item, _vm.SelectedReport) && _body != null;
            FrameworkElement anchor = body ? (FrameworkElement)_body.Body : card;
            var index = item == null ? 0 : _vm.Reports.IndexOf(item);
            _reading = new ReadingPosition { Id = item?.ReportId, AtTop = viewer.VerticalOffset < 0.5,
                Offset = viewer.VerticalOffset, Body = body, Top = anchor?.TranslatePoint(new Point(), viewer).Y ?? 0,
                Neighbours = _vm.Reports.Skip(index).Concat(_vm.Reports.Take(index).Reverse()).Select(r => r.ReportId).ToList(),
                Focused = _feed.IsKeyboardFocusWithin ? Keyboard.FocusedElement : null,
                FocusedReport = (_cards.FirstOrDefault(c => c.IsKeyboardFocusWithin)?.DataContext as ReportListItemViewModel)?.ReportId };
        }

        private void OnListUpdated(object sender, ReportListUpdateEventArgs e)
        {
            if (_disposed || _reading == null) return;
            var ids = new HashSet<string>(_vm.Reports.Select(r => r.ReportId), StringComparer.Ordinal);
            _newReports.RemoveWhere(id => !ids.Contains(id));
            if (_reading.AtTop) _newReports.Clear();
            else if (e.FromRefresh && _beforeIds?.Count > 0)
                foreach (var id in ids) if (!_beforeIds.Contains(id)) _newReports.Add(id);
            SyncNewReports();
            if (IsVisible) QueueRestore();
        }

        private void QueueRestore()
        {
            var version = ++_restoreVersion;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => RestoreReading(version)));
        }

        private void RestoreReading(int version)
        {
            if (_disposed || !IsVisible || version != _restoreVersion || _reading == null) return;
            var position = _reading;
            var viewer = HistoryChrome.FindScrollViewer(_feed);
            if (viewer == null) { _reading = null; return; }
            HistoryChrome.StopWheel(viewer);
            var item = position.Neighbours.Select(id => _vm.Reports.FirstOrDefault(r => r.ReportId == id)).FirstOrDefault(r => r != null);
            if (position.AtTop)
            {
                _feed.UpdateLayout();
                RestoreFocus(position);
                viewer.ScrollToTop();
            }
            else if (item != null)
            {
                _feed.ScrollIntoView(item);
                _feed.UpdateLayout();
                // A replaced item can be measured before its Loaded callback attaches the open body.
                FindCard(item)?.Refresh();
                _feed.UpdateLayout();
                RestoreFocus(position);
                for (var pass = 0; pass < 3; pass++)
                {
                    var card = FindCard(item);
                    FrameworkElement anchor = position.Body && item.ReportId == position.Id && card?.IsOpen == true ? _body?.Body : card;
                    if (anchor == null) break;
                    var delta = anchor.TranslatePoint(new Point(), viewer).Y - (item.ReportId == position.Id ? position.Top : 0);
                    if (Math.Abs(delta) < 0.5) break;
                    viewer.ScrollToVerticalOffset(viewer.VerticalOffset + delta);
                    _feed.UpdateLayout();
                }
            }
            else viewer.ScrollToVerticalOffset(position.Offset);
            _feed.UpdateLayout();
            _reading = null;
        }

        private ReportCard FindCard(ReportListItemViewModel item) => item == null ? null
            : Descendant<ReportCard>(_feed.ItemContainerGenerator.ContainerFromItem(item));

        private void RestoreFocus(ReadingPosition position)
        {
            if (position.Focused == null || !IsVisible) return;
            var card = FindCard(_vm.Reports.FirstOrDefault(r => r.ReportId == (position.FocusedReport ?? position.Id)));
            card?.Refresh();
            if (card == null || card.IsKeyboardFocusWithin) return;
            if (BelongsTo(position.Focused as DependencyObject, card)) Keyboard.Focus(position.Focused);
            else card.Header.Focus();
        }

        private static bool BelongsTo(DependencyObject element, DependencyObject parent)
        {
            while (element != null)
            {
                if (ReferenceEquals(element, parent)) return true;
                element = element is Visual ? VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element)
                    : LogicalTreeHelper.GetParent(element);
            }
            return false;
        }

        private static T Descendant<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent is T match) return match;
            if (parent == null) return null;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = Descendant<T>(VisualTreeHelper.GetChild(parent, i));
                if (child != null) return child;
            }
            return null;
        }

        private void SyncNewReports()
        {
            var wasVisible = _newReportsButton.Visibility == Visibility.Visible;
            var text = L.T("reports.newReports", ("count", _newReports.Count));
            _newReportsButton.Content = text;
            _newReportsButton.ToolTip = text;
            AutomationProperties.SetName(_newReportsButton, text);
            _newReportsButton.Visibility = _newReports.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!wasVisible && _newReports.Count > 0) ReportMotion.FadeIn(_newReportsButton);
        }

        private void OnNewReportsClicked(object sender, RoutedEventArgs e)
        {
            _restoreVersion++;
            _reading = null;
            _newReports.Clear();
            SyncNewReports();
            var viewer = HistoryChrome.FindScrollViewer(_feed);
            HistoryChrome.StopWheel(viewer);
            viewer?.ScrollToTop();
            _feed.UpdateLayout();
            FindCard(_vm.Reports.FirstOrDefault())?.Header.Focus();
            viewer?.ScrollToTop();
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_reading == null && _newReports.Count > 0 && HistoryChrome.FindScrollViewer(_feed)?.VerticalOffset < 0.5)
            {
                _newReports.Clear();
                SyncNewReports();
            }
        }

        /// <summary>Only an explicit selection/open request reveals the header; list refreshes restore their anchor.</summary>
        private void ScrollToSelection()
        {
            var selected = _vm.SelectedReport;
            if (selected == null || !IsLoaded) return;
            var version = ++_restoreVersion;
            _reading = null;
            HistoryChrome.StopWheel(HistoryChrome.FindScrollViewer(_feed));
            _feed.ScrollIntoView(selected);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (_disposed || version != _restoreVersion || !ReferenceEquals(_vm.SelectedReport, selected)) return;
                foreach (var card in _cards)
                    if (ReferenceEquals(card.DataContext, selected)) { card.Header.BringIntoView(); break; }
            }));
        }
    }

    internal sealed class ReportDayHeader : Border
    {
        public ReportDayHeader()
        {
            Padding = new Thickness(3, 7, 3, 5);
            Margin = new Thickness(0, 2, 0, 5);
            BorderBrush = ReportsStyles.BorderBrush;
            BorderThickness = new Thickness(0, 0, 0, 1);
            IsHitTestVisible = false;
            Focusable = false;
            var label = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = ReportsStyles.TextSecondary,
                TextTrimming = TextTrimming.CharacterEllipsis, Tag = "ReportDayHeader", IsHitTestVisible = false };
            label.SetBinding(TextBlock.TextProperty, new Binding(nameof(ReportListItemViewModel.DayHeading)));
            Child = label;
            SetBinding(VisibilityProperty, new Binding(nameof(ReportListItemViewModel.HasDayHeading)) { Converter = new BooleanToVisibilityConverter() });
            SetBinding(AutomationProperties.NameProperty, new Binding(nameof(ReportListItemViewModel.DayHeading)));
        }
    }

    /// <summary>One compact-mode card; binds to <see cref="ReportListItemViewModel"/> and asks the view for its open body.</summary>
    internal sealed class ReportCard : Border
    {
        private readonly Button _header;
        private readonly Border _bodyHost;
        private ReportsCompactView _owner;

        public ReportCard()
        {
            Background = ReportsStyles.Surface;
            BorderBrush = ReportsStyles.BorderBrush;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(5);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 5, 0, 0) };
            dot.SetBinding(Shape.FillProperty, new Binding(nameof(ReportListItemViewModel.Outcome)) { Converter = OutcomeDotConverter.Instance });
            grid.Children.Add(dot);
            var summary = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = ReportsStyles.Text };
            summary.SetBinding(TextBlock.TextProperty, new Binding(nameof(ReportListItemViewModel.Summary)));
            Grid.SetColumn(summary, 1);
            grid.Children.Add(summary);
            var time = new TextBlock { FontSize = 11, Foreground = ReportsStyles.TextSecondary, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            time.Tag = "ReportCardTime";
            time.SetBinding(TextBlock.TextProperty, new Binding(nameof(ReportListItemViewModel.CompactTimeText)));
            time.SetBinding(ToolTipProperty, new Binding(nameof(ReportListItemViewModel.TimeTooltip)));
            Grid.SetColumn(time, 2);
            grid.Children.Add(time);
            // Other-model reports are only listed under "All models", so IsOtherModel alone decides the sub-line.
            var sub = new TextBlock { FontSize = 11, Foreground = ReportsStyles.TextSecondary, Margin = new Thickness(0, 2, 0, 0), Tag = "OtherModelLine",
                TextTrimming = TextTrimming.CharacterEllipsis };
            sub.SetBinding(TextBlock.TextProperty, new Binding(nameof(ReportListItemViewModel.ModelName))
                { StringFormat = "{0} · " + L.T("reports.list.otherModel").Replace("{", "{{").Replace("}", "}}") });
            sub.SetBinding(VisibilityProperty, new Binding(nameof(ReportListItemViewModel.IsOtherModel)) { Converter = new BooleanToVisibilityConverter() });
            Grid.SetRow(sub, 1);
            Grid.SetColumn(sub, 1);
            Grid.SetColumnSpan(sub, 2);
            grid.Children.Add(sub);
            var updated = new TextBlock { FontSize = 11, Foreground = ReportsStyles.TextSecondary, Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis, Tag = "ReportUpdated" };
            updated.SetBinding(TextBlock.TextProperty, new Binding(nameof(ReportListItemViewModel.UpdatedText)));
            updated.SetBinding(VisibilityProperty, new Binding(nameof(ReportListItemViewModel.IsUpdated)) { Converter = new BooleanToVisibilityConverter() });
            updated.SetBinding(ToolTipProperty, new Binding(nameof(ReportListItemViewModel.UpdatedText)));
            Grid.SetRow(updated, 2);
            Grid.SetColumn(updated, 1);
            Grid.SetColumnSpan(updated, 2);
            grid.Children.Add(updated);

            _header = new Button { Style = (Style)ReportsStyles.Dictionary[ReportsStyles.CardHeaderKey], Content = grid };
            _header.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(ReportListItemViewModel.Summary)));
            _header.Click += (s, e) => _owner?.Toggle(DataContext as ReportListItemViewModel);
            _bodyHost = new Border();
            var stack = new StackPanel();
            stack.Children.Add(_header);
            stack.Children.Add(_bodyHost);
            Child = stack;

            Loaded += (s, e) =>
            {
                _owner = (ReportsCompactView)GetValue(ReportsCompactView.OwnerProperty);
                _owner?.Register(this);
                Refresh();
            };
            Unloaded += (s, e) =>
            {
                _owner?.Unregister(this);
                _owner = null;
                if (_bodyHost.Child != null) _bodyHost.Child = null;
            };
            DataContextChanged += (s, e) => Refresh();
        }

        internal Button Header => _header;
        internal bool IsOpen => _bodyHost.Child != null;

        internal void Refresh()
        {
            if (_owner == null) _owner = GetValue(ReportsCompactView.OwnerProperty) as ReportsCompactView;
            var item = DataContext as ReportListItemViewModel;
            var vm = _owner?.ViewModel;
            var open = vm != null && item != null && ReferenceEquals(item, vm.SelectedReport);
            BorderBrush = open ? ReportsStyles.CardOpenBorder : ReportsStyles.BorderBrush;
            var body = open ? _owner.BodyFor(vm.SelectedDetail) : null;
            if (ReferenceEquals(_bodyHost.Child, body)) return;
            if (body != null && body.Parent is Border previous && !ReferenceEquals(previous, _bodyHost)) previous.Child = null;
            _bodyHost.Child = body;
        }
    }

    /// <summary>Open card content: meta with Copy / Delete at its right, as in wide mode, then body, changes chip and list.</summary>
    internal sealed class CompactCardBody : StackPanel, IDisposable
    {
        private readonly ReportsViewModel _vm;
        private readonly ReportBodyView _body;
        private readonly ReportHistoryView _history;
        private readonly Button _chip, _copy, _delete;
        private readonly TextBlock _arrow;
        private readonly Border _changesHost;
        private bool _disposed;

        public CompactCardBody(ReportsViewModel viewModel, ReportDetailViewModel detail)
        {
            _vm = viewModel;
            Detail = detail;
            Margin = new Thickness(26, 0, 10, 10);

            var meta = new WrapPanel();
            meta.Children.Add(Spaced(ReportsStyles.Pill(detail.Outcome, detail.OutcomeLabel)));
            if (!string.IsNullOrEmpty(detail.ModelName))
                meta.Children.Add(Spaced(new TextBlock { Text = detail.ModelName, FontSize = 11.5, Foreground = ReportsStyles.TextSecondary,
                    VerticalAlignment = VerticalAlignment.Center }));
            meta.Children.Add(Spaced(ReportsStyles.SelfReportedChip(L.T("reports.detail.unverifiedShort"))));
            _copy = ReportsStyles.CopyButton(_vm);
            _copy.Width = _copy.MinWidth = _copy.Height = 24;
            _copy.FontSize = 12;
            _delete = ReportsStyles.DeleteButton(_vm.DeleteCommand);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 0, 0, 0) };
            actions.Children.Add(_copy);
            actions.Children.Add(_delete);
            DockPanel.SetDock(actions, Dock.Right);
            var top = new DockPanel();
            top.Children.Add(actions);
            top.Children.Add(meta);
            Children.Add(top);

            _body = new ReportBodyView { Margin = new Thickness(0, 8, 0, 0) };
            _body.SetDetail(detail);
            Children.Add(_body);
            _history = new ReportHistoryView(_vm);
            _history.SetDetail(detail);
            Children.Add(_history);

            var changes = detail.Changes;
            if (!string.IsNullOrEmpty(changes.ScopeText))
            {
                var scope = Note(changes.ScopeText, ReportsStyles.TextSecondary);
                scope.Tag = "ChangesScope";
                Children.Add(scope);
            }
            _changesHost = new Border { BorderBrush = ReportsStyles.BorderBrush, BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 6, 0, 0), Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
            if (changes.HasData && ReportChangesView.Total(changes) > 0)
            {
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new TextBlock { Text = L.T("reports.changes.chipTitle"), Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center });
                foreach (var badge in ReportChangesView.Badges(changes)) content.Children.Add(badge);
                _arrow = new TextBlock { Foreground = ReportsStyles.TextSecondary, VerticalAlignment = VerticalAlignment.Center };
                content.Children.Add(_arrow);
                _chip = new Button { Style = (Style)ReportsStyles.Dictionary[ReportsStyles.ChipButtonKey], Content = content, Margin = new Thickness(0, 8, 0, 0) };
                AutomationProperties.SetName(_chip, changes.ChipText);
                _chip.Click += (s, e) => _vm.CompactChangesExpanded = !_vm.CompactChangesExpanded;
                Children.Add(_chip);
                Children.Add(_changesHost);
            }
            else
            {
                Children.Add(Note(changes.NoteText, ReportsStyles.TextSecondary));
                // Without a chip there is no list to carry these, and "no changes" must not read as complete.
                if (!string.IsNullOrEmpty(changes.IncompleteText)) Children.Add(Note(changes.IncompleteText, ReportsStyles.WarningText));
                if (!string.IsNullOrEmpty(changes.TruncatedText)) Children.Add(Note(changes.TruncatedText, ReportsStyles.TextSecondary));
            }
            Sync();
        }

        public ReportDetailViewModel Detail { get; }
        internal ReportBodyView Body => _body;
        internal Button Chip => _chip;
        internal Button CopyButton => _copy;
        internal Button DeleteButton => _delete;
        internal ReportChangesView ChangesView => _changesHost.Child as ReportChangesView;
        internal bool ChangesVisible => _changesHost.Visibility == Visibility.Visible;

        /// <summary>Applies the expanded flag and the two-step delete state; the changes list is built on first expand.</summary>
        internal void Sync()
        {
            if (_disposed) return;
            if (_chip != null)
            {
                var expanded = _vm.CompactChangesExpanded;
                _arrow.Text = expanded ? "▴" : "▾";
                if (expanded && _changesHost.Child == null) _changesHost.Child = new ReportChangesView(Detail.Changes);
                _changesHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            }
            var pending = _vm.IsDeleteConfirmationPending;
            ReportsStyles.SyncDeleteButton(_delete, pending, _vm.DeleteLabel);
            // Same 24 px square as Copy; the confirm text sizes the button to itself.
            _delete.Height = 24;
            if (pending)
            {
                _delete.ClearValue(WidthProperty);
                _delete.ClearValue(MinWidthProperty);
                _delete.FontSize = 11.5;
                _delete.Padding = new Thickness(8, 1, 8, 1);
            }
            else
            {
                _delete.Width = _delete.MinWidth = 24;
                _delete.FontSize = 12;
                _delete.ClearValue(Control.PaddingProperty);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _copy.Command = null;
            (_copy as IDisposable)?.Dispose();
            _delete.Command = null;
            _history.Dispose();
            if (Parent is Border host) host.Child = null;
        }

        private static TextBlock Note(string text, System.Windows.Media.Brush foreground) => new TextBlock
        {
            Text = text, FontSize = 12, Foreground = foreground, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Tag = "ChangesNote"
        };

        private static FrameworkElement Spaced(FrameworkElement element)
        {
            element.Margin = new Thickness(0, 2, 8, 2);
            element.VerticalAlignment = VerticalAlignment.Center;
            return element;
        }
    }
}
