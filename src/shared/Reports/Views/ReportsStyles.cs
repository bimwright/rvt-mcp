using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Reports.Presentation;
using RvtMcp.Plugin.Views;

namespace RvtMcp.Plugin.Reports.Views
{
    // The plugin shells also compile RvtMcp.Plugin.Reports.Binding; name the WPF type explicitly.
    using Binding = System.Windows.Data.Binding;

    /// <summary>
    /// Reports-only palette and styles. Brushes are frozen; the dictionary merges
    /// <see cref="BimwrightStyles.Dictionary"/> and adds the Reports chrome on top.
    /// </summary>
    internal static class ReportsStyles
    {
        public const string CaptionButtonKey = "ReportsCaptionButton";
        public const string CloseButtonKey = "ReportsCloseButton";
        public const string ModeButtonKey = "ReportsModeButton";
        public const string FilterButtonKey = "ReportsFilterButton";
        public const string ListItemKey = "ReportsListItem";
        public const string FeedItemKey = "ReportsFeedItem";
        public const string CardHeaderKey = "ReportsCardHeader";
        public const string ChipButtonKey = "ReportsChipButton";
        public const string DangerButtonKey = "ReportsDangerButton";
        public const string TextButtonKey = "ReportsTextButton";
        public const string ExpanderKey = "ReportsExpander";

        public static readonly FontFamily Ui = new FontFamily("Segoe UI");
        public static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas");

        public static readonly SolidColorBrush Text = Brush(0xFF1E293B);
        public static readonly SolidColorBrush TextSecondary = Brush(0xFF64748B);
        public static readonly SolidColorBrush Muted = Brush(0xFF94A3B8);
        public static readonly SolidColorBrush BorderBrush = Brush(0xFFE0E0E0);
        public static readonly SolidColorBrush BorderStrongBrush = Brush(0xFFBDBDBD);
        public static readonly SolidColorBrush Surface = Brush(0xFFFFFFFF);
        public static readonly SolidColorBrush Header = Brush(0xFFF7F8FA);
        public static readonly SolidColorBrush Side = Brush(0xFFFAFBFC);
        public static readonly SolidColorBrush Feed = Brush(0xFFF3F5F8);
        public static readonly SolidColorBrush ItemDivider = Brush(0xFFEDF0F3);
        public static readonly SolidColorBrush CardOpenBorder = Brush(0xFFB9D7EF);
        public static readonly SolidColorBrush Primary = Brush(0xFF007ACC);
        public static readonly SolidColorBrush PrimaryHover = Brush(0xFF005A9E);
        public static readonly SolidColorBrush PrimarySoft = Brush(0xFFE3F0FA);
        public static readonly SolidColorBrush CodeBackground = Brush(0xFFF1F5F9);
        public static readonly SolidColorBrush CodeBorder = Brush(0xFFE2E8F0);
        public static readonly SolidColorBrush TableHeader = Brush(0xFFF8FAFC);
        public static readonly SolidColorBrush WarningText = Brush(0xFF92400E);
        public static readonly SolidColorBrush DangerText = Brush(0xFFB91C1C);
        public static readonly SolidColorBrush DangerBorder = Brush(0xFFF3A9A9);
        public static readonly SolidColorBrush DangerBackground = Brush(0xFFFFF5F5);

        public static readonly ToneColors AddedBadge = new ToneColors(0xFF166534, 0xFFDCFCE7, 0);
        public static readonly ToneColors ModifiedBadge = new ToneColors(0xFF00548C, 0xFFE0EFFA, 0);
        public static readonly ToneColors DeletedBadge = new ToneColors(0xFF991B1B, 0xFFFEE2E2, 0);
        public static readonly ToneColors NeutralBadge = new ToneColors(0xFF475569, 0xFFE2E8F0, 0);

        private static readonly ToneColors Completed = new ToneColors(0xFF166534, 0xFFDCFCE7, 0xFF16A34A);
        private static readonly ToneColors Failed = new ToneColors(0xFFB91C1C, 0xFFFEE2E2, 0xFFDC2626);
        private static readonly ToneColors Cancelled = new ToneColors(0xFF475569, 0xFFE2E8F0, 0xFF94A3B8);

        private static readonly ToneColors NeutralStatus = new ToneColors(0xFF64748B, 0xFFF7F8FA, 0xFF94A3B8);
        private static readonly ToneColors SuccessStatus = new ToneColors(0xFF00548C, 0xFFF7F8FA, 0xFF007ACC);
        private static readonly ToneColors WarningStatus = new ToneColors(0xFF92400E, 0xFFFFFBEB, 0xFFD97706);
        private static readonly ToneColors ErrorStatus = new ToneColors(0xFFB91C1C, 0xFFFEF2F2, 0xFFDC2626);

        private static readonly SolidColorBrush LiveLink = Primary;
        private static readonly SolidColorBrush DimLink = Muted;
        private static readonly TextDecorationCollection LiveUnderline = Underline(Brush(0x59007ACC), false);
        private static readonly TextDecorationCollection DimUnderline = Underline(Brush(0xFFCBD5E1), true);

        private static ResourceDictionary _dictionary;
        private static Style _linkStyle, _countBadgeStyle, _tagStyle;

        public static ResourceDictionary Dictionary => _dictionary ??= Parse();

        /// <summary>Unknown outcomes use the cancelled look.</summary>
        public static ToneColors Outcome(string outcome) =>
            outcome == "completed" ? Completed : outcome == "failed" ? Failed : Cancelled;

        public static ToneColors Status(ReportStatusTone tone) =>
            tone == ReportStatusTone.Success ? SuccessStatus : tone == ReportStatusTone.Warning ? WarningStatus
            : tone == ReportStatusTone.Error ? ErrorStatus : NeutralStatus;

        /// <summary>
        /// Element link: the DataContext exposes <c>IsLive</c>; Tag carries the dimmed tooltip.
        /// The link stays enabled when dimmed so the tooltip still shows.
        /// </summary>
        public static Style LinkStyle => _linkStyle ??= CreateLinkStyle();

        /// <summary>Count badge and view/sheet tag next to a link; both dim with it.</summary>
        public static Style CountBadgeStyle => _countBadgeStyle ??= CreateBadgeStyle(false);
        public static Style TagStyle => _tagStyle ??= CreateBadgeStyle(true);

        public static Ellipse Dot(SolidColorBrush fill, double size) =>
            new Ellipse { Width = size, Height = size, Fill = fill, VerticalAlignment = VerticalAlignment.Center };

        /// <summary>Outcome pill: dot, label, rounded tinted background.</summary>
        public static Border Pill(string outcome, string label)
        {
            var colors = Outcome(outcome);
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = colors.Dot,
                Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(new TextBlock { Text = label, FontSize = 11.5, FontWeight = FontWeights.SemiBold,
                Foreground = colors.Text, VerticalAlignment = VerticalAlignment.Center });
            var pill = new Border { Background = colors.Background, CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 1, 8, 1), Child = panel, VerticalAlignment = VerticalAlignment.Center, Tag = "OutcomePill" };
            AutomationProperties.SetName(pill, label);
            return pill;
        }

        /// <summary>Small rounded count badge for the changes header and chip.</summary>
        public static Border Badge(string text, ToneColors colors) => new Border
        {
            Background = colors.Background, CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, Foreground = colors.Text, FontSize = 11.5, FontWeight = FontWeights.Medium, LineHeight = 17 }
        };

        /// <summary>"i" chip that marks the report as self-reported by the agent.</summary>
        public static Border SelfReportedChip(string text)
        {
            var mark = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), BorderBrush = Muted,
                BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "i", FontSize = 8.5, FontWeight = FontWeights.Bold, FontStyle = FontStyles.Italic,
                    Foreground = TextSecondary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(mark);
            panel.Children.Add(new TextBlock { Text = text, FontSize = 11.5, Foreground = TextSecondary, VerticalAlignment = VerticalAlignment.Center });
            return new Border { BorderBrush = BorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6, 1, 8, 1), Child = panel, VerticalAlignment = VerticalAlignment.Center, Tag = "SelfReported" };
        }

        /// <summary>Copy as History does it: an icon button whose tooltip and accessible name say what it copies.</summary>
        public static Button CopyButton(ICommand command)
        {
            var label = L.T("reports.copy.button");
            var button = new Button { Style = (Style)Dictionary[BimwrightStyles.IconButtonKey], Content = "", Command = command,
                ToolTip = label, Margin = new Thickness(3, 2, 0, 2), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(button, label);
            return button;
        }

        /// <summary>Delete next to Copy: an "X" icon button; <see cref="SyncDeleteButton"/> sets its two-step state.</summary>
        public static Button DeleteButton(ICommand command) => new Button
        {
            Command = command, ToolTip = L.T("reports.delete.button"), Margin = new Thickness(3, 2, 0, 2), VerticalAlignment = VerticalAlignment.Center
        };

        /// <summary>The "X" icon, or while the delete waits for its second click, the red confirm text in a button as wide as the text.</summary>
        public static void SyncDeleteButton(Button button, bool pending, string label)
        {
            button.Style = (Style)Dictionary[pending ? DangerButtonKey : BimwrightStyles.IconButtonKey];
            button.Content = pending ? label : "";
            AutomationProperties.SetName(button, label);
        }

        public static SolidColorBrush Brush(uint argb)
        {
            var brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            brush.Freeze();
            return brush;
        }

        private static TextDecorationCollection Underline(SolidColorBrush brush, bool dotted)
        {
            var pen = new Pen(brush, 1);
            if (dotted) pen.DashStyle = new DashStyle(new double[] { 1, 2 }, 0);
            pen.Freeze();
            var decorations = new TextDecorationCollection { new TextDecoration(TextDecorationLocation.Underline, pen, 1,
                TextDecorationUnit.FontRecommended, TextDecorationUnit.Pixel) };
            decorations.Freeze();
            return decorations;
        }

        private static Style CreateLinkStyle()
        {
            var style = new Style(typeof(Hyperlink));
            style.Setters.Add(new Setter(TextElement.ForegroundProperty, DimLink));
            style.Setters.Add(new Setter(Inline.TextDecorationsProperty, DimUnderline));
            style.Setters.Add(new Setter(FrameworkContentElement.CursorProperty, Cursors.Arrow));
            style.Setters.Add(new Setter(FrameworkContentElement.ToolTipProperty, new Binding(nameof(FrameworkContentElement.Tag)) { RelativeSource = RelativeSource.Self }));
            style.Setters.Add(new Setter(AutomationProperties.HelpTextProperty, new Binding(nameof(FrameworkContentElement.Tag)) { RelativeSource = RelativeSource.Self }));
            var live = new DataTrigger { Binding = new Binding(nameof(ReportLinkViewModel.IsLive)), Value = true };
            live.Setters.Add(new Setter(TextElement.ForegroundProperty, LiveLink));
            live.Setters.Add(new Setter(Inline.TextDecorationsProperty, LiveUnderline));
            live.Setters.Add(new Setter(FrameworkContentElement.CursorProperty, Cursors.Hand));
            live.Setters.Add(new Setter(FrameworkContentElement.ToolTipProperty, null));
            live.Setters.Add(new Setter(AutomationProperties.HelpTextProperty, ""));
            style.Triggers.Add(live);
            var hover = new MultiDataTrigger();
            hover.Conditions.Add(new System.Windows.Condition(new Binding(nameof(ReportLinkViewModel.IsLive)), true));
            hover.Conditions.Add(new System.Windows.Condition(new Binding(nameof(ContentElement.IsMouseOver)) { RelativeSource = RelativeSource.Self }, true));
            hover.Setters.Add(new Setter(TextElement.BackgroundProperty, PrimarySoft));
            style.Triggers.Add(hover);
            style.Seal();
            return style;
        }

        private static Style CreateBadgeStyle(bool tag)
        {
            var style = new Style(typeof(Border));
            style.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(8)));
            style.Setters.Add(new Setter(Border.PaddingProperty, new Thickness(5, 0, 5, 0)));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 0, 0, 0)));
            style.Setters.Add(new Setter(TextElement.FontSizeProperty, 10.0));
            style.Setters.Add(new Setter(TextElement.FontWeightProperty, tag ? FontWeights.Medium : FontWeights.SemiBold));
            style.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(tag ? 1 : 0)));
            style.Setters.Add(new Setter(Border.BorderBrushProperty, Brush(0xFFE2E8F0)));
            style.Setters.Add(new Setter(Border.BackgroundProperty, tag ? Brushes.Transparent : Brush(0xFFEEF1F4)));
            style.Setters.Add(new Setter(TextElement.ForegroundProperty, Muted));
            var live = new DataTrigger { Binding = new Binding(nameof(ReportLinkViewModel.IsLive)), Value = true };
            live.Setters.Add(new Setter(TextElement.ForegroundProperty, PrimaryHover));
            if (tag) live.Setters.Add(new Setter(Border.BorderBrushProperty, CardOpenBorder));
            else live.Setters.Add(new Setter(Border.BackgroundProperty, Brush(0xFFE0EFFA)));
            style.Triggers.Add(live);
            style.Seal();
            return style;
        }

        private static ResourceDictionary Parse()
        {
            const string xaml = @"<ResourceDictionary
    xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">

    <!-- Caption buttons: flat, hit-testable inside WindowChrome -->
    <Style x:Key=""ReportsCaptionButton"" TargetType=""{x:Type Button}"">
        <Setter Property=""Width"" Value=""38""/>
        <Setter Property=""Height"" Value=""31""/>
        <Setter Property=""Background"" Value=""Transparent""/>
        <Setter Property=""Foreground"" Value=""#FF475569""/>
        <Setter Property=""Template"">
            <Setter.Value>
                <ControlTemplate TargetType=""{x:Type Button}"">
                    <Border x:Name=""Chrome"" Background=""{TemplateBinding Background}"">
                        <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property=""IsMouseOver"" Value=""True"">
                            <Setter TargetName=""Chrome"" Property=""Background"" Value=""#FFE2E8F0""/>
                        </Trigger>
                        <Trigger Property=""IsPressed"" Value=""True"">
                            <Setter TargetName=""Chrome"" Property=""Background"" Value=""#FFCBD5E1""/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
    <Style x:Key=""ReportsCloseButton"" TargetType=""{x:Type Button}"" BasedOn=""{StaticResource ReportsCaptionButton}"">
        <Setter Property=""Template"">
            <Setter.Value>
                <ControlTemplate TargetType=""{x:Type Button}"">
                    <Border x:Name=""Chrome"" Background=""{TemplateBinding Background}"">
                        <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property=""IsMouseOver"" Value=""True"">
                            <Setter TargetName=""Chrome"" Property=""Background"" Value=""#FFE81123""/>
                            <Setter Property=""Foreground"" Value=""#FFFFFFFF""/>
                        </Trigger>
                        <Trigger Property=""IsPressed"" Value=""True"">
                            <Setter TargetName=""Chrome"" Property=""Background"" Value=""#FFF1707A""/>
                            <Setter Property=""Foreground"" Value=""#FFFFFFFF""/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- Display-mode pair in the title bar -->
    <Style x:Key=""ReportsModeButton"" TargetType=""{x:Type ToggleButton}"">
        <Setter Property=""Width"" Value=""30""/>
        <Setter Property=""Height"" Value=""22""/>
        <Setter Property=""Background"" Value=""Transparent""/>
        <Setter Property=""Foreground"" Value=""#FF64748B""/>
        <Setter Property=""Cursor"" Value=""Hand""/>
        <Setter Property=""Template"">
            <Setter.Value>
                <ControlTemplate TargetType=""{x:Type ToggleButton}"">
                    <Border x:Name=""Chrome"" Background=""{TemplateBinding Background}"">
                        <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property=""IsMouseOver"" Value=""True"">
                            <Setter TargetName=""Chrome"" Property=""Background"" Value=""#FFF0F4F8""/>
                        </Trigger>
                        <Trigger Property=""IsChecked"" Value=""True"">
                            <Setter TargetName=""Chrome"" Property=""Background"" Value=""#FFE3F0FA""/>
                            <Setter Property=""Foreground"" Value=""#FF005A9E""/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- Segmented filter in wide mode -->
    <Style x:Key=""ReportsFilterButton"" TargetType=""{x:Type ToggleButton}"">
        <Setter Property=""Padding"" Value=""6,4""/>
        <Setter Property=""FontSize"" Value=""12""/>
        <Setter Property=""Background"" Value=""Transparent""/>
        <Setter Property=""Foreground"" Value=""#FF1E293B""/>
        <Setter Property=""Cursor"" Value=""Hand""/>
        <Setter Property=""Template"">
            <Setter.Value>
                <ControlTemplate TargetType=""{x:Type ToggleButton}"">
                    <Border x:Name=""Chrome"" Background=""{TemplateBinding Background}"" Padding=""{TemplateBinding Padding}"">
                        <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property=""IsMouseOver"" Value=""True"">
                            <Setter TargetName=""Chrome"" Property=""Background"" Value=""#FFF0F4F8""/>
                        </Trigger>
                        <Trigger Property=""IsChecked"" Value=""True"">
                            <Setter TargetName=""Chrome"" Property=""Background"" Value=""#FF007ACC""/>
                            <Setter Property=""Foreground"" Value=""#FFFFFFFF""/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- Wide list row: selection colour stays when the list loses focus -->
    <Style x:Key=""ReportsListItem"" TargetType=""{x:Type ListBoxItem}"">
        <Setter Property=""HorizontalContentAlignment"" Value=""Stretch""/>
        <Setter Property=""AutomationProperties.Name"" Value=""{Binding Summary}""/>
        <Setter Property=""Background"" Value=""Transparent""/>
        <Setter Property=""Cursor"" Value=""Hand""/>
        <Setter Property=""Template"">
            <Setter.Value>
                <ControlTemplate TargetType=""{x:Type ListBoxItem}"">
                    <!-- Each row is a light card so neighbouring reports read apart. Hover eases the border towards blue like
                         a Bimwright button, but lighter and without the press scale; the selected row keeps a stronger ring. -->
                    <Grid Margin=""8,6,8,0"">
                        <Border x:Name=""Chrome"" Background=""#FFFFFFFF"" BorderThickness=""1"" CornerRadius=""5"" Padding=""10,8"">
                            <Border.BorderBrush>
                                <SolidColorBrush x:Name=""RowBorder"" Color=""#FFE0E0E0""/>
                            </Border.BorderBrush>
                            <ContentPresenter/>
                        </Border>
                        <!-- Drawn over the animated border, so a hover that ends on the selected row cannot grey its ring -->
                        <Border x:Name=""SelectedRing"" BorderBrush=""#FF7FB6E0"" BorderThickness=""1"" CornerRadius=""5""
                                IsHitTestVisible=""False"" Visibility=""Collapsed""/>
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property=""IsMouseOver"" Value=""True"">
                            <Trigger.EnterActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <ColorAnimation Storyboard.TargetName=""RowBorder"" Storyboard.TargetProperty=""Color""
                                                        To=""#FFA8CFEE"" Duration=""0:0:0.2""/>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.EnterActions>
                            <Trigger.ExitActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <ColorAnimation Storyboard.TargetName=""RowBorder"" Storyboard.TargetProperty=""Color""
                                                        To=""#FFE0E0E0"" Duration=""0:0:0.25""/>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.ExitActions>
                            <Setter TargetName=""Chrome"" Property=""Background"" Value=""#FFF7FAFD""/>
                        </Trigger>
                        <Trigger Property=""IsSelected"" Value=""True"">
                            <Setter TargetName=""Chrome"" Property=""Background"" Value=""#FFE6F1FB""/>
                            <Setter TargetName=""SelectedRing"" Property=""Visibility"" Value=""Visible""/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- Every tooltip in the window: a Bimwright surface card (white, light border, 6 px corners, soft shadow); long text wraps -->
    <Style TargetType=""{x:Type ToolTip}"">
        <Setter Property=""OverridesDefaultStyle"" Value=""True""/>
        <Setter Property=""FontFamily"" Value=""Segoe UI""/>
        <Setter Property=""FontSize"" Value=""12""/>
        <Setter Property=""Foreground"" Value=""#FF1E293B""/>
        <Setter Property=""Template"">
            <Setter.Value>
                <ControlTemplate TargetType=""{x:Type ToolTip}"">
                    <Border Margin=""2,2,8,8"" MaxWidth=""360"" Background=""#FFFFFFFF"" BorderBrush=""#FFE0E0E0"" BorderThickness=""1""
                            CornerRadius=""6"" Padding=""10,7"" SnapsToDevicePixels=""True"">
                        <Border.Effect>
                            <DropShadowEffect Color=""#20000000"" Direction=""270"" ShadowDepth=""2"" BlurRadius=""10"" Opacity=""0.35""/>
                        </Border.Effect>
                        <ContentPresenter>
                            <ContentPresenter.Resources>
                                <Style TargetType=""{x:Type TextBlock}"">
                                    <Setter Property=""TextWrapping"" Value=""Wrap""/>
                                </Style>
                            </ContentPresenter.Resources>
                        </ContentPresenter>
                    </Border>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- Compact feed: the card owns focus and clicks, the container stays inert -->
    <Style x:Key=""ReportsFeedItem"" TargetType=""{x:Type ListBoxItem}"">
        <Setter Property=""HorizontalContentAlignment"" Value=""Stretch""/>
        <Setter Property=""Focusable"" Value=""False""/>
        <Setter Property=""Margin"" Value=""0,0,0,6""/>
        <Setter Property=""Template"">
            <Setter.Value>
                <ControlTemplate TargetType=""{x:Type ListBoxItem}"">
                    <ContentPresenter/>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style x:Key=""ReportsCardHeader"" TargetType=""{x:Type Button}"">
        <Setter Property=""HorizontalContentAlignment"" Value=""Stretch""/>
        <Setter Property=""Background"" Value=""Transparent""/>
        <Setter Property=""Foreground"" Value=""#FF1E293B""/>
        <Setter Property=""Cursor"" Value=""Hand""/>
        <Setter Property=""Template"">
            <Setter.Value>
                <ControlTemplate TargetType=""{x:Type Button}"">
                    <Border x:Name=""Chrome"" Background=""{TemplateBinding Background}"" CornerRadius=""5"" Padding=""10,8"">
                        <ContentPresenter HorizontalAlignment=""Stretch""/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property=""IsMouseOver"" Value=""True"">
                            <Setter TargetName=""Chrome"" Property=""Background"" Value=""#FFF0F4F8""/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style x:Key=""ReportsChipButton"" TargetType=""{x:Type Button}"">
        <Setter Property=""FontSize"" Value=""11.5""/>
        <Setter Property=""Foreground"" Value=""#FF1E293B""/>
        <Setter Property=""Background"" Value=""#FFFAFBFC""/>
        <Setter Property=""BorderBrush"" Value=""#FFE0E0E0""/>
        <Setter Property=""Cursor"" Value=""Hand""/>
        <Setter Property=""HorizontalAlignment"" Value=""Left""/>
        <Setter Property=""Template"">
            <Setter.Value>
                <ControlTemplate TargetType=""{x:Type Button}"">
                    <Border x:Name=""Chrome"" Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}""
                            BorderThickness=""1"" CornerRadius=""12"" Padding=""10,2"">
                        <ContentPresenter VerticalAlignment=""Center""/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property=""IsMouseOver"" Value=""True"">
                            <Setter TargetName=""Chrome"" Property=""BorderBrush"" Value=""#FF007ACC""/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- Delete waiting for its second click; the style (BasedOn BimToolbarButton) is built in code -->
    <ControlTemplate x:Key=""ReportsDangerTemplate"" TargetType=""{x:Type Button}"">
        <Border x:Name=""Chrome"" Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}""
                BorderThickness=""1"" CornerRadius=""6"">
            <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center"" Margin=""{TemplateBinding Padding}""/>
        </Border>
        <ControlTemplate.Triggers>
            <Trigger Property=""IsMouseOver"" Value=""True"">
                <Setter TargetName=""Chrome"" Property=""BorderBrush"" Value=""#FFDC2626""/>
            </Trigger>
        </ControlTemplate.Triggers>
    </ControlTemplate>

    <!-- Link-looking button (Show N more) -->
    <Style x:Key=""ReportsTextButton"" TargetType=""{x:Type Button}"">
        <Setter Property=""Foreground"" Value=""#FF007ACC""/>
        <Setter Property=""FontSize"" Value=""11.5""/>
        <Setter Property=""Cursor"" Value=""Hand""/>
        <Setter Property=""HorizontalAlignment"" Value=""Left""/>
        <Setter Property=""Template"">
            <Setter.Value>
                <ControlTemplate TargetType=""{x:Type Button}"">
                    <Border Background=""Transparent"" Padding=""0,2"">
                        <ContentPresenter x:Name=""Text""/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property=""IsMouseOver"" Value=""True"">
                            <Setter Property=""Foreground"" Value=""#FF005A9E""/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- Changes section in wide mode: triangle header, open by default -->
    <Style x:Key=""ReportsExpander"" TargetType=""{x:Type Expander}"">
        <Setter Property=""Template"">
            <Setter.Value>
                <ControlTemplate TargetType=""{x:Type Expander}"">
                    <DockPanel>
                        <ToggleButton x:Name=""HeaderToggle"" DockPanel.Dock=""Top"" Cursor=""Hand""
                                      IsChecked=""{Binding IsExpanded, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}""
                                      Content=""{TemplateBinding Header}""
                                      AutomationProperties.Name=""{Binding Tag, RelativeSource={RelativeSource TemplatedParent}}"">
                            <ToggleButton.Template>
                                <ControlTemplate TargetType=""{x:Type ToggleButton}"">
                                    <Border Background=""Transparent"" Padding=""10,7"">
                                        <DockPanel>
                                            <Path x:Name=""Arrow"" DockPanel.Dock=""Left"" Data=""M0,0 L5,4 L0,8 Z"" Fill=""#FF64748B""
                                                  Margin=""0,0,8,0"" VerticalAlignment=""Center"" RenderTransformOrigin=""0.5,0.5"">
                                                <Path.RenderTransform><RotateTransform Angle=""0""/></Path.RenderTransform>
                                            </Path>
                                            <ContentPresenter VerticalAlignment=""Center""/>
                                        </DockPanel>
                                    </Border>
                                    <ControlTemplate.Triggers>
                                        <Trigger Property=""IsChecked"" Value=""True"">
                                            <Setter TargetName=""Arrow"" Property=""RenderTransform"">
                                                <Setter.Value><RotateTransform Angle=""90""/></Setter.Value>
                                            </Setter>
                                        </Trigger>
                                    </ControlTemplate.Triggers>
                                </ControlTemplate>
                            </ToggleButton.Template>
                        </ToggleButton>
                        <ContentPresenter x:Name=""Body"" Visibility=""Collapsed""/>
                    </DockPanel>
                    <ControlTemplate.Triggers>
                        <Trigger Property=""IsExpanded"" Value=""True"">
                            <Setter TargetName=""Body"" Property=""Visibility"" Value=""Visible""/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
</ResourceDictionary>";
            var own = (ResourceDictionary)XamlReader.Parse(xaml);
            var danger = new Style(typeof(Button), (Style)BimwrightStyles.Dictionary[BimwrightStyles.ToolbarButtonKey]);
            danger.Setters.Add(new Setter(Control.ForegroundProperty, DangerText));
            danger.Setters.Add(new Setter(Control.BackgroundProperty, DangerBackground));
            danger.Setters.Add(new Setter(Control.BorderBrushProperty, DangerBorder));
            danger.Setters.Add(new Setter(Control.TemplateProperty, own["ReportsDangerTemplate"]));
            own.Add(DangerButtonKey, danger);
            var dictionary = new ResourceDictionary();
            dictionary.MergedDictionaries.Add(BimwrightStyles.Dictionary);
            dictionary.MergedDictionaries.Add(own);
            return dictionary;
        }

        internal sealed class ToneColors
        {
            public ToneColors(uint text, uint background, uint dot)
            {
                Text = Brush(text); Background = Brush(background); Dot = Brush(dot == 0 ? text : dot);
            }
            public SolidColorBrush Text { get; }
            public SolidColorBrush Background { get; }
            public SolidColorBrush Dot { get; }
        }
    }

    /// <summary>Outcome string → dot brush for list rows and cards.</summary>
    internal sealed class OutcomeDotConverter : IValueConverter
    {
        public static readonly OutcomeDotConverter Instance = new OutcomeDotConverter();
        public object Convert(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            ReportsStyles.Outcome(value as string).Dot;
        public object ConvertBack(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            Binding.DoNothing;
    }
}
