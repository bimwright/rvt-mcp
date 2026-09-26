using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;

namespace RvtMcp.Plugin.Views.Settings
{
    /// <summary>Window-scoped styles. Never changes the Revit host or the History window.</summary>
    internal static class SettingsStyles
    {
        internal static readonly Brush Background = Color("#F6F8FB");
        internal static readonly Brush Text = Color("#1E293B");
        internal static readonly Brush Secondary = Color("#64748B");
        internal static readonly Brush Line = Color("#E2E8F0");
        internal static readonly Brush Blue = Color("#007ACC");
        internal static readonly Brush Error = Color("#B42318");
        internal static readonly Brush Surface = Color("#FFFFFF");
        internal static readonly Brush SuccessText = Color("#067647");
        internal static readonly Brush SuccessFill = Color("#ECFDF3");
        internal static readonly Brush SuccessDot = Color("#17B26A");
        internal static readonly Brush WarningText = Color("#B54708");
        internal static readonly Brush WarningFill = Color("#FFFAEB");
        internal static readonly Brush WarningDot = Color("#F79009");
        internal static readonly Brush NeutralFill = Color("#F1F5F9");
        internal static readonly Brush NeutralDot = Color("#94A3B8");
        // Same wordmark colours as the toast (McpToastTheme): navy "BIM" + green "wright".
        internal static readonly Brush BrandBim = Color("#0C3F76");
        internal static readonly Brush BrandWright = Color("#589039");
        internal static Brush Color(string hex)
        {
            var brush = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        internal static ResourceDictionary Create()
        {
            var dictionary = (ResourceDictionary)XamlReader.Parse(Xaml);
            // Same overlay scrollbar as History; the window pairs it with ScrollBarFadeBehavior.
            dictionary.Add(typeof(ScrollBar), BimwrightStyles.Dictionary["BimScrollBar"]);
            return dictionary;
        }

        private const string Xaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style x:Key='FocusRing'>
    <Setter Property='Control.Template'>
      <Setter.Value><ControlTemplate><Border BorderBrush='#007ACC' BorderThickness='2' CornerRadius='5' Margin='-3'/></ControlTemplate></Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='Button'>
    <Setter Property='Background' Value='White'/><Setter Property='Foreground' Value='#1E293B'/>
    <Setter Property='BorderBrush' Value='#CBD5E1'/><Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Padding' Value='12,4'/><Setter Property='MinHeight' Value='28'/>
    <Setter Property='FontSize' Value='13'/><Setter Property='FontWeight' Value='SemiBold'/>
    <Setter Property='Cursor' Value='Hand'/><Setter Property='FocusVisualStyle' Value='{StaticResource FocusRing}'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='Button'>
        <Border x:Name='Chrome' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='6'>
          <ContentPresenter Margin='{TemplateBinding Padding}' HorizontalAlignment='Center' VerticalAlignment='Center'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Chrome' Property='BorderBrush' Value='#007ACC'/></Trigger>
          <Trigger Property='IsPressed' Value='True'><Setter TargetName='Chrome' Property='Opacity' Value='0.75'/></Trigger>
          <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/><Setter Property='Cursor' Value='Arrow'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>
  <Style x:Key='Primary' TargetType='Button' BasedOn='{StaticResource {x:Type Button}}'>
    <Setter Property='Background' Value='#007ACC'/><Setter Property='Foreground' Value='White'/><Setter Property='BorderBrush' Value='#007ACC'/>
    <Style.Triggers>
      <Trigger Property='IsMouseOver' Value='True'><Setter Property='Background' Value='#0067AD'/><Setter Property='BorderBrush' Value='#0067AD'/></Trigger>
    </Style.Triggers>
  </Style>
  <Style x:Key='Link' TargetType='Button' BasedOn='{StaticResource {x:Type Button}}'>
    <Setter Property='Background' Value='Transparent'/><Setter Property='BorderThickness' Value='0'/>
    <Setter Property='Foreground' Value='#007ACC'/><Setter Property='Padding' Value='0,6'/><Setter Property='MinHeight' Value='0'/>
    <Style.Triggers>
      <Trigger Property='IsMouseOver' Value='True'><Setter Property='Foreground' Value='#005A9E'/></Trigger>
    </Style.Triggers>
  </Style>
  <Style x:Key='Icon' TargetType='Button' BasedOn='{StaticResource {x:Type Button}}'>
    <Setter Property='Background' Value='Transparent'/><Setter Property='BorderBrush' Value='Transparent'/>
    <Setter Property='Foreground' Value='#64748B'/><Setter Property='Padding' Value='0'/>
    <Setter Property='Width' Value='28'/><Setter Property='Height' Value='28'/><Setter Property='MinHeight' Value='28'/>
    <Setter Property='FontFamily' Value='Segoe MDL2 Assets'/><Setter Property='FontSize' Value='14'/><Setter Property='FontWeight' Value='Normal'/>
    <Style.Triggers>
      <Trigger Property='IsMouseOver' Value='True'><Setter Property='Background' Value='#F1F5F9'/><Setter Property='Foreground' Value='#007ACC'/></Trigger>
    </Style.Triggers>
  </Style>
  <Style TargetType='CheckBox'>
    <Setter Property='FocusVisualStyle' Value='{StaticResource FocusRing}'/>
    <Setter Property='Cursor' Value='Hand'/><Setter Property='VerticalAlignment' Value='Center'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='CheckBox'>
        <Grid Width='40' Height='24' Background='Transparent'>
          <Border x:Name='Track' Height='22' CornerRadius='11' Background='#94A3B8'/>
          <Ellipse x:Name='Thumb' Width='16' Height='16' Fill='White' HorizontalAlignment='Left' Margin='3,0,0,0'>
            <Ellipse.RenderTransform><TranslateTransform x:Name='Shift'/></Ellipse.RenderTransform>
          </Ellipse>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property='IsChecked' Value='True'><Setter TargetName='Track' Property='Background' Value='#007ACC'/><Setter TargetName='Thumb' Property='RenderTransform'><Setter.Value><TranslateTransform X='18'/></Setter.Value></Setter></Trigger>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Track' Property='Opacity' Value='0.85'/></Trigger>
          <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>
  <Style TargetType='ComboBox'>
    <Setter Property='MinHeight' Value='28'/><Setter Property='Padding' Value='10,3'/>
    <Setter Property='Background' Value='White'/><Setter Property='Foreground' Value='#1E293B'/>
    <Setter Property='BorderBrush' Value='#CBD5E1'/><Setter Property='BorderThickness' Value='1'/>
    <Setter Property='VerticalContentAlignment' Value='Center'/><Setter Property='HorizontalContentAlignment' Value='Left'/>
    <Setter Property='ScrollViewer.CanContentScroll' Value='True'/>
    <Setter Property='FocusVisualStyle' Value='{StaticResource FocusRing}'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='ComboBox'>
        <Grid>
          <ToggleButton Focusable='False' IsChecked='{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}' ClickMode='Press'>
            <ToggleButton.Template><ControlTemplate TargetType='ToggleButton'>
              <Border x:Name='Chrome' Background='White' BorderBrush='#CBD5E1' BorderThickness='1' CornerRadius='6'>
                <Path Data='M0,0 L4,4 L8,0' Stroke='#64748B' StrokeThickness='1.5' HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,12,0'/>
              </Border>
              <ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Chrome' Property='BorderBrush' Value='#007ACC'/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate></ToggleButton.Template>
          </ToggleButton>
          <ContentPresenter IsHitTestVisible='False' Content='{TemplateBinding SelectionBoxItem}' ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}'
                            ContentStringFormat='{TemplateBinding SelectionBoxItemStringFormat}'
                            Margin='10,3,30,3' VerticalAlignment='Center' HorizontalAlignment='Left'/>
          <Popup x:Name='PART_Popup' IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Bottom' AllowsTransparency='True' Focusable='False'>
            <Border Background='White' BorderBrush='#CBD5E1' BorderThickness='1' CornerRadius='6' Margin='0,3,0,3'
                    MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}' MaxHeight='{TemplateBinding MaxDropDownHeight}'>
              <ScrollViewer Margin='4' CanContentScroll='True'><ItemsPresenter KeyboardNavigation.DirectionalNavigation='Contained'/></ScrollViewer>
            </Border>
          </Popup>
        </Grid>
        <ControlTemplate.Triggers><Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger></ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>
  <Style TargetType='ComboBoxItem'>
    <Setter Property='Padding' Value='8,4'/><Setter Property='MinHeight' Value='26'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ComboBoxItem'>
      <Border x:Name='Chrome' Background='Transparent' CornerRadius='4'><ContentPresenter Margin='{TemplateBinding Padding}'/></Border>
      <ControlTemplate.Triggers>
        <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Chrome' Property='Background' Value='#EEF4FA'/></Trigger>
        <Trigger Property='IsSelected' Value='True'><Setter TargetName='Chrome' Property='Background' Value='#E6F2FA'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType='TabControl'>
    <Setter Property='Background' Value='Transparent'/><Setter Property='BorderThickness' Value='0'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='TabControl'>
      <Grid KeyboardNavigation.TabNavigation='Local'>
        <Grid.RowDefinitions><RowDefinition Height='Auto'/><RowDefinition Height='*'/></Grid.RowDefinitions>
        <Border BorderBrush='#E2E8F0' BorderThickness='0,0,0,1'><TabPanel IsItemsHost='True' KeyboardNavigation.TabIndex='1'/></Border>
        <ContentPresenter x:Name='PART_SelectedContentHost' Grid.Row='1' ContentSource='SelectedContent' Margin='0,12,0,0'/>
      </Grid>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <!-- Tab page content inherits from its TabItem, so caption colour/weight live on the header presenter only. -->
  <Style TargetType='TabItem'>
    <Setter Property='Padding' Value='0,8'/><Setter Property='FocusVisualStyle' Value='{StaticResource FocusRing}'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='TabItem'>
      <Border x:Name='Underline' BorderBrush='Transparent' BorderThickness='0,0,0,2' Background='Transparent' Margin='0,0,24,0' Cursor='Hand'>
        <ContentPresenter x:Name='Header' ContentSource='Header' Margin='{TemplateBinding Padding}' RecognizesAccessKey='True'
                          TextElement.Foreground='#64748B' TextElement.FontSize='13'/>
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Header' Property='TextElement.Foreground' Value='#007ACC'/></Trigger>
        <Trigger Property='IsSelected' Value='True'>
          <Setter TargetName='Underline' Property='BorderBrush' Value='#007ACC'/>
          <Setter TargetName='Header' Property='TextElement.Foreground' Value='#1E293B'/>
          <Setter TargetName='Header' Property='TextElement.FontWeight' Value='SemiBold'/>
        </Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType='DataGrid'>
    <Setter Property='Background' Value='Transparent'/><Setter Property='BorderThickness' Value='0'/>
    <Setter Property='RowBackground' Value='Transparent'/><Setter Property='AlternatingRowBackground' Value='Transparent'/>
    <Setter Property='HorizontalGridLinesBrush' Value='#F1F5F9'/><Setter Property='ColumnHeaderHeight' Value='32'/>
    <Setter Property='RowHeight' Value='32'/><Setter Property='FontSize' Value='12'/><Setter Property='Foreground' Value='#1E293B'/>
    <Setter Property='EnableRowVirtualization' Value='True'/><Setter Property='EnableColumnVirtualization' Value='True'/>
    <Setter Property='ScrollViewer.CanContentScroll' Value='True'/>
  </Style>
  <Style TargetType='DataGridColumnHeader'>
    <Setter Property='Background' Value='Transparent'/><Setter Property='Foreground' Value='#64748B'/>
    <Setter Property='FontWeight' Value='SemiBold'/><Setter Property='Padding' Value='12,0'/><Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='DataGridColumnHeader'>
      <Border Background='{TemplateBinding Background}' BorderBrush='#E2E8F0' BorderThickness='0,0,0,1' Padding='{TemplateBinding Padding}'>
        <StackPanel Orientation='Horizontal' VerticalAlignment='Center'>
          <ContentPresenter VerticalAlignment='Center' RecognizesAccessKey='True'/>
          <Path x:Name='SortArrow' Data='M0,0 L4,4 L8,0' Stroke='#64748B' StrokeThickness='1.5' Margin='6,1,0,0'
                VerticalAlignment='Center' Visibility='Collapsed' RenderTransformOrigin='0.5,0.5'/>
        </StackPanel>
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property='SortDirection' Value='Ascending'>
          <Setter TargetName='SortArrow' Property='Visibility' Value='Visible'/>
          <Setter TargetName='SortArrow' Property='RenderTransform'><Setter.Value><RotateTransform Angle='180'/></Setter.Value></Setter>
        </Trigger>
        <Trigger Property='SortDirection' Value='Descending'><Setter TargetName='SortArrow' Property='Visibility' Value='Visible'/></Trigger>
        <Trigger Property='IsMouseOver' Value='True'><Setter Property='Foreground' Value='#1E293B'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType='DataGridRow'>
    <Style.Triggers>
      <Trigger Property='IsMouseOver' Value='True'><Setter Property='Background' Value='#F8FAFC'/></Trigger>
    </Style.Triggers>
  </Style>
  <Style TargetType='DataGridCell'>
    <Setter Property='BorderThickness' Value='0'/><Setter Property='Padding' Value='12,0'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='DataGridCell'>
      <Border Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}'><ContentPresenter VerticalAlignment='Center'/></Border>
    </ControlTemplate></Setter.Value></Setter>
    <Style.Triggers>
      <Trigger Property='IsSelected' Value='True'><Setter Property='Background' Value='#E6F2FA'/><Setter Property='Foreground' Value='#1E293B'/></Trigger>
      <Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter Property='Background' Value='#DBEDFA'/></Trigger>
    </Style.Triggers>
  </Style>
</ResourceDictionary>";
    }
}
