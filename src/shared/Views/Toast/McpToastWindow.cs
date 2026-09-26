using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using RvtMcp.Plugin.Localization;

namespace RvtMcp.Plugin.Views.Toast
{
    internal sealed class McpToastWindow : Window
    {
        private const double CardWidth = 340;
        private const double BrandRestOpacity = 0.3;
        private const double BrandSettleOpacity = 0.8;
        private const int GwlExStyle = -20;
        private const long WsExNoActivate = 0x08000000L;
        private const long WsExToolWindow = 0x00000080L;

        private readonly TextBlock _iconText;
        private readonly TextBlock _titleText;
        private readonly TextBlock _categoryText;
        private readonly TextBlock _bodyText;
        private readonly TextBlock _brandText;
        private readonly TextBlock _brandShine;
        private readonly TranslateTransform _brandSweep = new TranslateTransform(-0.75, 0);
        private readonly TranslateTransform _shineSweep = new TranslateTransform(-0.75, 0);
        private readonly Func<Point> _cursorPosition;
        private readonly Border _root;
        private readonly TranslateTransform _slideTransform;
        private readonly ScaleTransform _scaleTransform;
        private Border _closeHost;
        private MouseEventHandler _closeHostMouseEnterHandler;
        private MouseEventHandler _closeHostMouseLeaveHandler;
        private MouseButtonEventHandler _closeHostMouseUpHandler;
        private MouseEventHandler _mouseEnterHandler;
        private MouseEventHandler _mouseLeaveHandler;
        private MouseButtonEventHandler _mouseUpHandler;
        private EventHandler _sourceInitializedHandler;
        private EventHandler _closedHandler;
        private bool _isClosing;
        private long _cardId;
        private Action<McpToastWindow, long> _activityClosed;
        private Action<long> _activityDismissed;
        private Action<long> _activityClicked;
        private Action<long> _activityPointerEntered;
        private Action<long> _activityPointerLeft;
        private bool _hasPointerPosition;
        private int _lastPointerX;
        private int _lastPointerY;
        private bool _closedCallbackRaised;
        private bool _handlersDetached;

        public McpToastViewModel ViewModel { get; private set; }
        public long CardId => _cardId;

        /// <summary>
        /// Activity-card constructor. The manager owns timing and lifecycle; this
        /// window only renders the snapshot and reports user/window events.
        /// </summary>
        public McpToastWindow(
            ActivitySnapshot snapshot,
            Action<McpToastWindow, long> onClosed,
            Action<long> onDismiss,
            Action<long> onClick,
            Action<long> onPointerEntered,
            Action<long> onPointerLeft,
            Func<Point> cursorPosition = null)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            ViewModel = ToViewModel(snapshot);
            _cardId = snapshot.CardId;
            _activityClosed = onClosed;
            _activityDismissed = onDismiss;
            _activityClicked = onClick;
            _activityPointerEntered = onPointerEntered;
            _activityPointerLeft = onPointerLeft;
            _cursorPosition = cursorPosition ?? ReadCursorPosition;

            FontFamily = McpToastTheme.UiFont;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Width = CardWidth;
            Opacity = 0;

            _slideTransform = new TranslateTransform(-24, 0);
            _scaleTransform = new ScaleTransform(0.96, 0.96);

            var transformGroup = new TransformGroup();
            transformGroup.Children.Add(_scaleTransform);
            transformGroup.Children.Add(_slideTransform);

            _root = new Border
            {
                Width = CardWidth,
                Margin = new Thickness(8),
                CornerRadius = new CornerRadius(8),
                Background = McpToastTheme.Background,
                BorderBrush = McpToastTheme.BuildAccentBrush(ViewModel),
                BorderThickness = new Thickness(6, 0, 0, 0),
                RenderTransformOrigin = new Point(0, 0.5),
                RenderTransform = transformGroup,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 16,
                    ShadowDepth = 4,
                    Opacity = 0.22,
                    Color = Colors.Black
                }
            };

            var content = new Grid { Margin = new Thickness(10, 12, 14, 12) };
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // Reserve the single latest-result line even when a tool returns no body.
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new DockPanel { LastChildFill = true };

            _iconText = new TextBlock
            {
                Text = McpToastTheme.GetIconGlyph(ViewModel),
                FontFamily = McpToastTheme.IconFont,
                FontSize = 16,
                Foreground = McpToastTheme.BuildIconBrush(ViewModel),
                Margin = new Thickness(0, 1, 8, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            DockPanel.SetDock(_iconText, Dock.Left);
            header.Children.Add(_iconText);

            var closeHost = _closeHost = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Top
            };
            var closeGlyph = new TextBlock
            {
                Text = "\uE711",
                FontFamily = McpToastTheme.IconFont,
                FontSize = 10,
                Foreground = McpToastTheme.CloseIcon,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            closeHost.Child = closeGlyph;
            _closeHostMouseEnterHandler = (_, __) => closeHost.Background = McpToastTheme.CloseHover;
            _closeHostMouseLeaveHandler = (_, __) => closeHost.Background = Brushes.Transparent;
            _closeHostMouseUpHandler = (_, e) =>
            {
                e.Handled = true;
                _activityDismissed?.Invoke(_cardId);
            };
            closeHost.MouseEnter += _closeHostMouseEnterHandler;
            closeHost.MouseLeave += _closeHostMouseLeaveHandler;
            closeHost.MouseLeftButtonUp += _closeHostMouseUpHandler;
            DockPanel.SetDock(closeHost, Dock.Right);
            header.Children.Add(closeHost);

            _titleText = new TextBlock
            {
                Text = ViewModel.Title ?? string.Empty,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Foreground = McpToastTheme.Text,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            header.Children.Add(_titleText);

            Grid.SetRow(header, 0);
            content.Children.Add(header);

            _categoryText = new TextBlock
            {
                Text = ViewModel.CategoryLabel ?? string.Empty,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = McpToastTheme.Primary,
                Margin = new Thickness(24, 2, 0, 0)
            };
            Grid.SetRow(_categoryText, 1);
            content.Children.Add(_categoryText);

            _bodyText = new TextBlock
            {
                Text = ViewModel.Body,
                FontSize = 12.5,
                FontWeight = FontWeights.Medium,
                Foreground = McpToastTheme.Text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(24, 4, 0, 0),
                MaxHeight = 64,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetRow(_bodyText, 2);
            content.Children.Add(_bodyText);

            var footer = new DockPanel { Margin = new Thickness(24, 6, 0, 0) };

            _brandText = new TextBlock
            {
                // Logo casing and colours. Brightness lives in the OpacityMask: dimmed at
                // rest, then a lit front wipes left→right once ~1.3 s after the card shows
                // (WipeBrand) and the wordmark settles at BrandSettleOpacity.
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = BrandAssets.ProductTag,
                OpacityMask = BuildBrandMask(_brandSweep),
                Inlines =
                {
                    new Run(BrandAssets.WordmarkLeft) { Foreground = McpToastTheme.BrandBim },
                    new Run(BrandAssets.WordmarkRight) { Foreground = McpToastTheme.BrandWright }
                }
            };

            _brandShine = new TextBlock
            {
                // The wordmark again in lighter tints, masked to a narrow band that sweeps
                // with the wipe — the wave passes inside the letterforms.
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                OpacityMask = BuildShineMask(_shineSweep),
                Inlines =
                {
                    new Run(BrandAssets.WordmarkLeft) { Foreground = McpToastTheme.BrandBimShine },
                    new Run(BrandAssets.WordmarkRight) { Foreground = McpToastTheme.BrandWrightShine }
                }
            };

            var brandCell = new Grid { VerticalAlignment = VerticalAlignment.Center };
            brandCell.Children.Add(_brandText);
            brandCell.Children.Add(_brandShine);
            DockPanel.SetDock(brandCell, Dock.Right);
            footer.Children.Add(brandCell);

            Grid.SetRow(footer, 3);
            content.Children.Add(footer);

            ApplyActivitySnapshot(snapshot);

            _root.Child = content;
            _root.Cursor = Cursors.Hand;
            Content = _root;

            _mouseEnterHandler = (_, __) =>
            {
                if (PointerPositionChanged())
                    _activityPointerEntered?.Invoke(_cardId);
                WipeBrand(150);
            };
            _mouseLeaveHandler = (_, __) =>
            {
                if (PointerPositionChanged())
                    _activityPointerLeft?.Invoke(_cardId);
            };
            _mouseUpHandler = (_, e) =>
            {
                if (e.OriginalSource is Border activityCloseBorder && activityCloseBorder == closeHost)
                    return;
                if (_isClosing)
                {
                    e.Handled = true;
                    return;
                }
                _activityClicked?.Invoke(_cardId);
                e.Handled = true;
            };
            MouseEnter += _mouseEnterHandler;
            MouseLeave += _mouseLeaveHandler;
            MouseLeftButtonUp += _mouseUpHandler;

            // Also reconcile unexpected native/owner closes. Normal fade and force-close
            // paths call NotifyClosed explicitly, while this event covers a window closed
            // by WPF or the owner before those paths reach their finally block.
            _closedHandler = (_, __) => NotifyClosed();
            Closed += _closedHandler;

            // Without WS_EX_NOACTIVATE each shown toast can steal keyboard focus
            // from Revit mid-typing. Clicks are still delivered; only activation is blocked.
            _sourceInitializedHandler = (_, __) => MakeNoActivate();
            SourceInitialized += _sourceInitializedHandler;
        }

        public void PlayEnterAnimation()
        {
            var duration = TimeSpan.FromMilliseconds(280);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

            _slideTransform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-24, 0, duration) { EasingFunction = ease });
            _scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
            _scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
            WipeBrand();
        }

        public void SetPosition(double top, double left)
        {
            // A previous animation's held value must not override a direct reflow.
            BeginAnimation(TopProperty, null);
            BeginAnimation(LeftProperty, null);
            Top = top;
            Left = left;
        }

        /// <summary>Reconcile the visible activity card without replaying enter animation.</summary>
        public void Update(ActivitySnapshot snapshot)
        {
            if (snapshot == null || snapshot.CardId != _cardId)
                return;
            ApplyActivitySnapshot(snapshot);
        }

        public void CloseImmediate()
        {
            if (_closedCallbackRaised)
                return;
            _isClosing = true;
            try { Close(); }
            finally { NotifyClosed(); }
        }

        /// <summary>Brand reveal: a lit front wipes left→right once while a narrow band of
        /// lighter letters sweeps through the wordmark in sync, then the wordmark stays lit.
        /// The pass starts ~1.3 s after the card appears — the delay for a reader's eye to
        /// land on a fresh toast (delayMs=1300). Replayed quickly on hover.</summary>
        private void WipeBrand(int delayMs = 1300)
        {
            var dur = TimeSpan.FromMilliseconds(800);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
            var wipe = new DoubleAnimation(-0.75, 0.75, dur)
            {
                BeginTime = TimeSpan.FromMilliseconds(delayMs),
                EasingFunction = ease
            };
            _brandSweep.BeginAnimation(TranslateTransform.XProperty, wipe);
            _shineSweep.BeginAnimation(TranslateTransform.XProperty, wipe);   // same timeline, two clocks
        }

        /// <summary>Dim→full-crest→rest alpha profile sliding across the wordmark.</summary>
        private static LinearGradientBrush BuildBrandMask(TranslateTransform sweep)
        {
            return new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5),
                RelativeTransform = sweep,
                GradientStops =
                {
                    new GradientStop(Dim(BrandSettleOpacity), 0.00),
                    new GradientStop(Dim(BrandSettleOpacity), 0.32),
                    new GradientStop(Dim(1.0), 0.44),
                    new GradientStop(Dim(BrandRestOpacity), 0.58),
                    new GradientStop(Dim(BrandRestOpacity), 1.00),
                }
            };
        }

        /// <summary>Narrow alpha band peaking on the brand front's crest so the glint
        /// and the wipe arrive together.</summary>
        private static LinearGradientBrush BuildShineMask(TranslateTransform sweep)
        {
            return new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5),
                RelativeTransform = sweep,
                GradientStops =
                {
                    new GradientStop(Dim(0.0), 0.00),
                    new GradientStop(Dim(0.0), 0.36),
                    new GradientStop(Dim(1.0), 0.44),
                    new GradientStop(Dim(0.0), 0.52),
                    new GradientStop(Dim(0.0), 1.00),
                }
            };
        }

        private static Color Dim(double alpha)
        {
            return Color.FromArgb((byte)Math.Round(alpha * 255), 0, 0, 0);
        }

        public void BeginClose()
        {
            if (_closedCallbackRaised)
                return;
            if (_isClosing)
                return;
            _isClosing = true;
            var duration = TimeSpan.FromMilliseconds(220);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };
            var fade = new DoubleAnimation(Opacity, 0, duration) { EasingFunction = ease };
            fade.Completed += (_, __) =>
            {
                try { Close(); }
                finally { NotifyClosed(); }
            };
            BeginAnimation(OpacityProperty, fade);
        }

        private void ApplyActivitySnapshot(ActivitySnapshot snapshot)
        {
            ViewModel = ToViewModel(snapshot);

            _root.BorderBrush = snapshot.HasFailure
                ? McpToastTheme.BuildAccentBrush(new McpToastViewModel { Success = false })
                : McpToastTheme.BuildAccentBrush(new McpToastViewModel { Success = true });
            _iconText.Text = snapshot.LatestSuccess ? "\uE73E" : "\uE783";
            _iconText.Foreground = snapshot.HasFailure ? McpToastTheme.Error : McpToastTheme.Primary;

            if (snapshot.IsStatus)
            {
                _titleText.Text = snapshot.Title ?? string.Empty;
                _categoryText.Visibility = Visibility.Collapsed;
                _bodyText.Text = snapshot.Body ?? string.Empty;
            }
            else
            {
                _titleText.Text = LocalizedOrFallback("toast.activity.header", "MCP · Activity");
                _categoryText.Visibility = Visibility.Visible;
                _categoryText.Text = $"✓ {snapshot.Succeeded}   ! {snapshot.Failed}   ▧ {snapshot.Images}";
                _categoryText.ToolTip = LocalizedOrFallback(
                    "toast.activity.counts.tooltip",
                    "Success / failed / images");
                var latest = LocalizedOrFallback("toast.activity.latest", "Latest");
                var latestBody = string.IsNullOrWhiteSpace(snapshot.Title)
                    ? snapshot.Body ?? string.Empty
                    : string.IsNullOrWhiteSpace(snapshot.Body)
                        ? snapshot.Title
                        : snapshot.Title + " · " + snapshot.Body;
                _bodyText.Text = string.IsNullOrWhiteSpace(latestBody)
                    ? string.Empty
                    : latest + ": " + latestBody;
            }

            _bodyText.ToolTip = string.IsNullOrEmpty(_bodyText.Text) ? null : _bodyText.Text;
            _bodyText.Visibility = string.IsNullOrEmpty(_bodyText.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
            _titleText.TextWrapping = TextWrapping.NoWrap;
            _titleText.TextTrimming = TextTrimming.CharacterEllipsis;
            _bodyText.TextWrapping = TextWrapping.NoWrap;
            _bodyText.MaxHeight = 18;
            _bodyText.TextTrimming = TextTrimming.CharacterEllipsis;
        }

        /// <summary>
        /// Captures the pointer location immediately before <see cref="Window.Show"/>.
        /// WPF may raise MouseEnter for a stationary cursor when the HWND appears; that
        /// synthetic event must not pause the aggregator's idle deadline.
        /// </summary>
        public void CapturePointerBaseline()
        {
            var point = _cursorPosition();
            if (IsCursorPointValid(point))
            {
                _hasPointerPosition = true;
                _lastPointerX = (int)point.X;
                _lastPointerY = (int)point.Y;
            }
        }

        private static McpToastViewModel ToViewModel(ActivitySnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            return new McpToastViewModel
            {
                Title = snapshot.Title,
                Summary = snapshot.Body,
                Detail = null,
                Success = snapshot.LatestSuccess && !snapshot.HasFailure,
                Kind = ToolActivityKind.Read
            };
        }

        private void NotifyClosed()
        {
            if (_closedCallbackRaised)
                return;
            _closedCallbackRaised = true;
            var activityClosed = _activityClosed;
            DetachHandlers();
            activityClosed?.Invoke(this, _cardId);
        }

        private static string LocalizedOrFallback(string key, string fallback)
        {
            var value = L.T(key);
            return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal)
                ? fallback
                : value;
        }

        private void DetachHandlers()
        {
            if (_handlersDetached)
                return;
            _handlersDetached = true;

            if (_closeHost != null)
            {
                _closeHost.MouseEnter -= _closeHostMouseEnterHandler;
                _closeHost.MouseLeave -= _closeHostMouseLeaveHandler;
                _closeHost.MouseLeftButtonUp -= _closeHostMouseUpHandler;
            }

            MouseEnter -= _mouseEnterHandler;
            MouseLeave -= _mouseLeaveHandler;
            MouseLeftButtonUp -= _mouseUpHandler;
            SourceInitialized -= _sourceInitializedHandler;
            Closed -= _closedHandler;

            // Stop any pending visual clocks before releasing callbacks. This prevents a
            // late animation completion from retaining a closed window or re-entering the
            // manager after a force-close.
            BeginAnimation(OpacityProperty, null);
            _brandSweep.BeginAnimation(TranslateTransform.XProperty, null);
            _shineSweep.BeginAnimation(TranslateTransform.XProperty, null);
            _activityClosed = null;
            _activityDismissed = null;
            _activityClicked = null;
            _activityPointerEntered = null;
            _activityPointerLeft = null;
        }

        private bool PointerPositionChanged()
        {
            var point = _cursorPosition();
            if (!IsCursorPointValid(point))
                return true;
            var x = (int)point.X;
            var y = (int)point.Y;
            if (_hasPointerPosition && x == _lastPointerX && y == _lastPointerY)
                return false;
            _hasPointerPosition = true;
            _lastPointerX = x;
            _lastPointerY = y;
            return true;
        }

        private static Point ReadCursorPosition()
        {
            return GetCursorPos(out var point)
                ? new Point(point.X, point.Y)
                : new Point(double.NaN, double.NaN);
        }

        private static bool IsCursorPointValid(Point point) =>
            !double.IsNaN(point.X) && !double.IsInfinity(point.X)
            && !double.IsNaN(point.Y) && !double.IsInfinity(point.Y);

        private void MakeNoActivate()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero)
                    return;
                var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64()
                              | WsExNoActivate | WsExToolWindow;
                SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(exStyle));
            }
            catch
            {
                // Best-effort — toast still works without the style.
            }
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT point);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }
    }
}
