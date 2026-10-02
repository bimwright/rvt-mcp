using System.Windows;
using System.Windows.Markup;

namespace RvtMcp.Plugin.Views.Toast
{
    /// <summary>
    /// Vertical subset of rvt-mcp BimwrightStyles: 8 DIP overlay track, rounded
    /// #CBD5E1 thumb, #AEB8C5 hover / #94A3B8 drag, resting opacity 0.22, no arrows.
    /// Keep this self-contained; public gateways do not build against each other.
    /// </summary>
    internal static class ToastScrollBarStyles
    {
        internal static ResourceDictionary Create() => (ResourceDictionary)XamlReader.Parse(@"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style x:Key='ToastTrackButton' TargetType='{x:Type RepeatButton}'>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='IsTabStop' Value='False'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='{x:Type RepeatButton}'><Border Background='Transparent'/></ControlTemplate>
    </Setter.Value></Setter>
  </Style>
  <Style x:Key='ToastScrollThumb' TargetType='{x:Type Thumb}'>
    <Setter Property='Background' Value='#CBD5E1'/>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='{x:Type Thumb}'>
        <Border x:Name='ThumbBorder' Margin='1' CornerRadius='4' Background='{TemplateBinding Background}'/>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='ThumbBorder' Property='Background' Value='#AEB8C5'/></Trigger>
          <Trigger Property='IsDragging' Value='True'><Setter TargetName='ThumbBorder' Property='Background' Value='#94A3B8'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>
  <Style x:Key='ToastThinScrollBar' TargetType='{x:Type ScrollBar}'>
    <Setter Property='Orientation' Value='Vertical'/>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='BorderThickness' Value='0'/>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='Opacity' Value='0.22'/>
    <Setter Property='Width' Value='8'/>
    <Setter Property='MinWidth' Value='8'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='{x:Type ScrollBar}'>
        <Grid Width='{TemplateBinding Width}' Background='Transparent' SnapsToDevicePixels='True'>
          <Track x:Name='PART_Track' IsDirectionReversed='True'>
            <Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageUpCommand' Style='{StaticResource ToastTrackButton}'/></Track.DecreaseRepeatButton>
            <Track.Thumb><Thumb Style='{StaticResource ToastScrollThumb}'/></Track.Thumb>
            <Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageDownCommand' Style='{StaticResource ToastTrackButton}'/></Track.IncreaseRepeatButton>
          </Track>
        </Grid>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>
  <Style x:Key='ToastActivityScrollViewer' TargetType='{x:Type ScrollViewer}'>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='{x:Type ScrollViewer}'>
        <Grid Background='Transparent'>
          <ScrollContentPresenter x:Name='PART_ScrollContentPresenter' Margin='0,0,12,0'
              CanContentScroll='{TemplateBinding CanContentScroll}' Content='{TemplateBinding Content}'
              ContentTemplate='{TemplateBinding ContentTemplate}'/>
          <ScrollBar x:Name='PART_VerticalScrollBar' HorizontalAlignment='Right'
              Minimum='0' Maximum='{TemplateBinding ScrollableHeight}' Value='{TemplateBinding VerticalOffset}'
              ViewportSize='{TemplateBinding ViewportHeight}' Visibility='{TemplateBinding ComputedVerticalScrollBarVisibility}'
              Style='{StaticResource ToastThinScrollBar}'/>
        </Grid>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>
  <Style x:Key='ToastActivityList' TargetType='{x:Type ListBox}'>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='BorderThickness' Value='0'/>
    <Setter Property='Padding' Value='0'/>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='ScrollViewer.CanContentScroll' Value='True'/>
    <Setter Property='ScrollViewer.VerticalScrollBarVisibility' Value='Auto'/>
    <Setter Property='ScrollViewer.HorizontalScrollBarVisibility' Value='Disabled'/>
    <Setter Property='VirtualizingPanel.IsVirtualizing' Value='True'/>
    <Setter Property='VirtualizingPanel.VirtualizationMode' Value='Recycling'/>
    <Setter Property='VirtualizingPanel.ScrollUnit' Value='Pixel'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='{x:Type ListBox}'>
        <ScrollViewer x:Name='PART_ScrollViewer' CanContentScroll='True'
            VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Disabled'
            Style='{StaticResource ToastActivityScrollViewer}'><ItemsPresenter/></ScrollViewer>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>
</ResourceDictionary>");
    }
}
