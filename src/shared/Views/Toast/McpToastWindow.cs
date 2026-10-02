using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using RvtMcp.Plugin.Localization;

namespace RvtMcp.Plugin.Views.Toast
{
    internal sealed class McpToastWindow : Window
    {
        private const double CardWidth = 300;
        // Counter row width: the card minus the accent border (6), content margins (10 + 12)
        // and the icon column (24). The spare width is shared evenly around the separators.
        private const double CounterRowWidth = CardWidth - 52;
        // The capture sits in a fixed frame with the image centred on both axes, so the card never
        // changes size between captures of different shapes and only opens or closes as a whole.
        private const double ThumbnailFrameHeight = 120;
        private const double ThumbnailGap = 6;
        private const double ThumbnailRowHeight = ThumbnailGap + ThumbnailFrameHeight;
        private const int ThumbnailOpenMs = 260;
        private const int ThumbnailCloseMs = 240;
        private const int ThumbnailFadeMs = 300;
        private const double BrandSettleOpacity = 0.8;
        private const int BrandRevealDelayMs = 100;
        private const int BrandRevealDurationMs = 500;
        private const int BrandHideDurationMs = 200;
        private const int DetailsRevealDelayMs = 200;
        private const int DetailsLeaveDelayMs = 120;
        private const int DetailsOpenMs = 260;
        private const int DetailsCloseMs = 220;
        private const int GwlExStyle = -20;
        private const long WsExNoActivate = 0x08000000L;
        private const long WsExToolWindow = 0x00000080L;

        private readonly TextBlock _iconText;
        private readonly TextBlock _titleText;
        private readonly TextBlock _toolText;
        private readonly TextBlock _bodyText;
        private readonly Viewbox _counterRow;
        private readonly RollingToastNumber _successCount = new RollingToastNumber();
        private readonly RollingToastNumber _failedCount = new RollingToastNumber();
        private readonly RollingToastNumber _captureCount = new RollingToastNumber();
        private readonly TextBlock _successLabel;
        private readonly TextBlock _failedLabel;
        private readonly TextBlock _captureLabel;
        private readonly TextBlock _brandText;
        private readonly TextBlock _brandShine;
        private readonly Grid _brandRow;
        private readonly Grid _brandCell;
        private readonly string _instanceIdentity;
        private readonly Border _detailsRow;
        private readonly ToastActivityPanel _detailsPanel;
        private readonly TranslateTransform _detailsShift = new TranslateTransform(0, -6);
        private DispatcherTimer _detailsRevealTimer;
        private DispatcherTimer _detailsHideTimer;
        private bool _detailsRevealed;
        private bool _detailsHiding;
        private int _detailsGeneration;
        private MouseButtonEventHandler _detailsUpHandler;
        private readonly Border _thumbnailRow;
        private readonly Border _thumbnailHost;
        private readonly Image _thumbnailImage;
        private readonly Image _thumbnailBack;
        private string _thumbnailPath;
        private bool _thumbnailShown;
        private int _thumbnailStateGeneration;
        private int _thumbnailSwapGeneration;
        private readonly TranslateTransform _brandSweep = new TranslateTransform(-0.75, 0);
        private readonly TranslateTransform _shineSweep = new TranslateTransform(-0.75, 0);
        private readonly Func<Point> _cursorPosition;
        private readonly Func<bool> _motionEnabled;
        private readonly Border _root;
        private readonly TranslateTransform _slideTransform;
        private readonly ScaleTransform _scaleTransform;
        private Border _closeHost;
        private MouseButtonEventHandler _closeHostMouseUpHandler;
        private MouseEventHandler _mouseEnterHandler;
        private MouseEventHandler _mouseMoveHandler;
        private MouseEventHandler _mouseLeaveHandler;
        private DispatcherTimer _brandRevealTimer;
        private int _brandHideGeneration;
        private bool _brandPointerOver;
        private bool _brandPointerMoved;
        private bool _brandRevealed;
        private bool _brandHiding;
        private MouseButtonEventHandler _mouseUpHandler;
        private MouseButtonEventHandler _thumbnailUpHandler;
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
        private bool _showBranding = true;
        private ActivitySnapshot _lastSnapshot;
        private DockPanel _dragHeader;
        private bool _dragEnabled;
        private bool _alignRight;
        private bool _growUp;
        private bool _dragPending;
        private bool _dragStarted;
        private Point _dragPointerStart;
        private Point _dragWindowStart;
        private int _positionGeneration;
        internal bool IsDragging => _dragPending;
        internal bool HasExpandedRows => _detailsRevealed || _thumbnailShown;
        internal bool IsPositionAnimating { get; private set; }
        internal Action<double> PrepareExpansion { get; set; }
        internal Func<Point, Point> ConstrainDrag { get; set; }
        internal Action<Point> DragCompleted { get; set; }

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
            Func<Point> cursorPosition = null,
            Func<bool> motionEnabled = null,
            string instanceIdentity = null)
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
            _motionEnabled = motionEnabled ?? (() => SystemParameters.ClientAreaAnimation);
            _instanceIdentity = instanceIdentity;

            FontFamily = McpToastTheme.UiFont;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
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
                BorderBrush = McpToastTheme.BuildAccentBrush(new McpToastViewModel { Success = !snapshot.HasFailure }),
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

            var content = new Grid { Margin = new Thickness(10, 10, 12, 10) };
            // Title (product + Revit year), then the latest tool or status title.
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // Status summary and activity counters share one fixed-height body row.
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
            // Recent outcomes only expand on deliberate hover; then capture and branding.
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new DockPanel { LastChildFill = true };
            _dragHeader = header;
            header.PreviewMouseLeftButtonDown += OnHeaderMouseDown;
            PreviewMouseMove += OnDragMouseMove;
            PreviewMouseLeftButtonUp += OnDragMouseUp;
            LostMouseCapture += OnDragCaptureLost;

            _iconText = new TextBlock
            {
                FontFamily = McpToastTheme.IconFont,
                FontSize = 16,
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
            _closeHostMouseUpHandler = (_, e) =>
            {
                e.Handled = true;
                _activityDismissed?.Invoke(_cardId);
            };
            closeHost.MouseLeftButtonUp += _closeHostMouseUpHandler;
            DockPanel.SetDock(closeHost, Dock.Right);
            header.Children.Add(closeHost);

            _titleText = new TextBlock
            {
                Text = IdentityTitle,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Foreground = McpToastTheme.Text,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            header.Children.Add(_titleText);

            Grid.SetRow(header, 0);
            content.Children.Add(header);

            _toolText = new TextBlock
            {
                Margin = new Thickness(24, 1, 0, 0),
                FontSize = 12,
                Foreground = McpToastTheme.Text,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetRow(_toolText, 1);
            content.Children.Add(_toolText);

            var body = new Grid { Margin = new Thickness(24, 2, 0, 0) };
            // group · group · group: the star columns split the spare width evenly, so the
            // separators sit centred between the groups and the row spans the card.
            var counters = new Grid { MinWidth = CounterRowWidth };
            for (var i = 0; i < 5; i++)
            {
                counters.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = i % 2 == 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star)
                });
            }
            _successLabel = AddCounter(counters, 0, _successCount);
            AddCounterSeparator(counters, 1);
            _failedLabel = AddCounter(counters, 2, _failedCount);
            AddCounterSeparator(counters, 3);
            _captureLabel = AddCounter(counters, 4, _captureCount);
            // Long translations and large counts shrink within the same row; card size never changes.
            _counterRow = new Viewbox
            {
                Child = counters,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            body.Children.Add(_counterRow);
            _bodyText = new TextBlock
            {
                FontSize = 12,
                Foreground = McpToastTheme.Text,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            body.Children.Add(_bodyText);
            Grid.SetRow(body, 2);
            content.Children.Add(body);

            _detailsPanel = new ToastActivityPanel(_motionEnabled)
            {
                Opacity = 0, RenderTransform = _detailsShift
            };
            _detailsRow = new Border
            {
                Height = 0, ClipToBounds = true, Visibility = Visibility.Collapsed,
                Child = _detailsPanel, Cursor = Cursors.Arrow
            };
            // Reading/scrolling the preview must not invoke the card's dismiss action.
            _detailsUpHandler = (_, e) => e.Handled = true;
            _detailsRow.MouseLeftButtonUp += _detailsUpHandler;
            Grid.SetRow(_detailsRow, 3);
            content.Children.Add(_detailsRow);

            _thumbnailImage = CreateThumbnailImage();
            _thumbnailBack = CreateThumbnailImage();
            var thumbnailStack = new Grid();
            thumbnailStack.Children.Add(_thumbnailBack);
            thumbnailStack.Children.Add(_thumbnailImage);
            _thumbnailHost = new Border
            {
                Margin = new Thickness(24, ThumbnailGap, 0, 0),
                Height = ThumbnailFrameHeight,
                Padding = new Thickness(3),
                CornerRadius = new CornerRadius(4),
                BorderBrush = McpToastTheme.MutedAccent,
                BorderThickness = new Thickness(1),
                Background = McpToastTheme.ThumbnailBackground,
                Child = thumbnailStack,
                Cursor = Cursors.Hand
            };
            _thumbnailUpHandler = (_, e) =>
            {
                e.Handled = true;
                OpenThumbnail();
            };
            _thumbnailHost.MouseLeftButtonUp += _thumbnailUpHandler;
            // The row clips the frame while it opens and closes; its height is what the card follows.
            _thumbnailRow = new Border
            {
                Height = 0,
                ClipToBounds = true,
                Child = _thumbnailHost,
                Visibility = Visibility.Collapsed
            };
            Grid.SetRow(_thumbnailRow, 4);
            content.Children.Add(_thumbnailRow);

            _brandRow = new Grid { Margin = new Thickness(24, 5, 0, 0) };

            _brandText = new TextBlock
            {
                // Hidden until a real hover. The reveal mask samples fully transparent
                // while the sweep sits at -0.75, so a collapsed row that is shown early
                // still draws no letters.
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = BrandAssets.ProductTag,
                OpacityMask = BuildBrandRevealMask(_brandSweep),
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

            _brandCell = new Grid
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            _brandCell.Children.Add(_brandText);
            _brandCell.Children.Add(_brandShine);
            _brandRow.Children.Add(_brandCell);

            Grid.SetRow(_brandRow, 5);
            content.Children.Add(_brandRow);
            ParkBrandRow();

            ApplyActivitySnapshot(snapshot);

            _root.Child = content;
            _root.Cursor = Cursors.Hand;
            Content = _root;

            _mouseEnterHandler = (_, __) =>
            {
                var moved = PointerPositionChanged();
                if (moved)
                    _activityPointerEntered?.Invoke(_cardId);
                _brandPointerOver = true;
                if (moved)
                {
                    _brandPointerMoved = true;
                    ScheduleBrandReveal();
                    ScheduleDetailsReveal();
                }
            };
            // A card that opens under a still cursor ignores that enter. The first
            // real movement while the pointer remains inside is the hover.
            _mouseMoveHandler = (_, __) =>
            {
                if (!_brandPointerOver || !PointerPositionChanged())
                    return;
                // The card opened under a still cursor, so MouseEnter did not count.
                // This first real movement is the hover: pause idle as well as reveal the wordmark.
                _activityPointerEntered?.Invoke(_cardId);
                _brandPointerMoved = true;
                ScheduleBrandReveal();
                ScheduleDetailsReveal();
            };
            _mouseLeaveHandler = (_, __) =>
            {
                if (_dragPending || !PointerPositionChanged())
                    return;
                _activityPointerLeft?.Invoke(_cardId);
                _brandPointerOver = false;
                _brandPointerMoved = false;
                HideBrand(immediate: false);
                ScheduleDetailsHide();
            };
            _mouseUpHandler = (_, e) =>
            {
                if (e.Handled)
                    return;
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
            MouseMove += _mouseMoveHandler;
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
            if (!_motionEnabled())
            {
                _slideTransform.X = 0;
                _scaleTransform.ScaleX = _scaleTransform.ScaleY = 1;
                Opacity = 1;
                return;
            }
            var duration = TimeSpan.FromMilliseconds(280);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

            _slideTransform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(_alignRight ? 24 : -24, 0, duration) { EasingFunction = ease });
            _scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
            _scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        }

        internal void SetPositionPreferences(bool dragEnabled, bool alignRight, bool growUp)
        {
            _dragEnabled = dragEnabled;
            _alignRight = alignRight;
            _growUp = growUp;
            _root.RenderTransformOrigin = new Point(alignRight ? 1 : 0, growUp ? 1 : 0);
            _dragHeader.Cursor = dragEnabled ? Cursors.SizeAll : Cursors.Hand;
            if (!dragEnabled) CancelHeaderDrag();
        }

        internal void MoveTo(double top, double left)
        {
            var fromTop = Top;
            var fromLeft = Left;
            SetPosition(top, left);
            if (!_motionEnabled() || !IsVisible) return;
            var generation = ++_positionGeneration;
            IsPositionAnimating = true;
            var duration = TimeSpan.FromMilliseconds(260);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var move = new DoubleAnimation(fromTop, top, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            move.Completed += (_, __) => { if (generation == _positionGeneration) IsPositionAnimating = false; };
            BeginAnimation(TopProperty, move);
            BeginAnimation(LeftProperty, new DoubleAnimation(fromLeft, left, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        }

        public void SetPosition(double top, double left)
        {
            if (double.IsNaN(top) || double.IsInfinity(top) || double.IsNaN(left) || double.IsInfinity(left)) return;
            ++_positionGeneration;
            IsPositionAnimating = false;
            // A previous animation's held value must not override a direct reflow.
            BeginAnimation(TopProperty, null);
            BeginAnimation(LeftProperty, null);
            Top = top;
            Left = left;
        }

        private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_dragEnabled || _isClosing || IsWithin(e.OriginalSource as DependencyObject, _closeHost)) return;
            BeginHeaderDrag(_cursorPosition());
            if (_dragPending) e.Handled = true;
        }

        private static bool IsWithin(DependencyObject child, DependencyObject ancestor)
        {
            while (child != null)
            {
                if (ReferenceEquals(child, ancestor)) return true;
                child = child is Visual ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child);
            }
            return false;
        }

        internal void BeginHeaderDrag(Point screenPoint)
        {
            if (!_dragEnabled || _isClosing || !IsCursorPointValid(screenPoint)) return;
            SetPosition(Top, Left);
            _dragPointerStart = screenPoint;
            _dragWindowStart = new Point(Left, Top);
            // Capture may synchronously route synthetic move/lost-capture events.
            // Do not expose a pending gesture until capture has actually succeeded.
            if (!CaptureMouse()) return;
            _dragPending = true;
            _dragStarted = false;
            CancelDetailsRevealTimer();
            CancelDetailsHideTimer();
            _activityPointerEntered?.Invoke(_cardId);
        }

        private Vector ScreenDelta(Point screenPoint)
        {
            var delta = screenPoint - _dragPointerStart;
            var source = PresentationSource.FromVisual(this);
            return source?.CompositionTarget != null ? source.CompositionTarget.TransformFromDevice.Transform(delta) : delta;
        }

        internal void ContinueHeaderDrag(Point screenPoint)
        {
            if (!_dragPending || !IsCursorPointValid(screenPoint)) return;
            var delta = ScreenDelta(screenPoint);
            if (!_dragStarted)
            {
                if (!ToastPlacement.DragThreshold(delta.X, delta.Y,
                    SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance)) return;
                _dragStarted = true;
                HideDetails(immediate: true);
                HideBrand(immediate: true);
                UpdateLayout();
            }
            var point = new Point(_dragWindowStart.X + delta.X, _dragWindowStart.Y + delta.Y);
            if (ConstrainDrag != null) point = ConstrainDrag(point);
            SetPosition(point.Y, point.X);
        }

        private void OnDragMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragPending) return;
            if (e.LeftButton != MouseButtonState.Pressed) { CancelHeaderDrag(); return; }
            ContinueHeaderDrag(_cursorPosition());
            e.Handled = true;
        }

        internal bool FinishHeaderDrag()
        {
            if (!_dragPending) return false;
            var dragged = _dragStarted;
            _dragPending = _dragStarted = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            if (dragged) DragCompleted?.Invoke(new Point(Left, Top));
            ReconcileDragPointer();
            return dragged;
        }

        private void OnDragMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (FinishHeaderDrag()) e.Handled = true;
        }

        internal void CancelHeaderDrag()
        {
            if (!_dragPending) return;
            _dragPending = _dragStarted = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            ReconcileDragPointer();
        }

        private void OnDragCaptureLost(object sender, MouseEventArgs e) => CancelHeaderDrag();

        private void ReconcileDragPointer()
        {
            if (!IsMouseOver) _activityPointerLeft?.Invoke(_cardId);
            else
            {
                _brandPointerOver = _brandPointerMoved = true;
                ScheduleBrandReveal();
                ScheduleDetailsReveal();
            }
        }

        /// <summary>Reconcile the visible activity card without replaying enter animation.</summary>
        public void Update(ActivitySnapshot snapshot)
        {
            if (snapshot == null || snapshot.CardId != _cardId)
                return;
            ApplyActivitySnapshot(snapshot);
        }

        /// <summary>
        /// Arm or remove the hover wordmark. Turning it on leaves the card clean
        /// until the pointer actually moves onto it. Turning it off removes the
        /// wordmark immediately.
        /// </summary>
        public void SetShowBranding(bool show)
        {
            if (_showBranding == show || _closedCallbackRaised)
                return;
            _showBranding = show;
            if (!show)
            {
                HideBrand(immediate: true);
                return;
            }
            if (!_brandRevealed)
                ParkBrandRow();
            if (_brandPointerOver && _brandPointerMoved)
                ScheduleBrandReveal();
        }

        /// <summary>
        /// Re-applies localized chrome to the visible card after an L.Changed swap.
        /// This deliberately does not go through the aggregator: counts, CardId,
        /// deadline and hover state remain untouched. Status cards may provide a
        /// late-bound title/body resolver so their localized copy changes as well.
        /// </summary>
        public void RefreshLocalization()
        {
            if (_closedCallbackRaised || _lastSnapshot == null)
                return;

            ApplyActivitySnapshot(_lastSnapshot, preserveSnapshot: true);
            _detailsPanel.RefreshLocalization();
        }

        public void CloseImmediate()
        {
            if (_closedCallbackRaised)
                return;
            _isClosing = true;
            try { Close(); }
            finally { NotifyClosed(); }
        }

        /// <summary>
        /// A real pointer movement while branding is armed. Quick passes cancel
        /// during the delay, so the wordmark never flashes. A second movement
        /// while the letters are already up does not replay the wipe.
        /// </summary>
        private void ScheduleBrandReveal()
        {
            if (_dragPending || !_showBranding || !_brandPointerOver || _closedCallbackRaised || _isClosing)
                return;
            if (_brandRevealed && !_brandHiding)
                return;
            if (_brandHiding)
                ParkBrandRow();
            if (!_motionEnabled())
            {
                ShowBrandSettled();
                return;
            }
            if (_brandRevealTimer != null)
                return;

            _brandRevealTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(BrandRevealDelayMs) };
            _brandRevealTimer.Tick += (_, __) =>
            {
                CancelBrandRevealTimer();
                if (!_showBranding || !_brandPointerOver || _closedCallbackRaised || _isClosing)
                    return;
                BeginBrandReveal();
            };
            _brandRevealTimer.Start();
        }

        /// <summary>
        /// Intent delay is independent of branding and reduced motion. A passing pointer
        /// or a card appearing underneath a still pointer never exposes outcome content.
        /// </summary>
        private void ScheduleDetailsReveal()
        {
            CancelDetailsHideTimer();
            if (_dragPending || !_brandPointerOver || !_brandPointerMoved || _closedCallbackRaised || _isClosing
                || _lastSnapshot == null || _lastSnapshot.IsStatus || _lastSnapshot.RecentEntries.Count == 0)
                return;
            if (_detailsRevealed)
            {
                // Reverse an interrupted close from its current height, without resetting
                // the reader's scroll position or replaying a complete entrance.
                if (_detailsHiding)
                    BeginDetailsReveal();
                return;
            }
            if (_detailsRevealTimer != null)
                return;
            _detailsRevealTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(DetailsRevealDelayMs)
            };
            _detailsRevealTimer.Tick += (_, __) =>
            {
                CancelDetailsRevealTimer();
                if (_brandPointerOver && _brandPointerMoved && !_isClosing && !_closedCallbackRaised)
                    BeginDetailsReveal();
            };
            _detailsRevealTimer.Start();
        }

        private void BeginDetailsReveal()
        {
            var snapshot = _lastSnapshot;
            if (snapshot == null || snapshot.IsStatus || snapshot.RecentEntries.Count == 0)
                return;
            var newReading = !_detailsRevealed;
            if (newReading)
            {
                PrepareExpansion?.Invoke(157);
                _detailsShift.Y = _growUp ? 6 : -6;
                _detailsPanel.SetEntries(snapshot.RecentEntries);
            }
            // New results refresh this view separately. A reversal/height retarget must
            // preserve its scroll position; only a genuinely new hover starts at the tail.
            _detailsRevealed = true;
            _detailsHiding = false;
            var generation = ++_detailsGeneration;
            _detailsPanel.Measure(new Size(CardWidth - 28, double.PositiveInfinity));
            var height = _detailsPanel.DesiredSize.Height;
            _detailsRow.Visibility = Visibility.Visible;
            if (newReading)
                _detailsPanel.ScrollToLatest();
            if (!_motionEnabled())
            {
                SettleDetailsOpen(height);
                return;
            }
            var duration = TimeSpan.FromMilliseconds(DetailsOpenMs);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var open = new DoubleAnimation(_detailsRow.Height, height, duration) { EasingFunction = ease };
            open.Completed += (_, __) =>
            {
                if (generation == _detailsGeneration && !_closedCallbackRaised)
                    SettleDetailsOpen(height);
            };
            _detailsRow.BeginAnimation(HeightProperty, open);
            _detailsPanel.BeginAnimation(OpacityProperty,
                new DoubleAnimation(_detailsPanel.Opacity, 1, duration) { EasingFunction = ease });
            _detailsShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(_detailsShift.Y, 0, duration) { EasingFunction = ease });
        }

        private void SettleDetailsOpen(double height)
        {
            _detailsRow.BeginAnimation(HeightProperty, null);
            _detailsPanel.BeginAnimation(OpacityProperty, null);
            _detailsShift.BeginAnimation(TranslateTransform.YProperty, null);
            _detailsRow.Height = height;
            _detailsPanel.Opacity = 1;
            _detailsShift.Y = 0;
        }

        private void ScheduleDetailsHide()
        {
            CancelDetailsRevealTimer();
            if (!_detailsRevealed || _detailsHideTimer != null)
                return;
            // Grace period avoids collapsing when crossing into the newly expanded area.
            _detailsHideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(DetailsLeaveDelayMs)
            };
            _detailsHideTimer.Tick += (_, __) =>
            {
                CancelDetailsHideTimer();
                if (!_brandPointerOver)
                    HideDetails(immediate: false);
            };
            _detailsHideTimer.Start();
        }

        private void HideDetails(bool immediate)
        {
            CancelDetailsRevealTimer();
            CancelDetailsHideTimer();
            var generation = ++_detailsGeneration;
            if (immediate || !_motionEnabled() || !_detailsRevealed)
            {
                SettleDetailsClosed();
                return;
            }
            _detailsHiding = true;
            var duration = TimeSpan.FromMilliseconds(DetailsCloseMs);
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            var close = new DoubleAnimation(_detailsRow.Height, 0, duration) { EasingFunction = ease };
            close.Completed += (_, __) =>
            {
                if (generation == _detailsGeneration)
                    SettleDetailsClosed();
            };
            _detailsRow.BeginAnimation(HeightProperty, close);
            _detailsPanel.BeginAnimation(OpacityProperty,
                new DoubleAnimation(_detailsPanel.Opacity, 0, duration) { EasingFunction = ease });
            _detailsShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(_detailsShift.Y, _growUp ? 6 : -6, duration) { EasingFunction = ease });
        }

        private void SettleDetailsClosed()
        {
            _detailsRow.BeginAnimation(HeightProperty, null);
            _detailsPanel.BeginAnimation(OpacityProperty, null);
            _detailsShift.BeginAnimation(TranslateTransform.YProperty, null);
            _detailsRow.Height = 0;
            _detailsRow.Visibility = Visibility.Collapsed;
            _detailsPanel.Opacity = 0;
            _detailsPanel.SetEntries(ToastActivityLog.Freeze(null));
            _detailsShift.Y = _growUp ? 6 : -6;
            _detailsRevealed = _detailsHiding = false;
        }

        private void CancelDetailsRevealTimer()
        {
            _detailsRevealTimer?.Stop();
            _detailsRevealTimer = null;
        }

        private void CancelDetailsHideTimer()
        {
            _detailsHideTimer?.Stop();
            _detailsHideTimer = null;
        }

        private void BeginBrandReveal()
        {
            _brandHideGeneration++;
            _brandHiding = false;
            _brandCell.BeginAnimation(UIElement.OpacityProperty, null);
            _brandCell.Opacity = 1;
            _brandRow.Visibility = Visibility.Visible;
            _brandRevealed = true;
            _brandText.OpacityMask = BuildBrandRevealMask(_brandSweep);
            _brandShine.OpacityMask = BuildShineMask(_shineSweep);
            StopBrandSweep();
            _brandSweep.X = _shineSweep.X = -0.75;
            var wipe = new DoubleAnimation(-0.75, 0.75, TimeSpan.FromMilliseconds(BrandRevealDurationMs))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            _brandSweep.BeginAnimation(TranslateTransform.XProperty, wipe);
            _shineSweep.BeginAnimation(TranslateTransform.XProperty, wipe);
        }

        private void ShowBrandSettled()
        {
            _brandHideGeneration++;
            _brandHiding = false;
            CancelBrandRevealTimer();
            StopBrandSweep();
            _brandCell.BeginAnimation(UIElement.OpacityProperty, null);
            _brandCell.Opacity = 1;
            _brandRow.Visibility = Visibility.Visible;
            _brandRevealed = true;
            _brandText.OpacityMask = new SolidColorBrush(Dim(BrandSettleOpacity));
            _brandShine.OpacityMask = Brushes.Transparent;
        }

        private void HideBrand(bool immediate)
        {
            CancelBrandRevealTimer();
            StopBrandSweep();
            if (immediate || !_motionEnabled() || !_brandRevealed)
            {
                ParkBrandRow();
                return;
            }

            _brandHiding = true;
            var generation = ++_brandHideGeneration;
            var fade = new DoubleAnimation(_brandCell.Opacity, 0, TimeSpan.FromMilliseconds(BrandHideDurationMs))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            fade.Completed += (_, __) =>
            {
                if (generation != _brandHideGeneration)
                    return;
                ParkBrandRow();
            };
            _brandCell.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        /// <summary>
        /// Branding on reserves the bottom row (hover-revealed wordmark) and
        /// branding off removes it. Wordmark visibility is driven by its opacity
        /// mask, so a visible row still draws no letters until a real hover.
        /// </summary>
        private void ParkBrandRow()
        {
            _brandHideGeneration++;
            _brandHiding = false;
            _brandRevealed = false;
            CancelBrandRevealTimer();
            StopBrandSweep();
            _brandCell.BeginAnimation(UIElement.OpacityProperty, null);
            _brandCell.Opacity = 1;
            // The row stays reserved (Hidden) while branding is on, so park the wipe
            // masks too — a mid-sweep transform or settled solid mask would
            // otherwise keep half-revealed letters on screen.
            _brandSweep.X = _shineSweep.X = -0.75;
            _brandText.OpacityMask = BuildBrandRevealMask(_brandSweep);
            _brandShine.OpacityMask = BuildShineMask(_shineSweep);
            _brandRow.Visibility = _showBranding ? Visibility.Hidden : Visibility.Collapsed;
        }

        /// <summary>"rvt-mcp 2027": names the gateway and the Revit year, so cards from several
        /// MCP gateways or Revit years running side by side are told apart.</summary>
        private string IdentityTitle => string.IsNullOrWhiteSpace(_instanceIdentity)
            ? BrandAssets.ProductName
            : _instanceIdentity;

        private void CancelBrandRevealTimer()
        {
            if (_brandRevealTimer == null)
                return;
            _brandRevealTimer.Stop();
            _brandRevealTimer = null;
        }

        private void StopBrandSweep()
        {
            _brandSweep.BeginAnimation(TranslateTransform.XProperty, null);
            _shineSweep.BeginAnimation(TranslateTransform.XProperty, null);
        }

        /// <summary>
        /// Settled letters on the left, a hole at the crest so the lighter layer
        /// shows through, and fully transparent ahead of the wipe.
        /// </summary>
        private static LinearGradientBrush BuildBrandRevealMask(TranslateTransform sweep)
        {
            return new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5),
                RelativeTransform = sweep,
                GradientStops =
                {
                    new GradientStop(Dim(BrandSettleOpacity), 0.00),
                    new GradientStop(Dim(BrandSettleOpacity), 0.36),
                    new GradientStop(Dim(0.0), 0.44),
                    new GradientStop(Dim(0.0), 0.52),
                    new GradientStop(Dim(0.0), 1.00),
                }
            };
        }

        private static TextBlock AddCounter(Grid row, int column, RollingToastNumber number)
        {
            var group = new StackPanel { Orientation = Orientation.Horizontal };
            group.Children.Add(number);
            // Same size as the number, both centred in the slot height, so they share a baseline.
            var label = new TextBlock
            {
                FontSize = RollingToastNumber.TextSize,
                Foreground = McpToastTheme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };
            group.Children.Add(label);
            Grid.SetColumn(group, column);
            row.Children.Add(group);
            return label;
        }

        private static void AddCounterSeparator(Grid row, int column)
        {
            var separator = new TextBlock
            {
                Text = "\u00B7",
                FontSize = RollingToastNumber.TextSize,
                FontWeight = FontWeights.Bold,
                Foreground = McpToastTheme.MutedAccent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                // Minimum gap when large counts leave no spare width.
                Margin = new Thickness(6, 0, 6, 0)
            };
            Grid.SetColumn(separator, column);
            row.Children.Add(separator);
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
            if (!_motionEnabled())
            {
                CloseImmediate();
                return;
            }
            _isClosing = true;
            CancelDetailsRevealTimer();
            CancelDetailsHideTimer();
            var duration = TimeSpan.FromMilliseconds(220);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };
            CancelHeaderDrag();
            _slideTransform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(_slideTransform.X, _alignRight ? 24 : -24, duration) { EasingFunction = ease });
            var fade = new DoubleAnimation(Opacity, 0, duration) { EasingFunction = ease };
            fade.Completed += (_, __) =>
            {
                try { Close(); }
                finally { NotifyClosed(); }
            };
            BeginAnimation(OpacityProperty, fade);
        }

        private void ApplyActivitySnapshot(ActivitySnapshot snapshot, bool preserveSnapshot = false)
        {
            if (!preserveSnapshot)
                _lastSnapshot = snapshot;

            ViewModel = ToViewModel(snapshot);
            _root.BorderBrush = McpToastTheme.BuildAccentBrush(new McpToastViewModel { Success = !snapshot.HasFailure });
            _iconText.Text = snapshot.LatestSuccess ? "\uE73E" : "\uE783";
            _iconText.Foreground = snapshot.HasFailure ? McpToastTheme.Error : McpToastTheme.Primary;

            _titleText.Text = IdentityTitle;
            var animate = !preserveSnapshot && IsVisible && _motionEnabled();
            if (snapshot.IsStatus)
            {
                var status = ResolveStatusText(snapshot);
                _toolText.Text = status.Title;
                _counterRow.Visibility = Visibility.Collapsed;
                _bodyText.Visibility = Visibility.Visible;
                _bodyText.Text = status.Body;
                _bodyText.ToolTip = status.Body;
                HideDetails(immediate: true);
                ApplyThumbnail(null, animate);
            }
            else
            {
                _toolText.Text = snapshot.Title ?? string.Empty;
                _counterRow.Visibility = Visibility.Visible;
                _bodyText.Visibility = Visibility.Collapsed;
                _successLabel.Text = LocalizedOrFallback("toast.activity.success", "Success");
                _failedLabel.Text = LocalizedOrFallback("toast.activity.failed", "Failed");
                _captureLabel.Text = LocalizedOrFallback("toast.activity.capture", "Capture");
                _successCount.SetValue(snapshot.Succeeded, McpToastTheme.Primary, animate);
                _failedCount.SetValue(snapshot.Failed,
                    snapshot.Failed > 0 ? McpToastTheme.Error : McpToastTheme.TextSecondary, animate);
                _captureCount.SetValue(snapshot.Images, McpToastTheme.Text, animate);
                // No auto-popup result tooltip: outcomes require a deliberate hover.
                _counterRow.ToolTip = null;
                if (_detailsRevealed && !_isClosing)
                {
                    var previousRows = Math.Min(ToastActivityPanel.VisibleEntryCount, _detailsPanel.Entries.Count);
                    _detailsPanel.UpdateEntries(snapshot.RecentEntries);
                    var currentRows = Math.Min(ToastActivityPanel.VisibleEntryCount, _detailsPanel.Entries.Count);
                    // The first/second incoming result may grow the viewport. Retarget from
                    // the current animated height; never expose rows behind an old fixed clip.
                    if (previousRows != currentRows && !_detailsHiding)
                        BeginDetailsReveal();
                }
                if (_brandPointerOver && _brandPointerMoved)
                    ScheduleDetailsReveal();
                System.Windows.Automation.AutomationProperties.SetName(_counterRow,
                    $"{snapshot.Succeeded} {_successLabel.Text}, {snapshot.Failed} {_failedLabel.Text}, {snapshot.Images} {_captureLabel.Text}");
                ApplyThumbnail(snapshot.ImagePath, animate);
            }

            _titleText.ToolTip = _titleText.Text;
            _titleText.TextWrapping = TextWrapping.NoWrap;
            _titleText.TextTrimming = TextTrimming.CharacterEllipsis;
            _toolText.ToolTip = snapshot.IsStatus
                ? _toolText.Text + "\n" + _bodyText.Text
                : snapshot.RecentEntries.Count > 0
                    ? snapshot.RecentEntries[snapshot.RecentEntries.Count - 1].TooltipText
                    : ToastActivityEntry.Compact(snapshot.Title, ToastActivityEntry.TitleLimit)
                        + "\n" + ToastActivityEntry.Compact(snapshot.Body, ToastActivityEntry.BodyLimit);
        }

        /// <summary>
        /// Shows a small preview of the held capture between the counter row and the
        /// footer. Only runs when the path changed — a counter-only update keeps the
        /// already-decoded bitmap instead of reloading the file on every result.
        /// The frame opens and closes with the card following it, and a new capture
        /// replaces the old one with a cross-fade.
        /// </summary>
        private void ApplyThumbnail(string path, bool animate)
        {
            if (string.Equals(path, _thumbnailPath, StringComparison.Ordinal))
                return;
            _thumbnailPath = path;

            var bitmap = LoadThumbnail(path);
            if (bitmap == null)
                HideThumbnail(animate);
            else if (_thumbnailShown)
                SwapThumbnail(bitmap, animate);
            else
                RevealThumbnail(bitmap, animate);
        }

        private static BitmapSource LoadThumbnail(string path)
        {
            if (string.IsNullOrEmpty(path) || !ToastContentBuilder.IsSafeImagePath(path))
                return null;

            var bytes = ToastThumbnailLoader.TryLoadBytes(path, 8 * 1024 * 1024);
            if (bytes == null)
                return null;

            try
            {
                using (var stream = new MemoryStream(bytes))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 300;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
            }
            catch
            {
                return null;
            }
        }

        private static Image CreateThumbnailImage()
        {
            var image = new Image
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }

        /// <summary>No capture → capture: the frame grows from nothing while it fades in.</summary>
        private void RevealThumbnail(BitmapSource bitmap, bool animate)
        {
            PrepareExpansion?.Invoke(ThumbnailRowHeight);
            var generation = ++_thumbnailStateGeneration;
            _thumbnailShown = true;
            _thumbnailSwapGeneration++;
            _thumbnailBack.BeginAnimation(OpacityProperty, null);
            _thumbnailBack.Source = null;
            _thumbnailImage.BeginAnimation(OpacityProperty, null);
            _thumbnailImage.Opacity = 1;
            _thumbnailImage.Source = bitmap;
            _thumbnailHost.ToolTip = LocalizedOrFallback("toast.activity.open_image", "Open image");

            if (_thumbnailRow.Visibility != Visibility.Visible)
            {
                _thumbnailRow.Height = 0;
                _thumbnailHost.Opacity = 0;
                _thumbnailRow.Visibility = Visibility.Visible;
            }

            if (!animate)
            {
                SettleThumbnailOpen();
                return;
            }

            var open = new DoubleAnimation(ThumbnailRowHeight, TimeSpan.FromMilliseconds(ThumbnailOpenMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            open.Completed += (_, __) =>
            {
                if (generation == _thumbnailStateGeneration)
                    SettleThumbnailOpen();
            };
            _thumbnailRow.BeginAnimation(HeightProperty, open);
            _thumbnailHost.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(ThumbnailOpenMs - 40))
                {
                    BeginTime = TimeSpan.FromMilliseconds(50),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
        }

        private void SettleThumbnailOpen()
        {
            _thumbnailRow.BeginAnimation(HeightProperty, null);
            _thumbnailHost.BeginAnimation(OpacityProperty, null);
            _thumbnailRow.Height = ThumbnailRowHeight;
            _thumbnailHost.Opacity = 1;
        }

        /// <summary>Capture → capture: the new one fades in over the old one, which fades out.</summary>
        private void SwapThumbnail(BitmapSource bitmap, bool animate)
        {
            var generation = ++_thumbnailSwapGeneration;
            _thumbnailBack.BeginAnimation(OpacityProperty, null);
            _thumbnailBack.Source = _thumbnailImage.Source;
            _thumbnailBack.Opacity = 1;
            _thumbnailImage.BeginAnimation(OpacityProperty, null);
            _thumbnailImage.Source = bitmap;

            if (!animate)
            {
                SettleThumbnailSwap();
                return;
            }

            _thumbnailImage.Opacity = 0;
            var duration = TimeSpan.FromMilliseconds(ThumbnailFadeMs);
            var incoming = new DoubleAnimation(0, 1, duration)
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            incoming.Completed += (_, __) =>
            {
                if (generation == _thumbnailSwapGeneration)
                    SettleThumbnailSwap();
            };
            _thumbnailImage.BeginAnimation(OpacityProperty, incoming);
            _thumbnailBack.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, 0, duration)
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                });
        }

        private void SettleThumbnailSwap()
        {
            _thumbnailImage.BeginAnimation(OpacityProperty, null);
            _thumbnailBack.BeginAnimation(OpacityProperty, null);
            _thumbnailImage.Opacity = 1;
            _thumbnailBack.Opacity = 1;
            _thumbnailBack.Source = null;
        }

        /// <summary>Capture → none: the frame fades out, then the card closes up around the gap.</summary>
        private void HideThumbnail(bool animate)
        {
            if (!_thumbnailShown)
                return;
            _thumbnailShown = false;
            var generation = ++_thumbnailStateGeneration;

            if (!animate)
            {
                CollapseThumbnail();
                return;
            }

            _thumbnailHost.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(ThumbnailCloseMs - 90))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                });
            var close = new DoubleAnimation(0, TimeSpan.FromMilliseconds(ThumbnailCloseMs))
            {
                BeginTime = TimeSpan.FromMilliseconds(70),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            close.Completed += (_, __) =>
            {
                if (generation == _thumbnailStateGeneration)
                    CollapseThumbnail();
            };
            _thumbnailRow.BeginAnimation(HeightProperty, close);
        }

        private void CollapseThumbnail()
        {
            _thumbnailSwapGeneration++;
            _thumbnailRow.BeginAnimation(HeightProperty, null);
            _thumbnailHost.BeginAnimation(OpacityProperty, null);
            _thumbnailImage.BeginAnimation(OpacityProperty, null);
            _thumbnailBack.BeginAnimation(OpacityProperty, null);
            _thumbnailRow.Height = 0;
            _thumbnailRow.Visibility = Visibility.Collapsed;
            _thumbnailHost.Opacity = 1;
            _thumbnailImage.Opacity = 1;
            _thumbnailBack.Opacity = 1;
            _thumbnailImage.Source = null;
            _thumbnailBack.Source = null;
        }

        /// <summary>Click on the thumbnail opens the capture in the default viewer,
        /// then dismisses the card — the same click-through the per-toast card had.</summary>
        private void OpenThumbnail()
        {
            var path = _thumbnailPath;
            if (_isClosing || !ToastContentBuilder.IsSafeImagePath(path))
                return;

            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch
            {
                // Best-effort — the card still dismisses.
            }
            _activityDismissed?.Invoke(_cardId);
        }

        private static ActivityStatusText ResolveStatusText(ActivitySnapshot snapshot)
        {
            if (snapshot?.StatusTextProvider != null)
            {
                try
                {
                    var localized = snapshot.StatusTextProvider();
                    if (localized != null)
                        return new ActivityStatusText(localized.Title ?? string.Empty, localized.Body ?? string.Empty);
                }
                catch
                {
                    // A status resolver is a localization convenience. Keep the
                    // already-rendered copy if a reload fails or is interrupted.
                }
            }

            return new ActivityStatusText(snapshot?.Title ?? string.Empty, snapshot?.Body ?? string.Empty);
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
            CancelHeaderDrag();
            _dragHeader.PreviewMouseLeftButtonDown -= OnHeaderMouseDown;
            PreviewMouseMove -= OnDragMouseMove;
            PreviewMouseLeftButtonUp -= OnDragMouseUp;
            LostMouseCapture -= OnDragCaptureLost;
            PrepareExpansion = null;
            ConstrainDrag = null;
            DragCompleted = null;
            BeginAnimation(TopProperty, null);
            BeginAnimation(LeftProperty, null);
            IsPositionAnimating = false;
            ++_positionGeneration;

            if (_closeHost != null)
            {
                _closeHost.MouseLeftButtonUp -= _closeHostMouseUpHandler;
            }
            _detailsRow.MouseLeftButtonUp -= _detailsUpHandler;
            HideDetails(immediate: true);
            _detailsPanel.SetEntries(Array.AsReadOnly(new ToastActivityEntry[0]));
            _lastSnapshot = null;
            _thumbnailHost.MouseLeftButtonUp -= _thumbnailUpHandler;
            _thumbnailStateGeneration++;
            _thumbnailSwapGeneration++;
            _thumbnailRow.BeginAnimation(HeightProperty, null);
            _thumbnailHost.BeginAnimation(OpacityProperty, null);
            _thumbnailImage.BeginAnimation(OpacityProperty, null);
            _thumbnailBack.BeginAnimation(OpacityProperty, null);
            _thumbnailImage.Source = null;
            _thumbnailBack.Source = null;

            MouseEnter -= _mouseEnterHandler;
            MouseMove -= _mouseMoveHandler;
            MouseLeave -= _mouseLeaveHandler;
            CancelBrandRevealTimer();
            _brandHideGeneration++;
            _brandCell.BeginAnimation(UIElement.OpacityProperty, null);
            MouseLeftButtonUp -= _mouseUpHandler;
            SourceInitialized -= _sourceInitializedHandler;
            Closed -= _closedHandler;

            // Stop any pending visual clocks before releasing callbacks. This prevents a
            // late animation completion from retaining a closed window or re-entering the
            // manager after a force-close.
            BeginAnimation(OpacityProperty, null);
            _slideTransform.BeginAnimation(TranslateTransform.XProperty, null);
            _scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _brandSweep.BeginAnimation(TranslateTransform.XProperty, null);
            _shineSweep.BeginAnimation(TranslateTransform.XProperty, null);
            _successCount.StopAnimation();
            _failedCount.StopAnimation();
            _captureCount.StopAnimation();
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
