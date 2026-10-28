using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Shapes;
using System.Windows.Threading;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Reports.Presentation;
using RvtMcp.Plugin.Views;

namespace RvtMcp.Plugin.Reports.Views
{
    // The plugin shells also compile RvtMcp.Plugin.Reports.Binding; name the WPF type explicitly.
    using Binding = System.Windows.Data.Binding;

    /// <summary>Wide mode: report list with its filter on the left, the selected report on the right.</summary>
    internal sealed class ReportsWideView : Grid, IDisposable
    {
        private readonly ReportsViewModel _vm;
        private readonly Action<bool> _changesExpandedChanged;
        private readonly ToggleButton _activeFilter, _allFilter;
        private readonly ListBox _list;
        private readonly TextBlock _empty, _selectHint, _summary;
        private readonly DockPanel _detail;
        private readonly WrapPanel _meta, _changesHeader;
        private readonly Button _copy, _delete;
        private readonly ScrollViewer _scroll;
        private readonly ReportBodyView _body;
        private readonly Expander _changes;
        private readonly Border _changesContent;
        private ReportDetailViewModel _shownDetail;
        private ReportListItemViewModel _shownSelection;
        private bool _disposed;

        public ReportsWideView(ReportsViewModel viewModel, bool changesExpanded, Action<bool> changesExpandedChanged)
        {
            _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _changesExpandedChanged = changesExpandedChanged;
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Left column: filter, list or empty text.
            _activeFilter = FilterButton(L.T("reports.filter.activeModel"), ReportFilter.ActiveModel);
            _allFilter = FilterButton(L.T("reports.filter.all"), ReportFilter.All);
            var segments = new Grid();
            segments.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            segments.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            segments.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var divider = new Border { Width = 1, Background = ReportsStyles.BorderBrush };
            SetColumn(divider, 1);
            SetColumn(_allFilter, 2);
            segments.Children.Add(_activeFilter);
            segments.Children.Add(divider);
            segments.Children.Add(_allFilter);
            var segmented = new Border { BorderBrush = ReportsStyles.BorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Background = ReportsStyles.Surface, Child = segments };
            AutomationProperties.SetName(segmented, L.T("reports.filter.label"));
            var filterBar = new Border { Padding = new Thickness(8), BorderBrush = ReportsStyles.BorderBrush, BorderThickness = new Thickness(0, 0, 0, 1),
                Child = segmented };
            DockPanel.SetDock(filterBar, Dock.Top);

            _list = new ListBox
            {
                ItemsSource = _vm.Reports, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent,
                ItemContainerStyle = (Style)ReportsStyles.Dictionary[ReportsStyles.ListItemKey],
                ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(ReportListRow)) },
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            VirtualizingPanel.SetIsVirtualizing(_list, true);
            AutomationProperties.SetName(_list, L.T("reports.list.label"));
            _list.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(ReportsViewModel.SelectedReport)) { Source = _vm, Mode = BindingMode.TwoWay });
            _empty = EmptyText();
            var listArea = new Grid();
            listArea.Children.Add(_list);
            listArea.Children.Add(_empty);
            var side = new DockPanel { Background = ReportsStyles.Side };
            side.Children.Add(filterBar);
            side.Children.Add(listArea);
            Children.Add(new Border { BorderBrush = ReportsStyles.BorderBrush, BorderThickness = new Thickness(0, 0, 1, 0), Child = side });

            // Right column: header, then body and changes in one scroll region.
            _summary = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = ReportsStyles.Text };
            _meta = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
            _copy = new Button { Style = (Style)ReportsStyles.Dictionary[BimwrightStyles.ToolbarButtonKey], Content = L.T("reports.copy.button"),
                Command = _vm.CopyCommand };
            _delete = new Button { Command = _vm.DeleteCommand, Margin = new Thickness(3, 2, 0, 2) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(_copy);
            buttons.Children.Add(_delete);
            DockPanel.SetDock(buttons, Dock.Right);
            var actions = new DockPanel { Margin = new Thickness(0, 5, 0, 0), LastChildFill = false };
            actions.Children.Add(buttons);
            actions.Children.Add(ReportsStyles.SelfReportedChip(L.T("reports.detail.unverified")));
            var headerStack = new StackPanel();
            headerStack.Children.Add(_summary);
            headerStack.Children.Add(_meta);
            headerStack.Children.Add(actions);
            var header = new Border { Padding = new Thickness(16, 12, 16, 10), BorderBrush = ReportsStyles.BorderBrush,
                BorderThickness = new Thickness(0, 0, 0, 1), Child = headerStack };
            DockPanel.SetDock(header, Dock.Top);

            _body = new ReportBodyView();
            _changesHeader = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            _changesContent = new Border { Padding = new Thickness(12, 0, 12, 10) };
            _changes = new Expander { Style = (Style)ReportsStyles.Dictionary[ReportsStyles.ExpanderKey], Header = _changesHeader,
                Content = _changesContent, IsExpanded = changesExpanded, Tag = L.T("reports.changes.title") };
            _changes.Expanded += OnChangesToggled;
            _changes.Collapsed += OnChangesToggled;
            var scrolled = new StackPanel();
            scrolled.Children.Add(_body);
            scrolled.Children.Add(new Border { BorderBrush = ReportsStyles.BorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Background = ReportsStyles.Side, Margin = new Thickness(0, 12, 0, 0), Child = _changes });
            _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(16, 10, 16, 14), Content = scrolled, Focusable = false };
            _detail = new DockPanel();
            _detail.Children.Add(header);
            _detail.Children.Add(_scroll);
            _selectHint = new TextBlock { Text = L.T("reports.detail.selectHint"), Margin = new Thickness(16, 24, 16, 16), Foreground = ReportsStyles.TextSecondary,
                TextWrapping = TextWrapping.Wrap };
            var main = new Grid();
            main.Children.Add(_detail);
            main.Children.Add(_selectHint);
            SetColumn(main, 1);
            Children.Add(main);

            _vm.PropertyChanged += OnViewModelChanged;
            _vm.Reports.CollectionChanged += OnReportsChanged;
            Loaded += OnLoaded;
            SyncFilter();
            SyncDelete();
            SyncList();
            SyncDetail();
        }

        internal ListBox List => _list;
        internal ScrollViewer DetailScroll => _scroll;
        internal ReportBodyView Body => _body;
        internal Expander Changes => _changes;
        internal Button CopyButton => _copy;
        internal Button DeleteButton => _delete;
        internal ToggleButton ActiveFilterButton => _activeFilter;
        internal ToggleButton AllFilterButton => _allFilter;
        internal TextBlock EmptyTextBlock => _empty;
        internal TextBlock SelectHint => _selectHint;
        internal TextBlock SummaryText => _summary;
        internal WrapPanel Meta => _meta;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.Reports.CollectionChanged -= OnReportsChanged;
            Loaded -= OnLoaded;
            _changes.Expanded -= OnChangesToggled;
            _changes.Collapsed -= OnChangesToggled;
            BindingOperations.ClearAllBindings(_list);
            _list.ItemsSource = null;
            _copy.Command = null;
            _delete.Command = null;
        }

        private ToggleButton FilterButton(string text, ReportFilter filter)
        {
            var button = new ToggleButton { Style = (Style)ReportsStyles.Dictionary[ReportsStyles.FilterButtonKey], Content = text };
            button.Click += (s, e) => { _vm.Filter = filter; SyncFilter(); };
            return button;
        }

        private static TextBlock EmptyText() => new TextBlock
        {
            Margin = new Thickness(16, 24, 16, 16), Foreground = ReportsStyles.TextSecondary, TextWrapping = TextWrapping.Wrap, FontSize = 12.5
        };

        private void OnLoaded(object sender, RoutedEventArgs e) => ScrollToSelection(true);

        private void OnViewModelChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_disposed) return;
            switch (e.PropertyName)
            {
                case nameof(ReportsViewModel.Filter): SyncFilter(); break;
                case nameof(ReportsViewModel.EmptyText): SyncList(); break;
                case nameof(ReportsViewModel.SelectedReport): ScrollToSelection(false); SyncDetail(); break;
                case nameof(ReportsViewModel.SelectedDetail): SyncDetail(); break;
                case nameof(ReportsViewModel.IsDeleteConfirmationPending):
                case nameof(ReportsViewModel.DeleteLabel): SyncDelete(); break;
            }
        }

        private void OnReportsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (!_disposed) { SyncList(); SyncDetail(); }
        }

        private void OnChangesToggled(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(e.OriginalSource, _changes)) _changesExpandedChanged?.Invoke(_changes.IsExpanded);
        }

        private void SyncFilter()
        {
            _activeFilter.IsChecked = _vm.Filter == ReportFilter.ActiveModel;
            _allFilter.IsChecked = _vm.Filter == ReportFilter.All;
        }

        private void SyncDelete()
        {
            _delete.Content = _vm.DeleteLabel;
            _delete.Style = (Style)ReportsStyles.Dictionary[_vm.IsDeleteConfirmationPending
                ? ReportsStyles.DangerButtonKey : BimwrightStyles.ToolbarButtonKey];
        }

        private void SyncList()
        {
            var empty = _vm.Reports.Count == 0;
            _empty.Text = empty ? _vm.EmptyText : "";
            _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            _list.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Rebuilds the detail only for a different detail object; a reload that keeps it keeps the scroll offset.</summary>
        private void SyncDetail()
        {
            var detail = _vm.SelectedDetail;
            _detail.Visibility = detail != null ? Visibility.Visible : Visibility.Collapsed;
            _selectHint.Visibility = detail == null && _vm.Reports.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (ReferenceEquals(detail, _shownDetail)) return;
            _shownDetail = detail;
            _body.SetDetail(detail);
            _meta.Children.Clear();
            _changesHeader.Children.Clear();
            _changesContent.Child = null;
            if (detail == null) return;
            _summary.Text = detail.Summary ?? "";
            _meta.Children.Add(Spaced(ReportsStyles.Pill(detail.Outcome, detail.OutcomeLabel)));
            AddMeta(detail.TimeText, false);
            AddMeta(detail.ModelName, false);
            AddMeta(detail.TaskId, true);
            _changesHeader.Children.Add(new TextBlock { Text = L.T("reports.changes.title"), FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
            foreach (var badge in ReportChangesView.Badges(detail.Changes)) _changesHeader.Children.Add(badge);
            _changesContent.Child = new ReportChangesView(detail.Changes);
            _scroll.ScrollToTop();
        }

        private void AddMeta(string text, bool mono)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (_meta.Children.Count > 1)
                _meta.Children.Add(Spaced(new TextBlock { Text = "·", Foreground = ReportsStyles.Muted, VerticalAlignment = VerticalAlignment.Center }));
            var block = new TextBlock { Text = text, Foreground = ReportsStyles.TextSecondary, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            if (mono) { block.FontFamily = ReportsStyles.Mono; block.FontSize = 11.5; }
            _meta.Children.Add(Spaced(block));
        }

        private static FrameworkElement Spaced(FrameworkElement element)
        {
            element.Margin = new Thickness(0, 2, 8, 2);
            return element;
        }

        /// <summary>Scrolls to a newly selected report (ShowReport, keyboard, mode switch), not to a reload that kept it.</summary>
        private void ScrollToSelection(bool force)
        {
            var selected = _vm.SelectedReport;
            if (!force && ReferenceEquals(selected, _shownSelection)) return;
            _shownSelection = selected;
            if (selected == null || !IsLoaded) return;
            _list.ScrollIntoView(selected);
        }
    }

    /// <summary>One wide-mode list row; binds to <see cref="ReportListItemViewModel"/>.</summary>
    internal sealed class ReportListRow : Grid
    {
        public ReportListRow()
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(17) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 5, 0, 0) };
            dot.SetBinding(Shape.FillProperty, new Binding(nameof(ReportListItemViewModel.Outcome)) { Converter = OutcomeDotConverter.Instance });
            dot.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(ReportListItemViewModel.OutcomeLabel)));
            Children.Add(dot);
            var summary = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
                LineHeight = 17, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, MaxHeight = 34, Foreground = ReportsStyles.Text };
            summary.SetBinding(TextBlock.TextProperty, new Binding(nameof(ReportListItemViewModel.Summary)));
            var meta = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
            var time = MetaText();
            time.SetBinding(TextBlock.TextProperty, new Binding(nameof(ReportListItemViewModel.TimeText)));
            var model = MetaText();
            model.SetBinding(TextBlock.TextProperty, new Binding(nameof(ReportListItemViewModel.ModelName)));
            var other = new Border { BorderBrush = ReportsStyles.BorderStrongBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Tag = "OtherModel",
                Child = new TextBlock { Text = L.T("reports.list.otherModel"), FontSize = 10, Foreground = ReportsStyles.TextSecondary } };
            other.SetBinding(VisibilityProperty, new Binding(nameof(ReportListItemViewModel.IsOtherModel)) { Converter = new BooleanToVisibilityConverter() });
            meta.Children.Add(time);
            meta.Children.Add(new TextBlock { Text = "·", Foreground = ReportsStyles.Muted, FontSize = 11.5, Margin = new Thickness(0, 0, 4, 0) });
            meta.Children.Add(model);
            meta.Children.Add(other);
            var text = new StackPanel();
            text.Children.Add(summary);
            text.Children.Add(meta);
            SetColumn(text, 1);
            Children.Add(text);
        }

        private static TextBlock MetaText() => new TextBlock { FontSize = 11.5, Foreground = ReportsStyles.TextSecondary, Margin = new Thickness(0, 0, 4, 0),
            TextTrimming = TextTrimming.CharacterEllipsis };
    }
}
