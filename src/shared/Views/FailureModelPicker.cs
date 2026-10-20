using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;

namespace RvtMcp.Plugin.Views
{
    /// <summary>Keeps the search draft separate from the committed Issues model selection.</summary>
    internal sealed class FailureModelPicker : ComboBox
    {
        private object _previewItem;
        private DispatcherOperation _pendingFocus;
        private DispatcherOperation _pendingPreview;
        internal object PreviewedItem => _previewItem;
        internal TextBox SearchBox { get; } = new TextBox {
            MaxLength = 160, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        internal TextBlock SearchHint { get; } = new TextBlock {
            VerticalAlignment = VerticalAlignment.Center, Foreground = SystemColors.GrayTextBrush,
            IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis
        };
        internal TextBlock EmptyHint { get; } = new TextBlock {
            Margin = new Thickness(6), TextWrapping = TextWrapping.Wrap,
            Foreground = SystemColors.GrayTextBrush, Visibility = Visibility.Collapsed
        };

        internal FailureModelPicker()
        {
            IsTextSearchEnabled = false;
            ItemContainerStyle = CreateItemStyle();
            SearchBox.TextChanged += (s, e) => { SetPreview(null); QueuePreview(); };
            ItemContainerGenerator.StatusChanged += (s, e) => QueuePreview();
            Template = (ControlTemplate)XamlReader.Parse(@"
<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                 xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ComboBox'>
  <Grid>
    <ToggleButton Focusable='False' ClickMode='Press'
                  IsChecked='{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}'>
      <ToggleButton.Template>
        <ControlTemplate TargetType='ToggleButton'>
          <Border x:Name='Chrome' Background='White' BorderBrush='#ABADB3' BorderThickness='1' CornerRadius='2'>
            <Path Data='M 0 0 L 4 4 L 8 0 Z' Fill='#444444' HorizontalAlignment='Right'
                  VerticalAlignment='Center' Margin='0,0,8,0'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Chrome' Property='BorderBrush' Value='#007ACC'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </ToggleButton.Template>
    </ToggleButton>
    <ContentPresenter x:Name='Selection' Margin='6,1,28,1' IsHitTestVisible='False'
                      VerticalAlignment='Center' Content='{TemplateBinding SelectionBoxItem}'
                      ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}'/>
    <ContentControl x:Name='Search' Margin='6,2,28,2' Visibility='Collapsed'
                    HorizontalContentAlignment='Stretch' VerticalContentAlignment='Stretch'/>
    <Popup x:Name='PART_Popup' Placement='Bottom' AllowsTransparency='True' Focusable='False'
           IsOpen='{TemplateBinding IsDropDownOpen}'>
      <Border Background='White' BorderBrush='#ABADB3' BorderThickness='1'
              Width='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}'>
        <StackPanel>
          <ContentControl x:Name='Empty'/>
          <ScrollViewer MaxHeight='{Binding MaxDropDownHeight, RelativeSource={RelativeSource TemplatedParent}}'
                        CanContentScroll='True' HorizontalScrollBarVisibility='Disabled'
                        VerticalScrollBarVisibility='Auto'>
            <ItemsPresenter KeyboardNavigation.DirectionalNavigation='Contained'/>
          </ScrollViewer>
        </StackPanel>
      </Border>
    </Popup>
  </Grid>
  <ControlTemplate.Triggers>
    <Trigger Property='IsDropDownOpen' Value='True'>
      <Setter TargetName='Search' Property='Visibility' Value='Visible'/>
      <Setter TargetName='Selection' Property='Visibility' Value='Collapsed'/>
    </Trigger>
    <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.5'/></Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>");
        }

        // Native IsHighlighted also follows the committed selection. Do not mix that state
        // with the search preview: mouse and keyboard share exactly one highlighted row.
        internal static Style CreateItemStyle()
        {
            var style = (Style)XamlReader.Parse(@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ComboBoxItem'>
  <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
  <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='ComboBoxItem'>
        <Border x:Name='Row' Background='Transparent' BorderBrush='Transparent' BorderThickness='1' Padding='4,3'>
          <ContentPresenter HorizontalAlignment='Stretch' VerticalAlignment='Center'/>
        </Border>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>");
            // Use the actual dependency property: a string DataTrigger binding to a
            // private item type can leave IsPreviewed=true without painting the row.
            var template = (ControlTemplate)style.Setters.OfType<Setter>().Single(s => s.Property == TemplateProperty).Value;
            var preview = new Trigger { Property = PreviewItem.IsPreviewedProperty, Value = true };
            preview.Setters.Add(new Setter(Border.BackgroundProperty,
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0xF2, 0xFF)), "Row"));
            preview.Setters.Add(new Setter(Border.BorderBrushProperty,
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7A, 0xB7, 0xEA)), "Row"));
            template.Triggers.Add(preview);
            return style;
        }

        protected override DependencyObject GetContainerForItemOverride() => new PreviewItem(this);

        protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
        {
            base.OnItemsChanged(e);
            QueuePreview();
        }

        private List<ComboBoxItem> VisibleRows() => Items.Cast<object>()
            .Select(item => ItemContainerGenerator.ContainerFromItem(item) as ComboBoxItem)
            .Where(row => row != null && row.Visibility == Visibility.Visible && row.IsEnabled).ToList();

        private void SetPreview(object item)
        {
            _previewItem = item;
            foreach (var row in Items.Cast<object>().Select(value => ItemContainerGenerator.ContainerFromItem(value)).OfType<PreviewItem>())
                row.IsPreviewed = IsDropDownOpen && Equals(row.Content, item);
        }

        private void QueuePreview()
        {
            _pendingPreview?.Abort();
            if (!IsDropDownOpen) return;
            _pendingPreview = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => {
                _pendingPreview = null;
                if (!IsDropDownOpen) return;
                UpdateLayout();
                var rows = VisibleRows();
                var row = rows.FirstOrDefault(value => Equals(value.Content, _previewItem))
                    ?? (string.IsNullOrWhiteSpace(SearchBox.Text) ? rows.FirstOrDefault(value => Equals(value.Content, SelectedItem)) : null)
                    ?? rows.FirstOrDefault();
                SetPreview(row?.Content);
            }));
        }

        private sealed class PreviewItem : ComboBoxItem
        {
            private readonly FailureModelPicker _owner;
            public static readonly DependencyProperty IsPreviewedProperty =
                DependencyProperty.Register(nameof(IsPreviewed), typeof(bool), typeof(PreviewItem), new PropertyMetadata(false));
            public bool IsPreviewed { get => (bool)GetValue(IsPreviewedProperty); set => SetValue(IsPreviewedProperty, value); }
            internal PreviewItem(FailureModelPicker owner) { _owner = owner; }
            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (IsEnabled && _owner.IsDropDownOpen) _owner.SetPreview(Content);
            }
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            // Detach before a theme/template reapplication reparents these controls.
            if (SearchBox.Parent is Panel oldHost) oldHost.Children.Clear();
            if (EmptyHint.Parent is ContentControl oldEmpty) oldEmpty.Content = null;
            var searchHost = new Grid();
            searchHost.Children.Add(SearchBox);
            searchHost.Children.Add(SearchHint);
            ((ContentControl)GetTemplateChild("Search")).Content = searchHost;
            ((ContentControl)GetTemplateChild("Empty")).Content = EmptyHint;
        }

        protected override void OnDropDownOpened(EventArgs e)
        {
            SetPreview(SelectedItem);
            base.OnDropDownOpened(e);
            QueuePreview();
            _pendingFocus?.Abort();
            _pendingFocus = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => {
                _pendingFocus = null;
                if (IsDropDownOpen) SearchBox.Focus();
            }));
        }

        protected override void OnDropDownClosed(EventArgs e)
        {
            _pendingFocus?.Abort();
            _pendingPreview?.Abort();
            SetPreview(null);
            if (SearchBox.IsKeyboardFocusWithin) Focus();
            SearchBox.Clear();
            base.OnDropDownClosed(e);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape || (e.Key == Key.Tab && IsDropDownOpen))
            {
                IsDropDownOpen = false;
                e.Handled = e.Key == Key.Escape;
            }
            else if ((e.Key == Key.Down || e.Key == Key.Up) && Keyboard.Modifiers == ModifierKeys.None)
            {
                var wasOpen = IsDropDownOpen;
                IsDropDownOpen = true;
                _pendingPreview?.Abort();
                UpdateLayout();
                var rows = VisibleRows();
                var index = rows.FindIndex(row => Equals(row.Content, _previewItem));
                var next = index < 0 ? (e.Key == Key.Down ? 0 : rows.Count - 1)
                    : Math.Max(0, Math.Min(rows.Count - 1, index + (e.Key == Key.Down ? 1 : -1)));
                if (wasOpen && rows.Count > 0) { SetPreview(rows[next].Content); rows[next].BringIntoView(); }
                SearchBox.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && IsDropDownOpen)
            {
                var rows = VisibleRows();
                var row = rows.FirstOrDefault(item => Equals(item.Content, _previewItem))
                    ?? (string.IsNullOrWhiteSpace(SearchBox.Text) ? rows.FirstOrDefault(item => Equals(item.Content, SelectedItem)) : null)
                    ?? rows.FirstOrDefault();
                if (row != null) { SelectedItem = ItemContainerGenerator.ItemFromContainer(row); IsDropDownOpen = false; Focus(); }
                e.Handled = true;
            }
            if (!e.Handled) base.OnPreviewKeyDown(e);
        }

        protected override void OnPreviewTextInput(TextCompositionEventArgs e)
        {
            if (!SearchBox.IsKeyboardFocusWithin && !string.IsNullOrEmpty(e.Text))
            {
                IsDropDownOpen = true;
                SearchBox.Focus();
                SearchBox.SelectedText = e.Text;
                SearchBox.CaretIndex = SearchBox.SelectionStart + SearchBox.SelectionLength;
                SearchBox.SelectionLength = 0;
                e.Handled = true;
            }
            base.OnPreviewTextInput(e);
        }
    }
}
