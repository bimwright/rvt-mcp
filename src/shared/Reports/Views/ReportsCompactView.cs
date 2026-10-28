using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
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
        private readonly HashSet<ReportCard> _cards = new HashSet<ReportCard>();
        private CompactCardBody _body;
        private ReportListItemViewModel _shownSelection;
        private bool _syncingFilter, _disposed;

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

            _feed = new ListBox
            {
                ItemsSource = _vm.Reports, Background = ReportsStyles.Feed, BorderThickness = new Thickness(0), Padding = new Thickness(8),
                ItemContainerStyle = (Style)ReportsStyles.Dictionary[ReportsStyles.FeedItemKey],
                ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(ReportCard)) },
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
            feedArea.Children.Add(_feed);
            feedArea.Children.Add(_empty);
            Children.Add(feedArea);

            _vm.PropertyChanged += OnViewModelChanged;
            _vm.Reports.CollectionChanged += OnReportsChanged;
            Loaded += OnLoaded;
            SyncFilter();
            SyncList();
        }

        internal ReportsViewModel ViewModel => _vm;
        internal ComboBox Filter => _filter;
        internal ListBox Feed => _feed;
        internal TextBlock CountText => _count;
        internal TextBlock EmptyTextBlock => _empty;
        internal CompactCardBody OpenBody => _body;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.Reports.CollectionChanged -= OnReportsChanged;
            _filter.SelectionChanged -= OnFilterSelected;
            Loaded -= OnLoaded;
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

        private void OnLoaded(object sender, RoutedEventArgs e) => ScrollToSelection(true);

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
                    ScrollToSelection(false);
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

        /// <summary>After a card opens, scroll so its header is visible; a reload that kept the card does not scroll.</summary>
        private void ScrollToSelection(bool force)
        {
            var selected = _vm.SelectedReport;
            if (!force && ReferenceEquals(selected, _shownSelection)) return;
            _shownSelection = selected;
            if (selected == null || !IsLoaded) return;
            _feed.ScrollIntoView(selected);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (_disposed || !ReferenceEquals(_vm.SelectedReport, selected)) return;
                foreach (var card in _cards)
                    if (ReferenceEquals(card.DataContext, selected)) { card.Header.BringIntoView(); break; }
            }));
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
            var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 5, 0, 0) };
            dot.SetBinding(Shape.FillProperty, new Binding(nameof(ReportListItemViewModel.Outcome)) { Converter = OutcomeDotConverter.Instance });
            grid.Children.Add(dot);
            var summary = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = ReportsStyles.Text };
            summary.SetBinding(TextBlock.TextProperty, new Binding(nameof(ReportListItemViewModel.Summary)));
            Grid.SetColumn(summary, 1);
            grid.Children.Add(summary);
            var time = new TextBlock { FontSize = 11, Foreground = ReportsStyles.TextSecondary, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            time.SetBinding(TextBlock.TextProperty, new Binding(nameof(ReportListItemViewModel.TimeText)));
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

    /// <summary>Open card content: meta, body, changes chip and list, Copy / Delete.</summary>
    internal sealed class CompactCardBody : StackPanel, IDisposable
    {
        private readonly ReportsViewModel _vm;
        private readonly ReportBodyView _body;
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
            Children.Add(meta);

            _body = new ReportBodyView { Margin = new Thickness(0, 8, 0, 0) };
            _body.SetDetail(detail);
            Children.Add(_body);

            var changes = detail.Changes;
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

            var small = (Style)ReportsStyles.Dictionary[BimwrightStyles.ToolbarButtonKey];
            _copy = SmallButton(small, L.T("reports.copy.button"), _vm.CopyCommand);
            _delete = SmallButton(small, "", _vm.DeleteCommand);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-3, 6, 0, 0) };
            actions.Children.Add(_copy);
            actions.Children.Add(_delete);
            Children.Add(actions);
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
            _delete.Content = _vm.DeleteLabel;
            var pending = _vm.IsDeleteConfirmationPending;
            var style = (Style)ReportsStyles.Dictionary[pending ? ReportsStyles.DangerButtonKey : BimwrightStyles.ToolbarButtonKey];
            _delete.Style = style;
            ApplySmall(_delete);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _copy.Command = null;
            _delete.Command = null;
            if (Parent is Border host) host.Child = null;
        }

        private static TextBlock Note(string text, System.Windows.Media.Brush foreground) => new TextBlock
        {
            Text = text, FontSize = 12, Foreground = foreground, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Tag = "ChangesNote"
        };

        private static Button SmallButton(Style style, string text, ICommand command)
        {
            var button = new Button { Style = style, Content = text, Command = command };
            ApplySmall(button);
            return button;
        }

        private static void ApplySmall(Button button)
        {
            button.FontSize = 11.5;
            button.Height = 24;
            button.Padding = new Thickness(8, 1, 8, 1);
        }

        private static FrameworkElement Spaced(FrameworkElement element)
        {
            element.Margin = new Thickness(0, 2, 8, 2);
            element.VerticalAlignment = VerticalAlignment.Center;
            return element;
        }
    }
}
