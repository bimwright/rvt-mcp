using System.Windows;
using System.Windows.Markup;

namespace RvtMcp.Plugin.Views
{
    /// <summary>
    /// Standalone button/control styles for plugin windows — rounded chrome,
    /// animated border ring on hover, press-scale feedback. Self-contained:
    /// parsed from embedded XAML, no external resource files.
    /// </summary>
    internal static class BimwrightStyles
    {
        public const string ToolbarButtonKey = "BimToolbarButton";
        public const string PrimaryButtonKey = "BimPrimaryButton";

        private static ResourceDictionary _dictionary;

        public static ResourceDictionary Dictionary => _dictionary ??= Parse();

        private static ResourceDictionary Parse()
        {
            const string xaml = @"<ResourceDictionary
    xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">

    <!-- Palette -->
    <SolidColorBrush x:Key=""BimPrimary"" Color=""#FF007ACC""/>
    <SolidColorBrush x:Key=""BimPrimaryHover"" Color=""#FF005A9E""/>
    <SolidColorBrush x:Key=""BimPrimaryPressed"" Color=""#FF004578""/>
    <SolidColorBrush x:Key=""BimSurface"" Color=""#FFFFFF""/>
    <SolidColorBrush x:Key=""BimSurfaceHover"" Color=""#F0F4F8""/>
    <SolidColorBrush x:Key=""BimBackground"" Color=""#F5F5F5""/>
    <SolidColorBrush x:Key=""BimBorder"" Color=""#FFE0E0E0""/>
    <SolidColorBrush x:Key=""BimBorderStrong"" Color=""#FFBDBDBD""/>
    <SolidColorBrush x:Key=""BimText"" Color=""#FF1E293B""/>
    <SolidColorBrush x:Key=""BimTextSecondary"" Color=""#FF64748B""/>
    <SolidColorBrush x:Key=""BimTextOnPrimary"" Color=""#FFFFFFFF""/>
    <SolidColorBrush x:Key=""BimPressed"" Color=""#FFE2E8F0""/>

    <!-- Font family (Segoe UI) for common controls -->
    <Style TargetType=""{x:Type TextBlock}"">
        <Setter Property=""FontFamily"" Value=""Segoe UI""/>
    </Style>
    <Style TargetType=""{x:Type TextBox}"">
        <Setter Property=""FontFamily"" Value=""Segoe UI""/>
    </Style>
    <Style TargetType=""{x:Type ComboBox}"">
        <Setter Property=""FontFamily"" Value=""Segoe UI""/>
    </Style>
    <Style TargetType=""{x:Type DataGrid}"">
        <Setter Property=""FontFamily"" Value=""Segoe UI""/>
    </Style>

    <!-- Default button: rounded chrome, hover border ring, press scale -->
    <Style TargetType=""{x:Type Button}"">
        <Setter Property=""Background"" Value=""{StaticResource BimSurface}""/>
        <Setter Property=""Foreground"" Value=""{StaticResource BimText}""/>
        <Setter Property=""BorderThickness"" Value=""0.8""/>
        <Setter Property=""Padding"" Value=""12,4""/>
        <Setter Property=""FontFamily"" Value=""Segoe UI""/>
        <Setter Property=""FontSize"" Value=""12""/>
        <Setter Property=""FontWeight"" Value=""Medium""/>
        <Setter Property=""Height"" Value=""28""/>
        <Setter Property=""MinWidth"" Value=""80""/>
        <Setter Property=""Cursor"" Value=""Hand""/>
        <Setter Property=""Template"">
            <Setter.Value>
                <ControlTemplate TargetType=""{x:Type Button}"">
                    <Border x:Name=""ButtonBorder""
                            Background=""{TemplateBinding Background}""
                            BorderThickness=""{TemplateBinding BorderThickness}""
                            CornerRadius=""6""
                            RenderTransformOrigin=""0.5,0.5"">
                        <Border.BorderBrush>
                            <SolidColorBrush x:Name=""AnimatedBorderBrush"" Color=""#FFE0E0E0""/>
                        </Border.BorderBrush>
                        <Border.Effect>
                            <DropShadowEffect Color=""#20000000"" Direction=""270"" ShadowDepth=""1"" BlurRadius=""6"" Opacity=""0.35""/>
                        </Border.Effect>
                        <Border.RenderTransform>
                            <ScaleTransform x:Name=""PressScale"" ScaleX=""1"" ScaleY=""1""/>
                        </Border.RenderTransform>
                        <ContentPresenter HorizontalAlignment=""Center""
                                          VerticalAlignment=""Center""
                                          Margin=""{TemplateBinding Padding}""/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property=""IsMouseOver"" Value=""True"">
                            <Trigger.EnterActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <ColorAnimation Storyboard.TargetName=""AnimatedBorderBrush""
                                                        Storyboard.TargetProperty=""Color""
                                                        To=""#FF007ACC"" Duration=""0:0:0.25""/>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.EnterActions>
                            <Trigger.ExitActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <ColorAnimation Storyboard.TargetName=""AnimatedBorderBrush""
                                                        Storyboard.TargetProperty=""Color""
                                                        To=""#FFE0E0E0"" Duration=""0:0:0.25""/>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.ExitActions>
                            <Setter TargetName=""ButtonBorder"" Property=""Background"" Value=""{StaticResource BimSurfaceHover}""/>
                        </Trigger>
                        <Trigger Property=""IsPressed"" Value=""True"">
                            <Setter TargetName=""ButtonBorder"" Property=""Background"" Value=""{StaticResource BimPressed}""/>
                            <Trigger.EnterActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <DoubleAnimation Storyboard.TargetName=""PressScale"" Storyboard.TargetProperty=""ScaleX"" To=""0.98"" Duration=""0:0:0.08""/>
                                        <DoubleAnimation Storyboard.TargetName=""PressScale"" Storyboard.TargetProperty=""ScaleY"" To=""0.98"" Duration=""0:0:0.08""/>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.EnterActions>
                            <Trigger.ExitActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <DoubleAnimation Storyboard.TargetName=""PressScale"" Storyboard.TargetProperty=""ScaleX"" To=""1"" Duration=""0:0:0.1""/>
                                        <DoubleAnimation Storyboard.TargetName=""PressScale"" Storyboard.TargetProperty=""ScaleY"" To=""1"" Duration=""0:0:0.1""/>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.ExitActions>
                        </Trigger>
                        <Trigger Property=""IsEnabled"" Value=""False"">
                            <Setter TargetName=""ButtonBorder"" Property=""Background"" Value=""{StaticResource BimBackground}""/>
                            <Setter Property=""Foreground"" Value=""{StaticResource BimTextSecondary}""/>
                            <Setter Property=""Opacity"" Value=""0.6""/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- Toolbar button: compact variant -->
    <Style x:Key=""BimToolbarButton"" TargetType=""{x:Type Button}"" BasedOn=""{StaticResource {x:Type Button}}"">
        <Setter Property=""MinWidth"" Value=""0""/>
        <Setter Property=""Padding"" Value=""8,3""/>
        <Setter Property=""Margin"" Value=""3,2""/>
    </Style>

    <!-- Primary action: filled blue, same ring/press choreography -->
    <Style x:Key=""BimPrimaryButton"" TargetType=""{x:Type Button}"">
        <Setter Property=""Background"" Value=""{StaticResource BimPrimary}""/>
        <Setter Property=""Foreground"" Value=""{StaticResource BimTextOnPrimary}""/>
        <Setter Property=""BorderThickness"" Value=""0.8""/>
        <Setter Property=""Padding"" Value=""12,4""/>
        <Setter Property=""FontFamily"" Value=""Segoe UI""/>
        <Setter Property=""FontSize"" Value=""12""/>
        <Setter Property=""FontWeight"" Value=""Medium""/>
        <Setter Property=""Height"" Value=""28""/>
        <Setter Property=""MinWidth"" Value=""80""/>
        <Setter Property=""Cursor"" Value=""Hand""/>
        <Setter Property=""Template"">
            <Setter.Value>
                <ControlTemplate TargetType=""{x:Type Button}"">
                    <Border x:Name=""ButtonBorder""
                            Background=""{TemplateBinding Background}""
                            BorderThickness=""{TemplateBinding BorderThickness}""
                            CornerRadius=""6""
                            RenderTransformOrigin=""0.5,0.5"">
                        <Border.BorderBrush>
                            <SolidColorBrush x:Name=""AnimatedBorderBrush"" Color=""#FF005A9E""/>
                        </Border.BorderBrush>
                        <Border.Effect>
                            <DropShadowEffect Color=""#20000000"" Direction=""270"" ShadowDepth=""1"" BlurRadius=""6"" Opacity=""0.35""/>
                        </Border.Effect>
                        <Border.RenderTransform>
                            <ScaleTransform x:Name=""PressScale"" ScaleX=""1"" ScaleY=""1""/>
                        </Border.RenderTransform>
                        <ContentPresenter HorizontalAlignment=""Center""
                                          VerticalAlignment=""Center""
                                          Margin=""{TemplateBinding Padding}""/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property=""IsMouseOver"" Value=""True"">
                            <Trigger.EnterActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <ColorAnimation Storyboard.TargetName=""AnimatedBorderBrush""
                                                        Storyboard.TargetProperty=""Color""
                                                        To=""#FFFFFFFF"" Duration=""0:0:0.25""/>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.EnterActions>
                            <Trigger.ExitActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <ColorAnimation Storyboard.TargetName=""AnimatedBorderBrush""
                                                        Storyboard.TargetProperty=""Color""
                                                        To=""#FF005A9E"" Duration=""0:0:0.25""/>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.ExitActions>
                            <Setter TargetName=""ButtonBorder"" Property=""Background"" Value=""{StaticResource BimPrimaryHover}""/>
                        </Trigger>
                        <Trigger Property=""IsPressed"" Value=""True"">
                            <Setter TargetName=""ButtonBorder"" Property=""Background"" Value=""{StaticResource BimPrimaryPressed}""/>
                            <Trigger.EnterActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <DoubleAnimation Storyboard.TargetName=""PressScale"" Storyboard.TargetProperty=""ScaleX"" To=""0.98"" Duration=""0:0:0.08""/>
                                        <DoubleAnimation Storyboard.TargetName=""PressScale"" Storyboard.TargetProperty=""ScaleY"" To=""0.98"" Duration=""0:0:0.08""/>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.EnterActions>
                            <Trigger.ExitActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <DoubleAnimation Storyboard.TargetName=""PressScale"" Storyboard.TargetProperty=""ScaleX"" To=""1"" Duration=""0:0:0.1""/>
                                        <DoubleAnimation Storyboard.TargetName=""PressScale"" Storyboard.TargetProperty=""ScaleY"" To=""1"" Duration=""0:0:0.1""/>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.ExitActions>
                        </Trigger>
                        <Trigger Property=""IsEnabled"" Value=""False"">
                            <Setter TargetName=""ButtonBorder"" Property=""Background"" Value=""{StaticResource BimBackground}""/>
                            <Setter Property=""Foreground"" Value=""{StaticResource BimTextSecondary}""/>
                            <Setter Property=""Opacity"" Value=""0.6""/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
    <!-- ScrollBar: thin overlay track + rounded thumb (8px, resting opacity 0.22) -->
    <Style x:Key=""BimScrollBarTrackButtonStyle"" TargetType=""{x:Type RepeatButton}"">
        <Setter Property=""Focusable"" Value=""False""/>
        <Setter Property=""IsTabStop"" Value=""False""/>
        <Setter Property=""Template"">
            <Setter.Value>
                <ControlTemplate TargetType=""{x:Type RepeatButton}"">
                    <Border Background=""Transparent""/>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style x:Key=""BimScrollBarThumbStyle"" TargetType=""{x:Type Thumb}"">
        <Setter Property=""Background"" Value=""#CBD5E1""/>
        <Setter Property=""Template"">
            <Setter.Value>
                <ControlTemplate TargetType=""{x:Type Thumb}"">
                    <Border x:Name=""ThumbBorder""
                            Margin=""1""
                            CornerRadius=""4""
                            Background=""{TemplateBinding Background}""/>
                    <ControlTemplate.Triggers>
                        <Trigger Property=""IsMouseOver"" Value=""True"">
                            <Setter TargetName=""ThumbBorder"" Property=""Background"" Value=""#AEB8C5""/>
                        </Trigger>
                        <Trigger Property=""IsDragging"" Value=""True"">
                            <Setter TargetName=""ThumbBorder"" Property=""Background"" Value=""#94A3B8""/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <ControlTemplate x:Key=""BimVerticalScrollBarTemplate"" TargetType=""{x:Type ScrollBar}"">
        <Grid Width=""{TemplateBinding Width}"" Background=""Transparent"" SnapsToDevicePixels=""True"">
            <Track x:Name=""PART_Track"" IsDirectionReversed=""True"">
                <Track.DecreaseRepeatButton>
                    <RepeatButton Command=""ScrollBar.PageUpCommand"" Style=""{StaticResource BimScrollBarTrackButtonStyle}""/>
                </Track.DecreaseRepeatButton>
                <Track.Thumb>
                    <Thumb Style=""{StaticResource BimScrollBarThumbStyle}""/>
                </Track.Thumb>
                <Track.IncreaseRepeatButton>
                    <RepeatButton Command=""ScrollBar.PageDownCommand"" Style=""{StaticResource BimScrollBarTrackButtonStyle}""/>
                </Track.IncreaseRepeatButton>
            </Track>
        </Grid>
    </ControlTemplate>

    <ControlTemplate x:Key=""BimHorizontalScrollBarTemplate"" TargetType=""{x:Type ScrollBar}"">
        <Grid Height=""{TemplateBinding Height}"" Background=""Transparent"" SnapsToDevicePixels=""True"">
            <Track x:Name=""PART_Track"">
                <Track.DecreaseRepeatButton>
                    <RepeatButton Command=""ScrollBar.PageLeftCommand"" Style=""{StaticResource BimScrollBarTrackButtonStyle}""/>
                </Track.DecreaseRepeatButton>
                <Track.Thumb>
                    <Thumb Style=""{StaticResource BimScrollBarThumbStyle}""/>
                </Track.Thumb>
                <Track.IncreaseRepeatButton>
                    <RepeatButton Command=""ScrollBar.PageRightCommand"" Style=""{StaticResource BimScrollBarTrackButtonStyle}""/>
                </Track.IncreaseRepeatButton>
            </Track>
        </Grid>
    </ControlTemplate>

    <Style x:Key=""BimVerticalScrollBar"" TargetType=""{x:Type ScrollBar}"">
        <Setter Property=""Orientation"" Value=""Vertical""/>
        <Setter Property=""Background"" Value=""Transparent""/>
        <Setter Property=""BorderThickness"" Value=""0""/>
        <Setter Property=""Opacity"" Value=""0.22""/>
        <Setter Property=""Width"" Value=""8""/>
        <Setter Property=""MinWidth"" Value=""8""/>
        <Setter Property=""Template"" Value=""{StaticResource BimVerticalScrollBarTemplate}""/>
    </Style>

    <Style x:Key=""BimHorizontalScrollBar"" TargetType=""{x:Type ScrollBar}"">
        <Setter Property=""Orientation"" Value=""Horizontal""/>
        <Setter Property=""Background"" Value=""Transparent""/>
        <Setter Property=""BorderThickness"" Value=""0""/>
        <Setter Property=""Opacity"" Value=""0.22""/>
        <Setter Property=""Height"" Value=""8""/>
        <Setter Property=""MinHeight"" Value=""8""/>
        <Setter Property=""Template"" Value=""{StaticResource BimHorizontalScrollBarTemplate}""/>
    </Style>

    <Style x:Key=""BimScrollBar"" TargetType=""{x:Type ScrollBar}"" BasedOn=""{StaticResource BimVerticalScrollBar}"">
        <Style.Triggers>
            <Trigger Property=""Orientation"" Value=""Horizontal"">
                <Setter Property=""Height"" Value=""8""/>
                <Setter Property=""MinHeight"" Value=""8""/>
                <Setter Property=""Width"" Value=""Auto""/>
                <Setter Property=""MinWidth"" Value=""0""/>
                <Setter Property=""Template"" Value=""{StaticResource BimHorizontalScrollBarTemplate}""/>
            </Trigger>
        </Style.Triggers>
    </Style>

    <Style TargetType=""{x:Type ScrollBar}"" BasedOn=""{StaticResource BimScrollBar}""/>
</ResourceDictionary>";
            return (ResourceDictionary)XamlReader.Parse(xaml);
        }
    }
}
