using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using RvtMcp.Failures;
using RvtMcp.Plugin;
using RvtMcp.Plugin.Failures;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Views;

internal static class HistoryRegressionChecks
{
    internal static void Run()
    {
        var failures = new List<string>();
        foreach (var check in new Action[] { CrossModelActions, DecisionGuards, CopyInputs, LocalizedModelKinds, Chrome })
        {
            InitializeLanguage();
            try { check(); Console.WriteLine("PASS: History " + check.Method.Name); }
            catch (Exception ex) { failures.Add(check.Method.Name + ": " + ex.GetBaseException().Message); }
            finally { L.ResetForTests(); }
        }
        if (failures.Count > 0) throw new Exception(string.Join("\n", failures));
    }

    private static void InitializeLanguage()
    {
        var assembly = typeof(HistoryRegressionChecks).Assembly;
        var english = EmbeddedCatalog.Load(assembly, "en");
        L.ForceSyncBuilds = true;
        L.Initialize("English", "en", locale => StringTable.Build(locale, english,
            EmbeddedCatalog.Load(assembly, locale), null), null);
    }

    private static FailureIncident Fixture()
    {
        var incident = new FailureIncident {
            ModelScopeKey = "selected-b", ModelTitle = "Selected B", RevitYear = 2027,
            OperationId = "fixture-operation", OperationContext = "edit", RequestHash = "request-b",
            DocumentRevision = "recorded-b", MessageCount = 1,
            NativeAction = "save_as", NativeContextHash = "native-b", NativeContextDescription = "Fixture destination",
            Messages = new List<FailureMessage> { new FailureMessage {
                DefinitionId = "6ce96eb8-545c-45df-a795-068c6c8c5d72", Severity = "Warning", Description = "Fixture warning" } }
        };
        FailureLedgerStore.Models = new List<FailureIssueModel> { new FailureIssueModel {
            ModelScopeKey = incident.ModelScopeKey, Title = incident.ModelTitle, RevitYear = incident.RevitYear,
            IsFamilyDocument = false, IssueCount = 1 } };
        FailureLedgerStore.Incidents = new List<FailureIncident> { incident };
        FailureLedgerStore.Operation = new FailureOperation {
            OperationId = incident.OperationId, Execution = "completed", Outcome = "rolled_back", Tool = "fixture_tool" };
        FailureControl.SelectedContext = null;
        FailureControl.LastDecision = null;
        return incident;
    }

    private static void CrossModelActions()
    {
        foreach (var context in new[] {
            new FailureIssuesContext { IsAvailable = true, ModelScopeKey = "unrelated-a", RevitYear = 2022, DocumentRevision = "unrelated-a" },
            new FailureIssuesContext(),
            new FailureIssuesContext { IsAvailable = true, ModelScopeKey = "selected-b", RevitYear = 2022, DocumentRevision = "other-year" },
            new FailureIssuesContext { IsAvailable = true, ModelScopeKey = "selected-b", RevitYear = 2027, DocumentRevision = "live-b" }
        })
        {
            Fixture();
            FailureControl.Context = context;
            WithView(view => {
                Field<CheckBox>(view, "_verified").IsChecked = true;
                foreach (var name in Actions)
                    Expect(Field<Button>(view, name).IsEnabled, name + " must allow a reviewed selected issue with context " + (context.ModelScopeKey ?? "none"));
                Expect(!view.ShellStatusText.Contains(L.T("security.failure.currentModelRequired")), "another active model must not produce an activation-required hint");
                Field<TextBox>(view, "_reason").Text = "Reviewed B";
                Field<Button>(view, "_rework").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var saved = FailureControl.LastDecision;
                Expect(saved != null && saved.ModelScopeKey == "selected-b" && saved.Action == "rework"
                    && saved.VerifiedModel && saved.Reason == "Reviewed B", "click saves the selected issue, not the active model");
                Expect(saved.DocumentRevision == (context.IsAvailable && context.ModelScopeKey == "selected-b" && context.RevitYear == 2027
                    ? "live-b" : "recorded-b"), "revision must belong to the selected model and Revit year");
            });
        }
    }

    private static readonly string[] Actions = { "_rework", "_abandon", "_accept", "_createRule", "_native" };

    private static void DecisionGuards()
    {
        var incident = Fixture();
        FailureControl.Context = new FailureIssuesContext { IsAvailable = true, ModelScopeKey = "selected-b", RevitYear = 2027, DocumentRevision = "recorded-b" };
        WithView(view => {
            Expect(Actions.All(name => !Field<Button>(view, name).IsEnabled), "review acknowledgment is required");
            Field<CheckBox>(view, "_verified").IsChecked = true;
            FailureLedgerStore.Operation.Execution = "running";
            RefreshActions(view);
            Expect(Actions.All(name => !Field<Button>(view, name).IsEnabled), "running operations cannot be decided");
            FailureLedgerStore.Operation.Execution = "completed";
            incident.Complete = false;
            RefreshActions(view);
            Expect(!Field<Button>(view, "_accept").IsEnabled && !Field<Button>(view, "_createRule").IsEnabled
                && !Field<Button>(view, "_native").IsEnabled, "incomplete capture cannot grant acceptance or native permission");
            Expect(Field<Button>(view, "_rework").IsEnabled && Field<Button>(view, "_abandon").IsEnabled, "reviewed incomplete issues may still be closed");
            incident.Complete = true;
            incident.Messages[0].Severity = "Error";
            RefreshActions(view);
            Expect(!Field<Button>(view, "_accept").IsEnabled && !Field<Button>(view, "_createRule").IsEnabled, "errors are not acceptable warnings");
            incident.Messages[0].Severity = "Warning";
            incident.RequestHash = null;
            RefreshActions(view);
            Expect(!Field<Button>(view, "_native").IsEnabled, "native permission requires its request fingerprint");
            incident.RequestHash = "request-b";
            incident.State = "resolved_rework";
            RefreshActions(view);
            Expect(Actions.All(name => !Field<Button>(view, name).IsEnabled), "resolved issues cannot receive another decision");
        });
        incident = Fixture();
        incident.State = "observed";
        incident.OperationContext = "open:ignore_unresolved_references";
        incident.Messages[0].Severity = "TaskDialog";
        incident.Messages[0].DefinitionId = "TaskDialog_Unresolved_References";
        WithView(view => {
            // Select observed records using the production state filter.
            Field<ComboBox>(view, "_states").SelectedIndex = 2;
            Field<CheckBox>(view, "_verified").IsChecked = true;
            Expect(Field<Button>(view, "_createRule").IsEnabled && !Field<Button>(view, "_accept").IsEnabled,
                "a supported observed dialog allows only a future rule, not an accept-once grant");
        });
    }

    private static void RefreshActions(FailureIssuesView view)
    {
        Expect(view.IsLoaded, "the production view must be subscribed to context changes");
        FailureControl.SetContext(FailureControl.Context);
        Pump();
    }

    private static void CopyInputs()
    {
        var window = new HistoryWindow(new McpSessionLog(), new CommandDispatcher(), new McpEventHandler(), new Autodesk.Revit.UI.ExternalEvent());
        try
        {
            foreach (var lines in new[] { 1, 16 })
            {
                var code = string.Join("\n", Enumerable.Repeat("return @\"C:\\Temp\\very_long_model_name.rvt\"; // keep_exact_value", lines));
                var input = (UIElement)Call(window, "BuildInputControl", new McpCallEntry { ToolName = "send_code_to_revit", CodeSnippet = code });
                var box = input as TextBox ?? (TextBox)((Expander)input).Content;
                if (input is Expander expander) expander.Content = null;
                var expected = string.Join("", code.Split('\n').Select((line, i) => $"{i + 1,3} | {line}{Environment.NewLine}"));
                Expect(box.Text == expected, "code rendering must not insert invisible characters");
                Expect(box.TextWrapping == TextWrapping.Wrap, "code still wraps through WPF layout");
                Expect(CopyText(box) == expected, "WPF code copy preserves the original display content");
            }
            var sql = "SELECT dbo.Some_Table FROM dbo.Model_Table WHERE path = 'C:\\Temp\\model.rvt'";
            var sqlInput = (TextBox)Call(window, "BuildInputControl", new McpCallEntry {
                ToolName = "query_fixture", ParamsJson = new JObject { ["sql"] = sql }.ToString() });
            var sqlDisplay = "sql:" + Environment.NewLine + "  SELECT dbo.Some_Table \n  FROM dbo.Model_Table \n  WHERE path = 'C:\\Temp\\model.rvt'" + Environment.NewLine;
            Expect(sqlInput.Text == sqlDisplay, "SQL formatting preserves identifiers and paths without U+200B");
            Expect(CopyText(sqlInput) == sqlDisplay, "WPF SQL copy preserves the formatted text");
            // A genuine invisible character in source data must not be stripped by a copy workaround.
            var original = "return \"intentional\u200Bvalue\";";
            var literal = (TextBox)Call(window, "BuildInputControl", new McpCallEntry { ToolName = "send_code_to_revit", CodeSnippet = original });
            Expect(CopyText(literal) == "  1 | " + original + Environment.NewLine, "copy retains characters actually present in source data");
        }
        finally { window.Close(); }
    }

    private static string CopyText(TextBox box)
    {
        string text = null;
        DataObjectCopyingEventHandler handler = (sender, args) => {
            text = args.DataObject.GetData(DataFormats.UnicodeText) as string;
            args.CancelCommand(); // Exercise native Copy without replacing the user's clipboard.
        };
        DataObject.AddCopyingHandler(box, handler);
        var host = new Window { Content = box, Width = 500, Height = 180, ShowInTaskbar = false };
        try { host.Show(); Pump(); box.Focus(); box.SelectAll(); box.Copy(); return text; }
        finally { host.Content = null; host.Close(); DataObject.RemoveCopyingHandler(box, handler); }
    }

    private static void LocalizedModelKinds()
    {
        Fixture();
        FailureLedgerStore.Models.Add(new FailureIssueModel { ModelScopeKey = "family", Title = "Original_Family_Name", RevitYear = 2027, IsFamilyDocument = true, IssueCount = 1 });
        FailureLedgerStore.Models.Add(new FailureIssueModel { ModelScopeKey = "unknown", Title = "Original_Unknown_Name", RevitYear = 0, IssueCount = 1 });
        FailureControl.Context = new FailureIssuesContext();
        WithView(view => {
            var picker = Field<ComboBox>(view, "_models");
            var selected = picker.SelectedItem;
            foreach (var sample in new[] {
                new[] { "en", "Project", "Family", "Unknown" },
                new[] { "fr", "Projet", "Famille", "Inconnu" },
                new[] { "ja", "プロジェクト", "ファミリ", "不明" }
            })
            {
                L.SetLanguage(sample[0]); Pump();
                Expect(ReferenceEquals(picker.SelectedItem, selected), "language changes preserve the selected review");
                foreach (var row in picker.Items.Cast<object>())
                {
                    var scope = (string)row.GetType().GetProperty("ModelScopeKey").GetValue(row);
                    var label = (string)row.GetType().GetProperty("Prefix").GetValue(row);
                    var kind = scope == "selected-b" ? sample[1] : scope == "family" ? sample[2] : sample[3];
                    Expect(label.Contains(" - " + kind + " - "), "model-kind label follows " + sample[0] + ": " + label);
                }
                picker.IsDropDownOpen = true; Pump();
                var rendered = string.Join("\n", TextBlocks(picker.ItemContainerGenerator.ContainerFromItem(picker.Items[0])).Select(text => text.Text));
                var firstPrefix = (string)picker.Items[0].GetType().GetProperty("Prefix").GetValue(picker.Items[0]);
                Expect(rendered.Contains(firstPrefix), "the rendered model template refreshes after a locale change");
                picker.IsDropDownOpen = false; Pump();
            }
        });
    }

    private static void Chrome()
    {
        Fixture();
        FailureLedgerStore.Models.Add(new FailureIssueModel { ModelScopeKey = "long", RevitYear = 2027,
            Title = new string('M', 180), IssueCount = 1000 });
        var history = new HistoryWindow(new McpSessionLog(), new CommandDispatcher(), new McpEventHandler(), new Autodesk.Revit.UI.ExternalEvent());
        try
        {
            history.Show(); Pump();
            var tab = Field<TabItem>(history, "_activityTab");
            Expect((string)tab.Header == "Activities", "the tab is named Activities");
            var activityHint = Field<TextBlock>(history, "_searchPrompt");
            history.ShowIssues(); Pump();
            var view = Field<FailureIssuesView>(history, "_issuesView");
            var reasonHint = Field<TextBlock>(view, "_reasonWatermark");
            Expect(reasonHint.FontStyle == FontStyles.Italic && ReferenceEquals(reasonHint.Style, activityHint.Style), "reason and Activities search share one italic dictionary style");
            var copy = Field<Button>(view, "_copyDetails");
            Expect(copy.Content is string copyGlyph && copyGlyph.Length == 1 && copy.ActualWidth <= 32, "Copy is a compact icon button");
            var expand = FindButtons(view).Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == L.T("failure.group.record"));
            Expect(expand.Content is string expandGlyph && expandGlyph.Length == 1 && expand.ActualWidth <= 32, "Expand is a compact accessible icon button");
            expand.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            Expect(Field<Grid>(view, "_facts").Visibility == Visibility.Visible && System.Windows.Automation.AutomationProperties.GetName(expand) == L.T("failure.group.collapse"), "the expansion icon updates its accessible action");
            var picker = Field<ComboBox>(view, "_models");
            var width = picker.ActualWidth;
            Expect(!double.IsNaN(picker.Width), "the model picker has an explicit fixed width");
            picker.IsDropDownOpen = true; Pump();
            var popup = (Popup)picker.Template.FindName("PART_Popup", picker);
            Expect(Math.Abs(((FrameworkElement)popup.Child).ActualWidth - width) < 1, "the dropdown has the same fixed width");
            ((FailureModelPicker)picker).SearchBox.Text = "MMMM"; Pump();
            Expect(Math.Abs(picker.ActualWidth - width) < 1, "search cannot resize the model picker");
            picker.IsDropDownOpen = false; Pump();
            picker.SelectedIndex = picker.Items.Count - 1; Pump();
            Expect(Math.Abs(picker.ActualWidth - width) < 1, "selecting a long model name cannot resize the model picker");
        }
        finally { history.Close(); }
    }

    private static IEnumerable<Button> FindButtons(DependencyObject root)
    {
        if (root is Button button) yield return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var value in FindButtons(VisualTreeHelper.GetChild(root, i))) yield return value;
    }

    private static IEnumerable<TextBlock> TextBlocks(DependencyObject root)
    {
        if (root == null) yield break;
        if (root is TextBlock text) yield return text;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in TextBlocks(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void WithView(Action<FailureIssuesView> check)
    {
        var view = new FailureIssuesView();
        var window = new Window { Content = view, Width = 1100, Height = 720, ShowInTaskbar = false };
        window.Show();
        try { Pump(); check(view); }
        finally { window.Close(); }
    }

    private static T Field<T>(object value, string field)
    {
        var info = value.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
        if (info == null && value is HistoryWindow) return Field<T>(Field<ActivityHistoryView>(value, "_activitiesView"), field);
        return (T)info.GetValue(value);
    }
    private static object Call(object value, string method, params object[] args)
    {
        var info = value.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
        if (info == null && value is HistoryWindow) return Call(Field<ActivityHistoryView>(value, "_activitiesView"), method, args);
        return info.Invoke(value, args);
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Expect(bool condition, string message) { if (!condition) throw new Exception(message); }
}
