using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using RvtMcp.Plugin.Views;

// Load the production row and templates, without constructing the view or calling Revit/ledger code.
internal static class FailureModelPickerDiagnostics
{
    internal static void Run(string pluginPath, string output, bool demo)
    {
        Directory.CreateDirectory(output);
        var assembly = Assembly.LoadFrom(Path.GetFullPath(pluginPath));
        var view = assembly.GetType("RvtMcp.Plugin.Views.FailureIssuesView", true);
        var rowType = typeof(RvtMcp.Plugin.Presentation.Issues.ModelRow);
        var modelType = assembly.GetType("RvtMcp.Failures.FailureIssueModel", true);
        var contextType = assembly.GetType("RvtMcp.Plugin.Views.FailureIssuesContext", true);
        object Context(string scope)
        {
            var context = Activator.CreateInstance(contextType, true);
            Set(context, "IsAvailable", true); Set(context, "ModelScopeKey", scope); Set(context, "RevitYear", 2026);
            return context;
        }
        var context = Context("farm");
        var open = new HashSet<string> { "farm", "pump" };
        object Row(string scope, string title, int year, int count)
        {
            var model = Activator.CreateInstance(modelType);
            Set(model, "ModelScopeKey", scope); Set(model, "Title", title);
            Set(model, "RevitYear", year); Set(model, "IssueCount", count); Set(model, "IsFamilyDocument", false);
            return Activator.CreateInstance(rowType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { model, context, open }, null);
        }
        var rows = new[] { Row("farm", "Cao Bằng Farm", 2026, 12), Row("pump", "Pump", 2026, 3),
            Row("farm", "Cao Bằng Farm - old year", 2027, 5), Row("closed", "Closed model", 2026, 1) };
        var picker = (ComboBox)Activator.CreateInstance(assembly.GetType("RvtMcp.Plugin.Views.FailureModelPicker", true), true);
        picker.Height = 28; picker.MaxDropDownHeight = 180; picker.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var searchBox = (TextBox)picker.GetType().GetProperty("SearchBox", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(picker);
        var searchHint = (TextBlock)picker.GetType().GetProperty("SearchHint", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(picker);
        var emptyHint = (TextBlock)picker.GetType().GetProperty("EmptyHint", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(picker);
        picker.ItemTemplate = (DataTemplate)view.GetMethod("ModelTemplate", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        picker.ItemContainerStyle = (Style)view.GetMethod("ModelSearchStyle", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        picker.ItemsSource = rows;
        picker.SelectedItem = rows[1];
        searchHint.Text = "Search models…";
        emptyHint.Text = "No matching models";
        void Filter()
        {
            foreach (var row in rows) Call(row, "UpdateSearch", searchBox.Text);
            var selected = picker.SelectedItem;
            picker.ItemsSource = rows.OrderByDescending(row => (double)Get(row, "SearchScore"))
                .ThenByDescending(row => (bool)Get(row, "IsOpen")).ThenByDescending(row => (bool)Get(row, "IsCurrent")).ToArray();
            picker.SelectedItem = selected;
            emptyHint.Visibility = rows.Any(row => (bool)Get(row, "MatchesSearch")) ? Visibility.Collapsed : Visibility.Visible;
            searchHint.Visibility = searchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        searchBox.TextChanged += (s, e) => Filter();
        picker.DropDownOpened += (s, e) => Filter();
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Model — production row/template", Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(picker);
        var switchActive = new Button { Content = "Switch active model", Margin = new Thickness(0, 12, 0, 0) };
        switchActive.Click += (s, e) => {
            context = Context((string)Get(context, "ModelScopeKey") == "farm" ? "pump" : "farm");
            foreach (var row in rows) Call(row, "UpdateContext", context, open);
            Filter();
        };
        panel.Children.Add(switchActive);
        var status = new TextBlock { Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(status);
        var window = new Window { Content = panel, Title = "Issues model picker diagnostics", Width = 640, Height = 300 };
        var log = new List<string>();
        var failures = new List<string>();
        void Check(bool condition, string message) { if (!condition) failures.Add(message); }
        bool Preview(object row)
        {
            var container = picker.ItemContainerGenerator.ContainerFromItem(row) as ComboBoxItem;
            return container != null && (bool)(container.GetType().GetProperty("IsPreviewed")?.GetValue(container) ?? container.IsHighlighted);
        }
        void Record(string label)
        {
            var containers = picker.Items.Cast<object>().Select(item => picker.ItemContainerGenerator.ContainerFromItem(item) as ComboBoxItem)
                .Where(item => item != null).ToArray();
            var text = label + " open=" + picker.IsDropDownOpen + " editorFocus=" + searchBox.IsKeyboardFocused
                + " selected=" + (picker.SelectedItem == null ? "null" : Get(picker.SelectedItem, "Prefix"))
                + "\n" + string.Join("\n", containers.Select(item => Get(item.Content, "Prefix")
                    + ": bold=" + Get(item.Content, "IsOpen") + " preview=" + Preview(item.Content)
                    + " nativeHighlight=" + item.IsHighlighted + " focus=" + item.IsKeyboardFocusWithin));
            log.Add(text); status.Text = text; File.WriteAllLines(Path.Combine(output, "trace.txt"), log);
        }
        if (demo)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            timer.Tick += (s, e) => Record("UI");
            window.Closed += (s, e) => timer.Stop();
            timer.Start();
            new Application().Run(window);
            return;
        }
        window.Show();
        try
        {
            Pump();
            picker.IsDropDownOpen = true; Pump(); Record("opened");
            Check(!(bool)Get(rows[2], "IsOpen"), "different Revit year must not appear open/bold");
            Key(searchBox, System.Windows.Input.Key.Down); Pump(); Record("down");
            Check(Preview(rows[2]) && !Preview(rows[1]), "Down must move the single highlight to the next row");
            picker.IsDropDownOpen = false; Pump();
            // Opening and navigating before the queued focus callback is a real rapid-input sequence.
            picker.IsDropDownOpen = true;
            picker.UpdateLayout();
            Key(searchBox, System.Windows.Input.Key.Down);
            Record("rapid before dispatcher"); Pump(); Record("rapid after dispatcher");
            Check(Preview(rows[2]) && !Preview(rows[1]), "queued opening work must not reset rapid arrow navigation");
            Check(searchBox.IsKeyboardFocused, "native deferred opening focus must not displace the search editor");
            picker.IsDropDownOpen = false; Pump();
            picker.IsDropDownOpen = true; Pump();
            searchBox.Text = "closed"; Pump(); Record("filter");
            Check(Preview(rows[3]), "filtered first result must be highlighted");
            picker.IsDropDownOpen = false; Pump(); Record("closed");
            foreach (var row in rows) Call(row, "UpdateContext", Context("pump"), open);
            picker.IsDropDownOpen = true; Pump(); Record("active switched");
            Check((bool)Get(rows[1], "IsCurrent") && !(bool)Get(rows[0], "IsCurrent"),
                "active-model priority must follow the context");
            Check(rows.All(row => !((string)Get(row, "Prefix")).Contains("●")),
                "model labels must not contain the removed active-model marker");
            Console.WriteLine(string.Join("\n", log));
            if (failures.Count > 0) throw new Exception("FAIL: " + string.Join("; ", failures));
        }
        finally { window.Close(); }
    }
    private static void Set(object value, string name, object content) => value.GetType().GetProperty(name).SetValue(value, content);
    private static object Get(object value, string name) => value.GetType().GetProperty(name).GetValue(value);
    private static void Call(object value, string method, params object[] args) =>
        value.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(value, args);
    private static void Key(UIElement target, Key key) => target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
        PresentationSource.FromVisual(target), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
