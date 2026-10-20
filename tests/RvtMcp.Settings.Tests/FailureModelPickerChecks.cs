using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RvtMcp.Plugin.Views;

internal static class FailureModelPickerChecks
{
    internal static void Run(string snapshots)
    {
        var picker = new FailureModelPicker { Height = 28, MinWidth = 440, MaxDropDownHeight = 160 };
        var entries = new[] { "R26 - Project - Cao Bằng Farm    12", "R27 - Family - Pump    3", "R26 - Unknown - Sample    1" };
        picker.ItemsSource = entries;
        picker.SelectedIndex = 0;
        picker.SearchHint.Text = "Search models…";
        picker.EmptyHint.Text = "No matching models";
        picker.SearchBox.TextChanged += (s, e) => {
            picker.SearchHint.Visibility = picker.SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            // The Issues view replaces the sorted source while preserving its committed row.
            var selected = picker.SelectedItem;
            picker.ItemsSource = entries.OrderByDescending(entry =>
                entry.IndexOf(picker.SearchBox.Text, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            picker.SelectedItem = selected;
            picker.UpdateLayout();
            var matches = 0;
            foreach (var entry in entries)
            {
                var row = picker.ItemContainerGenerator.ContainerFromItem(entry) as ComboBoxItem;
                if (row == null) continue;
                var match = entry.IndexOf(picker.SearchBox.Text, StringComparison.OrdinalIgnoreCase) >= 0;
                row.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                row.IsEnabled = match;
                if (match) matches++;
            }
            picker.EmptyHint.Visibility = matches == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Model", Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(picker);
        var next = new Button { Content = "Next control", Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(next);
        var window = new Window { Content = panel, Width = 510, Height = 230, ShowInTaskbar = false, Title = "Model picker checks" };
        window.Show();
        try
        {
            Pump();
            Expect(!picker.SearchBox.IsVisible && picker.ActualHeight == 28, "closed picker has one row");
            if (snapshots != null) Save(panel, Path.Combine(snapshots, "model-picker-closed.png"));
            picker.IsDropDownOpen = true;
            Pump();
            Expect(picker.SearchBox.IsKeyboardFocused, "opening focuses the embedded editor");
            Expect(picker.SearchBox.IsVisible, "search is inside the open picker");
            picker.SearchBox.Text = "Pump";
            Pump();
            Expect(Equals(picker.SelectedItem, entries[0]), "search does not commit another model");
            if (snapshots != null)
            {
                Save(panel, Path.Combine(snapshots, "model-picker-search.png"));
                var popup = (Popup)picker.Template.FindName("PART_Popup", picker);
                Save((FrameworkElement)popup.Child, Path.Combine(snapshots, "model-picker-results.png"));
            }
            SendKey(picker.SearchBox, Key.Down);
            Pump();
            var pumpRow = (ComboBoxItem)picker.ItemContainerGenerator.ContainerFromItem(entries[1]);
            Expect(Equals(picker.PreviewedItem, entries[1]) && picker.SearchBox.IsKeyboardFocused, "Down highlights the visible row without stealing editor focus");
            var previewBorder = (Border)pumpRow.Template.FindName("Row", pumpRow);
            Expect(previewBorder.Background.ToString() == "#FFE5F2FF", "preview flag paints the actual row background");
            Expect(Equals(picker.SelectedItem, entries[0]), "arrow navigation only previews");
            SendKey(pumpRow, Key.Escape);
            Pump();
            Expect(!picker.IsDropDownOpen && Equals(picker.SelectedItem, entries[0]) && picker.SearchBox.Text == "",
                "Escape cancels and restores the committed selection");

            picker.IsDropDownOpen = true;
            Pump();
            picker.SearchBox.Text = "Pump";
            SendKey(picker.SearchBox, Key.Enter);
            Pump();
            Expect(!picker.IsDropDownOpen && Equals(picker.SelectedItem, entries[1]), "Enter commits the filtered result");

            picker.IsDropDownOpen = true;
            Pump();
            picker.SearchBox.Text = "missing";
            Pump();
            SendKey(picker.SearchBox, Key.Enter);
            Expect(picker.IsDropDownOpen && Equals(picker.SelectedItem, entries[1]), "empty results cannot change the model");
            Expect(picker.EmptyHint.IsVisible, "empty message lives in the dropdown");
            SendKey(picker.SearchBox, Key.Tab);
            Pump();
            Expect(!picker.IsDropDownOpen && picker.SearchBox.Text == "", "Tab ends the search without committing");

            picker.IsDropDownOpen = true;
            Pump();
            SendKey(picker.SearchBox, Key.Up);
            Pump();
            var last = (ComboBoxItem)picker.ItemContainerGenerator.ContainerFromItem(entries[0]);
            Expect(Equals(picker.PreviewedItem, entries[0]), "Up moves from the selected model to the previous visible result");
            SendKey(last, Key.Enter);
            Pump();
            Expect(Equals(picker.SelectedItem, entries[0]), "Enter commits the highlighted row");
            picker.IsDropDownOpen = true;
            Pump();
            picker.SearchBox.Text = "Farm";
            picker.IsDropDownOpen = false;
            Pump();
            Expect(Equals(picker.SelectedItem, entries[0]) && !picker.SearchBox.IsVisible, "closing preserves the selected model");
            picker.IsDropDownOpen = true;
            Pump();
            SendKey(picker.SearchBox, Key.Enter);
            Expect(Equals(picker.SelectedItem, entries[0]), "Enter without a query retains the current model");
            Type(picker, "P");
            Pump();
            Expect(picker.IsDropDownOpen && picker.SearchBox.Text == "P" && picker.SearchBox.IsKeyboardFocused,
                "typing on the closed picker starts a search");
            picker.SearchBox.Text = "Pump";
            picker.SearchBox.CaretIndex = picker.SearchBox.Text.Length;
            SendKey(picker.SearchBox, Key.Down);
            Pump();
            Type(picker.SearchBox, "s");
            Pump();
            Expect(picker.SearchBox.Text == "Pumps" && picker.SearchBox.IsKeyboardFocused,
                "typing after arrow navigation continues the search draft");
            picker.IsDropDownOpen = false;
            Console.WriteLine("PASS: model picker layout, focus, filtering, arrows, Enter, Escape, Tab, empty results, close and typing");
        }
        finally { window.Close(); }
    }

    private static void SendKey(UIElement target, Key key)
    {
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), 0, key) {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        });
    }

    private static void Type(UIElement target, string text)
    {
        TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, target, text));
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
    }

    private static void Save(FrameworkElement element, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
            var offset = VisualTreeHelper.GetOffset(element);
            drawing.DrawRectangle(Brushes.White, null, bounds);
            drawing.DrawRectangle(new VisualBrush(element) {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(offset.X, offset.Y, element.ActualWidth, element.ActualHeight)
            }, null, bounds);
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
