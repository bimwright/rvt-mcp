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
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RvtMcp.Plugin.Views.Toast
{
    internal sealed class McpToastWindow : Window
    {
        private const double CardWidth = 340;
        private const double BrandRestOpacity = 0.4;
        private const int GwlExStyle = -20;
        private const long WsExNoActivate = 0x08000000L;
        private const long WsExToolWindow = 0x00000080L;

        private readonly Action<McpToastWindow> _onClosed;
        private readonly TextBlock _iconText;
        private readonly TextBlock _titleText;
        private readonly TextBlock _categoryText;
        private readonly TextBlock _summaryText;
        private readonly TextBlock _detailText;
        private readonly TextBlock _durationText;
        private readonly TextBlock _brandText;
        private readonly Border _thumbnailHost;
        private readonly Image _thumbnailImage;
        private readonly Border _root;
        private readonly TranslateTransform _slideTransform;
        private readonly ScaleTransform _scaleTransform;
        private readonly DispatcherTimer _autoDismissTimer;
        private bool _isMouseOver;
        private bool _isClosing;

        public McpToastViewModel ViewModel { get; private set; }

        public McpToastWindow(McpToastViewModel viewModel, Action<McpToastWindow> onClosed)
        {
            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _onClosed = onClosed;

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
                BorderBrush = McpToastTheme.BuildAccentBrush(viewModel),
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
            for (var i = 0; i < 6; i++)
                content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new DockPanel { LastChildFill = true };

            _iconText = new TextBlock
            {
                Text = McpToastTheme.GetIconGlyph(viewModel),
                FontFamily = McpToastTheme.IconFont,
                FontSize = 16,
                Foreground = McpToastTheme.BuildIconBrush(viewModel),
                Margin = new Thickness(0, 1, 8, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            DockPanel.SetDock(_iconText, Dock.Left);
            header.Children.Add(_iconText);

            var closeHost = new Border
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
            closeHost.MouseEnter += (_, __) => closeHost.Background = McpToastTheme.CloseHover;
            closeHost.MouseLeave += (_, __) => closeHost.Background = Brushes.Transparent;
            closeHost.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                BeginClose();
            };
            DockPanel.SetDock(closeHost, Dock.Right);
            header.Children.Add(closeHost);

            _titleText = new TextBlock
            {
                Text = viewModel.Title ?? string.Empty,
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
                Text = viewModel.CategoryLabel ?? string.Empty,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = McpToastTheme.Primary,
                Margin = new Thickness(24, 2, 0, 0)
            };
            Grid.SetRow(_categoryText, 1);
            content.Children.Add(_categoryText);

            _summaryText = new TextBlock
            {
                Text = viewModel.Summary ?? string.Empty,
                FontSize = 12.5,
                FontWeight = FontWeights.Medium,
                Foreground = McpToastTheme.Text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(24, 4, 0, 0),
                MaxHeight = 64,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetRow(_summaryText, 2);
            content.Children.Add(_summaryText);

            _detailText = new TextBlock
            {
                FontSize = 11.5,
                Foreground = McpToastTheme.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(24, 2, 0, 0),
                MaxHeight = 40,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetRow(_detailText, 3);
            content.Children.Add(_detailText);

            _thumbnailImage = new Image
            {
                Stretch = Stretch.Uniform,
                MaxWidth = 300,
                MaxHeight = 120,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _thumbnailHost = new Border
            {
                Margin = new Thickness(24, 8, 0, 0),
                CornerRadius = new CornerRadius(4),
                BorderBrush = McpToastTheme.MutedAccent,
                BorderThickness = new Thickness(1),
                Background = Brushes.White,
                Child = _thumbnailImage,
                Visibility = Visibility.Collapsed
            };
            Grid.SetRow(_thumbnailHost, 4);
            content.Children.Add(_thumbnailHost);

            var footer = new DockPanel { Margin = new Thickness(24, 6, 0, 0) };

            _brandText = new TextBlock
            {
                // Logo casing and colours; dimmed at rest, fades to full on hover.
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Opacity = BrandRestOpacity,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "bimwright rvt-mcp",
                Inlines =
                {
                    new Run("BIM") { Foreground = McpToastTheme.BrandBim },
                    new Run("wright") { Foreground = McpToastTheme.BrandWright }
                }
            };
            DockPanel.SetDock(_brandText, Dock.Right);
            footer.Children.Add(_brandText);

            _durationText = new TextBlock
            {
                Text = FormatDuration(viewModel),
                FontSize = 11,
                Foreground = McpToastTheme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center
            };
            footer.Children.Add(_durationText);
            Grid.SetRow(footer, 5);
            content.Children.Add(footer);

            ApplyViewModelText(viewModel);
            ApplyThumbnail(viewModel.ThumbnailPath);

            _root.Child = content;
            _root.Cursor = Cursors.Hand;
            Content = _root;

            MouseEnter += (_, __) =>
            {
                _isMouseOver = true;
                _autoDismissTimer.Stop();
                FadeBrand(1);
            };
            MouseLeave += (_, __) =>
            {
                _isMouseOver = false;
                if (!_isClosing)
                    _autoDismissTimer.Start();
                FadeBrand(BrandRestOpacity);
            };
            MouseLeftButtonUp += (_, e) =>
            {
                if (e.OriginalSource is Border border && border == closeHost)
                    return;
                HandleToastClick();
            };

            _autoDismissTimer = new DispatcherTimer();
            _autoDismissTimer.Tick += (_, __) =>
            {
                _autoDismissTimer.Stop();
                BeginClose();
            };

            Loaded += (_, __) =>
            {
                StartAutoDismiss();
            };

            // Without WS_EX_NOACTIVATE each shown toast can steal keyboard focus
            // from Revit mid-typing. Clicks are still delivered; only activation is blocked.
            SourceInitialized += (_, __) => MakeNoActivate();
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
        }

        public void AnimateToPosition(double top, double left)
        {
            var fromTop = Top;
            var fromLeft = Left;
            SetPosition(top, left);
            // An unshown WPF Window defaults to NaN. It has no position to animate from.
            if (double.IsNaN(fromTop) || double.IsNaN(fromLeft))
                return;

            var duration = TimeSpan.FromMilliseconds(200);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(TopProperty, new DoubleAnimation(fromTop, top, duration) { EasingFunction = ease });
            BeginAnimation(LeftProperty, new DoubleAnimation(fromLeft, left, duration) { EasingFunction = ease });
        }

        public void SetPosition(double top, double left)
        {
            // A previous animation's held value must not override a direct reflow.
            BeginAnimation(TopProperty, null);
            BeginAnimation(LeftProperty, null);
            Top = top;
            Left = left;
        }

        public void StartAutoDismiss()
        {
            _autoDismissTimer.Interval = TimeSpan.FromSeconds(GetAutoDismissSeconds(ViewModel));
            if (!_isMouseOver)
                _autoDismissTimer.Start();
        }

        public void CloseImmediate()
        {
            if (_isClosing)
                return;
            _isClosing = true;
            _autoDismissTimer.Stop();
            Close();
            _onClosed?.Invoke(this);
        }

        private void ApplyViewModelText(McpToastViewModel vm)
        {
            _titleText.Text = vm.Title ?? string.Empty;
            _categoryText.Text = vm.CategoryLabel ?? string.Empty;
            _categoryText.Visibility = string.IsNullOrWhiteSpace(vm.CategoryLabel)
                ? Visibility.Collapsed
                : Visibility.Visible;
            _summaryText.Text = vm.Summary ?? string.Empty;
            _detailText.Text = vm.Detail ?? string.Empty;
            _detailText.Visibility = string.IsNullOrWhiteSpace(vm.Detail)
                ? Visibility.Collapsed
                : Visibility.Visible;
            _durationText.Text = FormatDuration(vm);
        }

        private void ApplyThumbnail(string path)
        {
            if (!ToastContentBuilder.IsSafeImagePath(path))
            {
                _thumbnailHost.Visibility = Visibility.Collapsed;
                _thumbnailImage.Source = null;
                return;
            }

            var bytes = ToastThumbnailLoader.TryLoadBytes(path, 8 * 1024 * 1024);
            if (bytes == null)
            {
                _thumbnailHost.Visibility = Visibility.Collapsed;
                _thumbnailImage.Source = null;
                return;
            }

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
                    _thumbnailImage.Source = bitmap;
                }
                _thumbnailHost.Visibility = Visibility.Visible;
            }
            catch
            {
                _thumbnailHost.Visibility = Visibility.Collapsed;
                _thumbnailImage.Source = null;
            }
        }

        private void HandleToastClick()
        {
            if (_isClosing)
            {
                BeginClose();
                return;
            }

            if (ViewModel.Success
                && string.Equals(ViewModel.CommandName, "capture_view_image", StringComparison.OrdinalIgnoreCase))
            {
                TryOpenCapturedImage(ViewModel.ThumbnailPath);
            }

            BeginClose();
        }

        private static void TryOpenCapturedImage(string path)
        {
            if (!ToastContentBuilder.IsSafeImagePath(path))
                return;

            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch
            {
                // Best-effort — dismiss still proceeds.
            }
        }

        /// <summary>Brand mark: dimmed at rest, quick fade to full on hover, slower fade back on leave.</summary>
        private void FadeBrand(double to)
        {
            var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(to > 0 ? 180 : 350));
            _brandText.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        private void BeginClose()
        {
            if (_isClosing)
                return;
            _isClosing = true;
            _autoDismissTimer.Stop();

            var duration = TimeSpan.FromMilliseconds(220);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };
            var fade = new DoubleAnimation(Opacity, 0, duration) { EasingFunction = ease };
            fade.Completed += (_, __) =>
            {
                Close();
                _onClosed?.Invoke(this);
            };
            BeginAnimation(OpacityProperty, fade);
        }

        private static string FormatDuration(McpToastViewModel vm)
        {
            return vm.DurationMs > 0 ? $"{vm.DurationMs}ms" : string.Empty;
        }

        private static double GetAutoDismissSeconds(McpToastViewModel vm)
        {
            if (vm.AutoDismissSeconds.HasValue)
                return vm.AutoDismissSeconds.Value;
            if (!vm.Success)
                return 8;
            if (!string.IsNullOrEmpty(vm.ThumbnailPath))
                return 9;
            return vm.Kind == ToolActivityKind.Write ? 6 : 3;
        }

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
    }
}
