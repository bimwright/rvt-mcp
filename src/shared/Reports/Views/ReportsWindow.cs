using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Reports.Presentation;
using RvtMcp.Plugin.Views;
using RvtMcp.Plugin.Views.Toast;

namespace RvtMcp.Plugin.Reports.Views
{
    // The plugin shells also compile RvtMcp.Plugin.Reports.Binding; name the WPF type explicitly.
    using Binding = System.Windows.Data.Binding;

    /// <summary>
    /// Floating Reports window with a drawn title bar (WindowChrome), two display modes and per-mode bounds.
    /// The host sets the owner and shows it; closing hides it and only <see cref="CloseForShutdown"/> closes it.
    /// The view model belongs to the host and is not disposed here.
    /// </summary>
    public sealed class ReportsWindow : Window, IDisposable
    {
        private const double CaptionHeight = 32;
        private static readonly Geometry WideIcon = Freeze(new GeometryGroup { Children = {
            new RectangleGeometry(new Rect(1.5, 2.5, 13, 11), 1, 1), new LineGeometry(new Point(6, 2.5), new Point(6, 13.5)) } });
        private static readonly Geometry CompactIcon = Freeze(new GeometryGroup { Children = {
            new RectangleGeometry(new Rect(4.5, 1.5, 7, 13), 1, 1), Geometry.Parse("M6.5,5 H9.5 M6.5,8 H9.5 M6.5,11 H9.5") } });
        private static readonly Geometry MinimizeIcon = Freeze(Geometry.Parse("M0,5 H10"));
        private static readonly Geometry MaximizeIcon = Freeze(new RectangleGeometry(new Rect(0.5, 0.5, 9, 9)));
        private static readonly Geometry RestoreIcon = Freeze(new GeometryGroup { Children = {
            new RectangleGeometry(new Rect(0.5, 2.5, 7, 7)), Geometry.Parse("M2.5,2.5 V0.5 H9.5 V7.5 H7.5") } });
        private static readonly Geometry CloseIcon = Freeze(Geometry.Parse("M0,0 L10,10 M10,0 L0,10"));

        private readonly ReportsViewModel _vm;
        private readonly Action<ReportsWindowState> _saveState;
        private readonly IReportsScreen _screen;
        private readonly WindowChrome _chrome;
        private readonly Border _frame, _viewHost, _status;
        private readonly TextBlock _title, _subtitle, _statusText;
        private readonly ToggleButton _wideButton, _compactButton;
        private readonly Button _minimizeButton, _maximizeButton, _closeButton;
        private readonly Path _maximizeGlyph;
        private readonly Ellipse _statusDot;
        private ReportsWindowState _state;
        private DisplayMode _mode;
        private FrameworkElement _view;
        private bool _placed, _wideChangesExpanded = true, _shutdown, _disposed;

        public ReportsWindow(ReportsViewModel viewModel, ReportsWindowState state, Action<ReportsWindowState> saveState, IReportsScreen screen = null)
        {
            _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _state = state ?? new ReportsWindowState();
            _saveState = saveState;
            _screen = screen ?? new ReportsScreen();
            _mode = _vm.Mode;

            WindowStartupLocation = WindowStartupLocation.Manual;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            ShowInTaskbar = true;
            FontFamily = ReportsStyles.Ui;
            FontSize = 12.5;
            Foreground = ReportsStyles.Text;
            Background = ReportsStyles.Surface;
            UseLayoutRounding = true;
            _chrome = new WindowChrome
            {
                CaptionHeight = CaptionHeight, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false
            };
            WindowChrome.SetWindowChrome(this, _chrome);
            Resources.MergedDictionaries.Add(ReportsStyles.Dictionary);
            ScrollBarFadeBehavior.SetIsEnabled(this, true);

            // Title bar: mark, title, subtitle, mode pair, minimize / maximize / close.
            var mark = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(3), Background = ReportsStyles.Primary,
                Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new Border { Width = 6, Height = 6, BorderBrush = Brushes.White, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(1) } };
            _title = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis };
            _subtitle = new TextBlock { Text = " · Bimwright", Foreground = ReportsStyles.TextSecondary, VerticalAlignment = VerticalAlignment.Center };
            _wideButton = ModeButton(WideIcon, DisplayMode.Wide);
            _compactButton = ModeButton(CompactIcon, DisplayMode.Compact);
            var modePanel = new StackPanel { Orientation = Orientation.Horizontal };
            modePanel.Children.Add(_wideButton);
            modePanel.Children.Add(new Border { Width = 1, Background = ReportsStyles.BorderBrush });
            modePanel.Children.Add(_compactButton);
            var modes = new Border { BorderBrush = ReportsStyles.BorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Background = ReportsStyles.Surface, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Child = modePanel };
            _minimizeButton = CaptionButton(MinimizeIcon, ReportsStyles.CaptionButtonKey, out _);
            _maximizeButton = CaptionButton(MaximizeIcon, ReportsStyles.CaptionButtonKey, out _maximizeGlyph);
            _closeButton = CaptionButton(CloseIcon, ReportsStyles.CloseButtonKey, out _);
            _minimizeButton.Click += (s, e) => SystemCommands.MinimizeWindow(this);
            _maximizeButton.Click += (s, e) =>
            {
                if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
                else SystemCommands.MaximizeWindow(this);
            };
            _closeButton.Click += (s, e) => SystemCommands.CloseWindow(this);
            var bar = new DockPanel { LastChildFill = false };
            foreach (var right in new FrameworkElement[] { _closeButton, _maximizeButton, _minimizeButton, modes })
            {
                DockPanel.SetDock(right, Dock.Right);
                bar.Children.Add(right);
            }
            foreach (var left in new FrameworkElement[] { mark, _title, _subtitle })
            {
                DockPanel.SetDock(left, Dock.Left);
                bar.Children.Add(left);
            }
            var titleBar = new Border { Height = CaptionHeight, Background = ReportsStyles.Header, BorderBrush = ReportsStyles.BorderBrush,
                BorderThickness = new Thickness(0, 0, 0, 1), Child = bar };

            // Status line: dot and text, tinted by tone; the hint shows when there is no status.
            _statusDot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(10, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            _statusText = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            var statusPanel = new DockPanel();
            DockPanel.SetDock(_statusDot, Dock.Left);
            statusPanel.Children.Add(_statusDot);
            statusPanel.Children.Add(_statusText);
            _status = new Border { Height = 26, BorderBrush = ReportsStyles.BorderBrush, BorderThickness = new Thickness(0, 1, 0, 0), Child = statusPanel };

            _viewHost = new Border();
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_viewHost, 1);
            Grid.SetRow(_status, 2);
            root.Children.Add(titleBar);
            root.Children.Add(_viewHost);
            root.Children.Add(_status);
            _frame = new Border { BorderBrush = ReportsStyles.BorderStrongBrush, BorderThickness = new Thickness(1), Background = ReportsStyles.Surface, Child = root };
            Content = _frame;

            ApplyDefaultSize(Config(_mode));
            _vm.PropertyChanged += OnViewModelChanged;
            L.Changed += OnLanguageChanged;
            IsVisibleChanged += OnVisibleChanged;
            StateChanged += OnFrameChanged;
            SizeChanged += OnFrameChanged;
            ApplyLocalization();
            ShowView();
            SyncModeChrome();
            SyncStatus();
        }

        internal DisplayMode CurrentMode => _mode;
        internal ReportsWindowState State => _state;
        internal FrameworkElement View => _view;
        internal ToggleButton WideButton => _wideButton;
        internal ToggleButton CompactButton => _compactButton;
        internal Button MinimizeButton => _minimizeButton;
        internal Button MaximizeButton => _maximizeButton;
        internal Button CloseButton => _closeButton;
        internal TextBlock TitleText => _title;
        internal TextBlock SubtitleText => _subtitle;
        internal Border StatusBar => _status;
        internal Ellipse StatusDot => _statusDot;
        internal TextBlock StatusText => _statusText;
        internal Border Frame => _frame;

        /// <summary>Saves the state and closes for real; plain Close and Alt+F4 only hide the window.</summary>
        public void CloseForShutdown()
        {
            if (_disposed) return;
            _shutdown = true;
            Close();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _vm.PropertyChanged -= OnViewModelChanged;
            L.Changed -= OnLanguageChanged;
            IsVisibleChanged -= OnVisibleChanged;
            StateChanged -= OnFrameChanged;
            SizeChanged -= OnFrameChanged;
            (_view as IDisposable)?.Dispose();
            _view = null;
            _viewHost.Child = null;
        }

        /// <summary>The host has set the owner by now: place the window before its first frame.</summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var mode = Config(_mode);
            var saved = _state.BoundsFor(mode);
            Place(mode, saved, saved == null ? (ToastBounds?)null : Rect(saved));
            _placed = true;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            CaptureState();
            SaveState();
            if (!_shutdown)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            Dispose();
        }

        private ToggleButton ModeButton(Geometry icon, DisplayMode mode)
        {
            var button = new ToggleButton { Style = (Style)ReportsStyles.Dictionary[ReportsStyles.ModeButtonKey] };
            button.Content = Glyph(icon, button, 16, 14, 1.4);
            WindowChrome.SetIsHitTestVisibleInChrome(button, true);
            button.Click += (s, e) =>
            {
                _vm.Mode = mode;
                SyncModeChrome(); // a click on the checked button toggles it off; put it back.
            };
            return button;
        }

        private static Button CaptionButton(Geometry icon, string styleKey, out Path glyph)
        {
            var button = new Button { Style = (Style)ReportsStyles.Dictionary[styleKey], VerticalAlignment = VerticalAlignment.Top };
            var box = Glyph(icon, button, 10, 10, 1.1);
            glyph = (Path)((Canvas)box.Child).Children[0];
            button.Content = box;
            WindowChrome.SetIsHitTestVisibleInChrome(button, true);
            return button;
        }

        /// <summary>Icon drawn in a <paramref name="viewBox"/>-unit square, stroked with the button's foreground.</summary>
        private static Viewbox Glyph(Geometry icon, Control owner, double viewBox, double size, double stroke)
        {
            var path = new Path { Data = icon, StrokeThickness = stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round, SnapsToDevicePixels = true };
            path.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = owner });
            var canvas = new Canvas { Width = viewBox, Height = viewBox };
            canvas.Children.Add(path);
            return new Viewbox { Width = size, Height = size, Child = canvas };
        }

        private void ApplyLocalization()
        {
            Title = L.T("reports.window.title");
            _title.Text = Title;
            Describe(_wideButton, L.T("reports.mode.wide"));
            Describe(_compactButton, L.T("reports.mode.compact"));
            Describe(_minimizeButton, L.T("reports.window.minimize"));
            Describe(_closeButton, L.T("reports.window.close"));
            SyncMaximizeButton();
        }

        private static void Describe(FrameworkElement element, string text)
        {
            element.ToolTip = text;
            AutomationProperties.SetName(element, text);
        }

        private void ShowView()
        {
            (_view as IDisposable)?.Dispose();
            _view = _mode == DisplayMode.Compact
                ? (FrameworkElement)new ReportsCompactView(_vm)
                : new ReportsWideView(_vm, _wideChangesExpanded, expanded => _wideChangesExpanded = expanded);
            _viewHost.Child = _view;
        }

        private void SyncModeChrome()
        {
            _wideButton.IsChecked = _mode == DisplayMode.Wide;
            _compactButton.IsChecked = _mode == DisplayMode.Compact;
            _subtitle.Visibility = _mode == DisplayMode.Compact ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SyncMaximizeButton()
        {
            var maximized = WindowState == WindowState.Maximized;
            _maximizeGlyph.Data = maximized ? RestoreIcon : MaximizeIcon;
            Describe(_maximizeButton, L.T(maximized ? "reports.window.restore" : "reports.window.maximize"));
        }

        private void SyncStatus()
        {
            var hint = string.IsNullOrEmpty(_vm.StatusText);
            var colors = ReportsStyles.Status(hint ? ReportStatusTone.Neutral : _vm.StatusTone);
            var text = hint ? L.T("reports.status.hint") : _vm.StatusText;
            _status.Background = colors.Background;
            _statusDot.Fill = colors.Dot;
            _statusText.Foreground = colors.Text;
            _statusText.Text = text;
            _statusText.ToolTip = text;
        }

        private void OnViewModelChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_disposed) return;
            if (e.PropertyName == nameof(ReportsViewModel.Mode)) SwitchMode(_vm.Mode);
            else if (e.PropertyName == nameof(ReportsViewModel.StatusText) || e.PropertyName == nameof(ReportsViewModel.StatusTone)) SyncStatus();
        }

        /// <summary>
        /// Keep the old mode's normal bounds, leave the maximized state, place the new mode, swap the view,
        /// then save once. Selection, filter, link state and status live in the view model.
        /// </summary>
        private void SwitchMode(DisplayMode mode)
        {
            if (mode == _mode) return;
            var oldMode = Config(_mode);
            var newMode = Config(mode);
            if (_placed)
            {
                var current = CurrentBounds();
                if (current != null) _state = _state.WithBounds(oldMode, current);
                if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            }
            _mode = mode;
            _state = _state.WithMode(newMode);
            if (_placed)
            {
                var saved = _state.BoundsFor(newMode);
                var bounds = Place(newMode, saved, saved != null ? Rect(saved) : _screen.GetOwnerBounds(this));
                _state = _state.WithBounds(newMode, bounds);
            }
            else ApplyDefaultSize(newMode);
            ShowView();
            SyncModeChrome();
            SaveState();
        }

        private ReportsWindowBounds Place(ReportsWindowMode mode, ReportsWindowBounds saved, ToastBounds? near)
        {
            var owner = _screen.GetOwnerBounds(this);
            var work = _screen.GetWorkArea(this, near ?? owner);
            var bounds = ReportsWindowPlacement.Restore(mode, saved, owner, work);
            ApplyBounds(mode, bounds);
            return bounds;
        }

        /// <summary>Lower the minimums, set the bounds, then raise them, so WPF never clamps the new size to the old minimum.</summary>
        private void ApplyBounds(ReportsWindowMode mode, ReportsWindowBounds bounds)
        {
            MinWidth = Math.Min(ReportsWindowPlacement.WideMinWidth, ReportsWindowPlacement.CompactMinWidth);
            MinHeight = Math.Min(ReportsWindowPlacement.WideMinHeight, ReportsWindowPlacement.CompactMinHeight);
            Left = bounds.Left;
            Top = bounds.Top;
            Width = bounds.Width;
            Height = bounds.Height;
            MinWidth = ReportsWindowPlacement.MinWidth(mode);
            MinHeight = ReportsWindowPlacement.MinHeight(mode);
        }

        private void ApplyDefaultSize(ReportsWindowMode mode)
        {
            var compact = mode == ReportsWindowMode.Compact;
            ApplyBounds(mode, new ReportsWindowBounds(double.IsNaN(Left) ? 0 : Left, double.IsNaN(Top) ? 0 : Top,
                compact ? ReportsWindowPlacement.CompactWidth : ReportsWindowPlacement.WideWidth,
                compact ? ReportsWindowPlacement.CompactHeight : ReportsWindowPlacement.WideHeight));
        }

        /// <summary>Normal bounds; while maximized or minimized, the bounds the window restores to.</summary>
        private ReportsWindowBounds CurrentBounds()
        {
            if (WindowState == WindowState.Normal)
                return new ReportsWindowBounds(Left, Top, double.IsNaN(Width) ? ActualWidth : Width, double.IsNaN(Height) ? ActualHeight : Height);
            var restore = RestoreBounds;
            return restore.IsEmpty ? null : new ReportsWindowBounds(restore.Left, restore.Top, restore.Width, restore.Height);
        }

        private void CaptureState()
        {
            var mode = Config(_mode);
            if (_placed)
            {
                var current = CurrentBounds();
                if (current != null) _state = _state.WithBounds(mode, current);
            }
            _state = _state.WithMode(mode);
        }

        private void SaveState()
        {
            try { _saveState?.Invoke(_state); }
            catch { } // A failed save must never block hiding or closing.
        }

        private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_disposed || !IsVisible) return;
            if (_placed && WindowState == WindowState.Normal)
            {
                // Shown again after a hide: the monitor layout may have changed meanwhile.
                var mode = Config(_mode);
                var current = CurrentBounds();
                Place(mode, current, current == null ? (ToastBounds?)null : Rect(current));
            }
            _ = _vm.RefreshAsync();
        }

        private void OnLanguageChanged(object sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ApplyLanguage));
                return;
            }
            ApplyLanguage();
        }

        private void ApplyLanguage()
        {
            if (_disposed) return;
            ApplyLocalization();
            ShowView();
            SyncStatus();
        }

        /// <summary>
        /// A maximized WindowChrome window overhangs the monitor by the system frame on every side;
        /// inset the content by that much so nothing is cut off, and stretch the caption to match.
        /// </summary>
        private void OnFrameChanged(object sender, EventArgs e)
        {
            if (_disposed) return;
            var inset = WindowState == WindowState.Maximized ? ReportsScreen.MaximizedOverhang(this) : new Thickness(0);
            if (_frame.Margin != inset)
            {
                _frame.Margin = inset;
                _frame.BorderThickness = new Thickness(WindowState == WindowState.Maximized ? 0 : 1);
                _chrome.CaptionHeight = CaptionHeight + inset.Top;
            }
            if (e is RoutedEventArgs) return;
            SyncMaximizeButton();
        }

        private static ReportsWindowMode Config(DisplayMode mode) => mode == DisplayMode.Compact ? ReportsWindowMode.Compact : ReportsWindowMode.Wide;

        private static ToastBounds Rect(ReportsWindowBounds bounds) =>
            new ToastBounds(bounds.Left, bounds.Top, bounds.Width, bounds.Height);

        private static Geometry Freeze(Geometry geometry)
        {
            geometry.Freeze();
            return geometry;
        }
    }
}
