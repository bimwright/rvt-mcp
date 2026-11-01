using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using RvtMcp.Plugin;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Reports.Presentation;
using RvtMcp.Plugin.Reports.Views;
using RvtMcp.Plugin.Views.Toast;
using static H;

/// <summary>Window checks: layout, modes, placement, maximize, status, empty, language, keyboard, lifecycle.</summary>
internal static class ReportsChecks
{
    /// <summary>1. Wide: 790×540 centred on the fake owner; full list; first report selected; header and changes open.</summary>
    public static void Wide()
    {
        using (var s = new Scenario(DisplayMode.Wide).Show())
        {
            var w = s.Window;
            Expect(Bounds(w, 245, 70, 790, 540), "wide default bounds centred on the owner, got " + Describe(w));
            Expect(w.MinWidth == 720 && w.MinHeight == 460 && double.IsPositiveInfinity(w.MaxWidth) && w.MaximizeButton.Visibility == Visibility.Visible,
                $"wide limits: min 720×460, no maximum, maximize button shown (got {w.MinWidth}×{w.MinHeight}, max {w.MaxWidth})");
            var view = s.Wide;
            Expect(view != null, "wide view is shown");
            var expected = s.Runtime.Reports.Count(r => r.Documents.Any(d => d.Token == DemoData.TokenA));
            Expect(s.ViewModel.Reports.Count == expected && view.List.Items.Count == expected, "list shows every report of the open model");
            Expect(ReferenceEquals(s.ViewModel.SelectedReport, s.ViewModel.Reports[0]) && ReferenceEquals(view.List.SelectedItem, s.ViewModel.Reports[0]),
                "first report is selected");
            Expect(view.SummaryText.Text == s.ViewModel.Reports[0].Summary, "header shows the summary");
            Expect(PillText(view) == "Completed", "header shows the outcome pill");
            var chip = Visuals<Border>(view).FirstOrDefault(b => (b.Tag as string) == "SelfReported");
            Expect(chip != null && Texts(chip).Contains(L.T("reports.detail.unverifiedShort")) && (chip.ToolTip as string) == L.T("reports.detail.unverified"),
                "header meta line ends with the short self-reported label; its tooltip says it is not verified");
            Expect(view.CopyButton.Content as string == "" && (view.CopyButton.ToolTip as string) == L.T("reports.copy.button")
                && System.Windows.Automation.AutomationProperties.GetName(view.CopyButton) == L.T("reports.copy.button"),
                "Copy is an icon button with a tooltip and a name");
            Expect(view.Changes.IsExpanded, "actual changes start open");
            Expect(w.Title == "BIMwright - Agent report" && w.HostDetailsText.Contains(s.Runtime.HostStatus.Display),
                "short caption keeps host details in accessibility help");
            Expect(w.WideButton.IsChecked == true && w.CompactButton.IsChecked == false, "wide mode button is pressed");
        }
    }

    /// <summary>2. Mode switch keeps selection, filter and status; each mode keeps its own bounds; one save per switch.</summary>
    public static void ModeSwitch()
    {
        using (var s = new Scenario(DisplayMode.Wide).Show())
        {
            var w = s.Window;
            w.Left = 300; w.Top = 100; w.Width = 800; w.Height = 560;
            Pump();
            Click(s.Wide.AllFilterButton);
            s.Select("r02");
            Click(s.Wide.CopyButton);
            var status = s.ViewModel.StatusText;
            Expect(status == L.T("reports.status.copied"), "copy sets a status before the switch");

            Click(w.CompactButton);
            Expect(s.ViewModel.Mode == DisplayMode.Compact && w.CurrentMode == DisplayMode.Compact && s.Compact != null, "compact button switches the mode");
            Expect(Bounds(w, 834, 36, 390, 608), "compact default bounds at the owner's right edge, got " + Describe(w));
            Expect(s.ViewModel.SelectedReport?.ReportId == "r02", "selection survives the switch");
            var card = Cards(s.Compact).FirstOrDefault(c => ReferenceEquals(c.DataContext, s.ViewModel.SelectedReport));
            Expect(card != null && card.IsOpen, "the selected report's card is open");
            Expect(s.ViewModel.Filter == ReportFilter.All && s.Compact.Filter.SelectedIndex == 1, "filter survives the switch");
            Expect(s.ViewModel.StatusText == status && w.StatusText.Text == status, "status survives the switch");
            Expect(w.CompactButton.IsChecked == true && w.WideButton.IsChecked == false && Near(w.CaptionArea.ActualHeight, 32),
                "compact chrome: button pressed, caption stays one row");
            Expect(s.Saved.Count == 1 && s.Saved[0].Mode == ReportsWindowMode.Compact && Same(s.Saved[0].Wide, 300, 100, 800, 560)
                && Same(s.Saved[0].Compact, 834, 36, 390, 608), "first switch saves the mode and both bounds once");

            Click(w.CompactButton);
            Expect(w.CurrentMode == DisplayMode.Compact && w.CompactButton.IsChecked == true && s.Saved.Count == 1,
                "pressing the active mode button changes nothing");

            Click(w.WideButton);
            Expect(w.CurrentMode == DisplayMode.Wide && s.Wide != null, "wide button switches back");
            Expect(Bounds(w, 300, 100, 800, 560), "wide comes back to its earlier bounds, got " + Describe(w));
            Expect(s.ViewModel.SelectedReport?.ReportId == "r02" && ReferenceEquals(s.Wide.List.SelectedItem, s.ViewModel.SelectedReport),
                "selection survives the way back");
            Expect(s.Saved.Count == 2 && s.Saved[1].Mode == ReportsWindowMode.Wide && Same(s.Saved[1].Wide, 300, 100, 800, 560)
                && Same(s.Saved[1].Compact, 834, 36, 390, 608), "second switch saves the mode and both bounds once");
        }
    }

    /// <summary>3. Reopen from saved state; off-screen bounds clamp to the work area; empty state uses the defaults.</summary>
    public static void Reopen()
    {
        var saved = new ReportsWindowState(ReportsWindowMode.Compact, new ReportsWindowBounds(200, 80, 820, 560), new ReportsWindowBounds(900, 50, 380, 600));
        using (var s = new Scenario(DisplayMode.Compact, saved).Show())
        {
            Expect(s.Window.CurrentMode == DisplayMode.Compact && s.Compact != null, "saved compact mode is restored");
            Expect(Bounds(s.Window, 900, 50, 380, 600), "saved compact bounds are restored, got " + Describe(s.Window));
            Click(s.Window.WideButton);
            Expect(Bounds(s.Window, 200, 80, 820, 560), "saved wide bounds are used on the first switch, got " + Describe(s.Window));
        }
        var offscreen = new ReportsWindowState(ReportsWindowMode.Wide, new ReportsWindowBounds(5000, -400, 790, 540));
        using (var s = new Scenario(DisplayMode.Wide, offscreen).Show())
            Expect(Bounds(s.Window, 490, 0, 790, 540), "off-screen bounds clamp into the work area, got " + Describe(s.Window));
        var tooBig = new ReportsWindowState(ReportsWindowMode.Wide, new ReportsWindowBounds(-50, -50, 4000, 3000));
        using (var s = new Scenario(DisplayMode.Wide, tooBig).Show())
            Expect(Bounds(s.Window, 0, 0, 1280, 680), "bounds larger than the work area shrink to it, got " + Describe(s.Window));
        using (var s = new Scenario(DisplayMode.Compact, new ReportsWindowState(ReportsWindowMode.Compact)).Show())
            Expect(Bounds(s.Window, 834, 36, 390, 608), "empty compact state docks at the owner's right edge, got " + Describe(s.Window));
        using (var s = new Scenario(DisplayMode.Wide))
        {
            s.Screen.Owner = null;
            s.Screen.Work = new ToastBounds(0, 0, 1000, 600);
            s.Show();
            Expect(Bounds(s.Window, 105, 30, 790, 540), "no owner: wide centres in the work area, got " + Describe(s.Window));
        }
    }

    /// <summary>4. Maximize: content stays inside the real work area; a mode switch restores to Normal; the old mode keeps RestoreBounds.</summary>
    public static void Maximize()
    {
        using (var s = new Scenario(DisplayMode.Wide).Show())
        {
            var w = s.Window;
            // A WM_SYSCOMMAND posted within milliseconds of the first show is sometimes dropped by Windows
            // (it never reaches the window); a person cannot click that fast, so let the new window settle.
            WaitFor(() => false, 400);
            Click(w.MaximizeButton);
            // SystemCommands posts WM_SYSCOMMAND; the state changes a little later.
            Expect(WaitFor(() => w.WindowState == WindowState.Maximized), "maximize button maximizes");
            Expect((w.MaximizeButton.ToolTip as string) == L.T("reports.window.restore"), "maximize button turns into restore");
            var work = WorkAreaPx(w);
            var topLeft = w.Frame.PointToScreen(new Point(0, 0));
            var bottomRight = w.Frame.PointToScreen(new Point(w.Frame.ActualWidth, w.Frame.ActualHeight));
            Expect(topLeft.X >= work.Left - 1 && topLeft.Y >= work.Top - 1, $"content top-left {topLeft} inside work area {work}");
            Expect(bottomRight.X <= work.Right + 1 && bottomRight.Y <= work.Bottom + 1, $"content bottom-right {bottomRight} inside work area {work}");
            Expect(w.Frame.BorderThickness.Top == 0, "no frame border while maximized");
            var restore = w.RestoreBounds;
            Click(w.CompactButton);
            Expect(w.WindowState == WindowState.Normal && w.CurrentMode == DisplayMode.Compact, "switching while maximized goes back to Normal");
            Expect(Bounds(w, 834, 36, 390, 608), "compact bounds after the switch, got " + Describe(w));
            Expect(w.MinWidth == 360 && w.MinHeight == 460 && w.MaxWidth == 560 && w.MaximizeButton.Visibility == Visibility.Collapsed,
                $"compact limits: min 360×460, max width 560, no maximize button (got {w.MinWidth}×{w.MinHeight}, max {w.MaxWidth})");
            w.WindowState = WindowState.Maximized;
            Expect(WaitFor(() => w.WindowState == WindowState.Normal) && Bounds(w, 834, 36, 390, 608),
                "compact refuses a maximize from another route (caption double-click, Win+Up) and keeps its bounds, got " + Describe(w));
            Expect(w.Frame.Margin == new Thickness(0) && w.Frame.BorderThickness.Top == 1, "frame inset removed after restore");
            var last = s.Saved.Last();
            Expect(Same(last.Wide, restore.Left, restore.Top, restore.Width, restore.Height), "wide keeps its RestoreBounds " + restore);
            Expect((w.MaximizeButton.ToolTip as string) == L.T("reports.window.maximize"), "restore button turns back into maximize");
            Click(w.MinimizeButton);
            Expect(WaitFor(() => w.WindowState == WindowState.Minimized), "minimize button minimizes");
            w.WindowState = WindowState.Normal;
            Pump();
            Expect(Bounds(w, 834, 36, 390, 608), "restoring from minimized keeps the bounds, got " + Describe(w));
        }
    }

    /// <summary>11. Status tones use the PLAN colours; an empty status shows the hint.</summary>
    public static void StatusTones()
    {
        using (var s = new Scenario(DisplayMode.Wide).Show())
        {
            var w = s.Window;
            ExpectStatus(w, L.T("reports.status.hint"), 0x64748B, 0x94A3B8, 0xF7F8FA, "hint");
            Click(s.Wide.CopyButton);
            ExpectStatus(w, L.T("reports.status.copied"), 0x00548C, 0x007ACC, 0xF7F8FA, "success");
            s.Navigation.Next = new ReportNavigationResult { Outcome = ReportNavigationOutcome.ElementsGone };
            Click(BodyLinks(s.Wide.Body).First());
            ExpectStatus(w, L.T("reports.navigation.elementsGone"), 0x92400E, 0xD97706, 0xFFFBEB, "warning");
            s.Runtime.CopyResult = false;
            Click(s.Wide.CopyButton);
            ExpectStatus(w, L.T("reports.status.copyFailed"), 0xB91C1C, 0xDC2626, 0xFEF2F2, "error");
            s.Select("r04");
            ExpectStatus(w, L.T("reports.status.hint"), 0x64748B, 0x94A3B8, 0xF7F8FA, "hint after a new selection");
            Expect(w.StatusText.TextTrimming == TextTrimming.CharacterEllipsis && (w.StatusText.ToolTip as string) == w.StatusText.Text,
                "long status trims with the full text in the tooltip");
            Expect(Near(w.StatusBar.ActualHeight, 26) && Near(w.StatusDot.Width, 7), "status bar is 26 high with a 7 px dot");
        }
    }

    /// <summary>12. No active document under "Open model" shows the empty text; "All" shows the list. Both modes.</summary>
    public static void Empty()
    {
        using (var s = new Scenario(DisplayMode.Wide, noActive: true).Show())
        {
            var view = s.Wide;
            Expect(view.EmptyTextBlock.Visibility == Visibility.Visible && view.EmptyTextBlock.Text == L.T("reports.empty.noActiveDocument"),
                "wide shows the no-active-model text");
            Expect(view.List.Visibility == Visibility.Collapsed && view.SelectHint.Visibility == Visibility.Collapsed, "wide hides the list and the hint");
            Click(view.AllFilterButton);
            Expect(view.List.Visibility == Visibility.Visible && view.List.Items.Count == s.Runtime.Reports.Count, "All shows every report");
            Expect(view.EmptyTextBlock.Visibility == Visibility.Collapsed && s.ViewModel.SelectedReport != null, "All selects the first report");
        }
        using (var s = new Scenario(DisplayMode.Compact, noActive: true).Show())
        {
            var view = s.Compact;
            Expect(view.EmptyTextBlock.Visibility == Visibility.Visible && view.EmptyTextBlock.Text == L.T("reports.empty.noActiveDocument"),
                "compact shows the no-active-model text");
            Expect(view.Feed.Visibility == Visibility.Collapsed && view.CountText.Text == L.T("reports.list.count", ("count", 0)), "compact hides the feed");
            view.Filter.SelectedIndex = 1;
            Pump();
            Expect(s.ViewModel.Filter == ReportFilter.All && view.Feed.Visibility == Visibility.Visible
                && view.CountText.Text == L.T("reports.list.count", ("count", s.Runtime.Reports.Count)), "All shows the feed and the count");
        }
    }

    /// <summary>13. fr then ja: title, filter and outcome text follow; selection, mode and filter stay.</summary>
    public static void Language()
    {
        try
        {
            using (var s = new Scenario(DisplayMode.Wide).Show())
            {
                Click(s.Wide.AllFilterButton);
                s.Select("r03");
                foreach (var code in new[] { "fr", "ja" })
                {
                    L.SetLanguage(code);
                    Pump();
                    Expect(L.Locale == code, "language " + code + " is active");
                    var view = s.Wide;
                    Expect(view != null && s.Window.CurrentMode == DisplayMode.Wide, code + ": mode stays wide");
                    Expect(s.Window.Title == "BIMwright - " + L.T("reports.window.title")
                        && s.Window.HostDetailsText.Contains(s.Runtime.HostStatus.Display), code + ": short title localizes and tooltip retains owner identity");
                    Expect((view.ActiveFilterButton.Content as string) == L.T("reports.filter.activeModel")
                        && (view.AllFilterButton.Content as string) == L.T("reports.filter.all") && L.T("reports.filter.all") != "All models",
                        code + ": filter buttons follow the language");
                    Expect(PillText(view) == L.T("reports.outcome.cancelled") && PillText(view) != "Cancelled", code + ": outcome pill follows the language");
                    Expect(s.ViewModel.SelectedReport?.ReportId == "r03" && ReferenceEquals(view.List.SelectedItem, s.ViewModel.SelectedReport),
                        code + ": selection stays");
                    Expect(s.ViewModel.Filter == ReportFilter.All && view.AllFilterButton.IsChecked == true, code + ": filter stays");
                    Expect(s.Window.StatusText.Text == L.T("reports.status.hint"), code + ": hint follows the language");
                }
                Click(s.Window.CompactButton);
                L.SetLanguage("fr");
                Pump();
                var compact = s.Compact;
                Expect(compact != null && (compact.Filter.Items[1] as string) == L.T("reports.filter.all") && compact.Filter.SelectedIndex == 1,
                    "compact filter follows the language and stays on All");
                var card = Cards(compact).FirstOrDefault(c => ReferenceEquals(c.DataContext, s.ViewModel.SelectedReport));
                Expect(card != null && card.IsOpen, "compact card stays open across a language change");
            }
        }
        finally
        {
            L.SetLanguage("en");
            Pump();
        }
    }

    /// <summary>15. Tab reaches the mode buttons, filter and list; arrows move the wide selection; Enter toggles a compact card.</summary>
    public static void Keyboard()
    {
        using (var s = new Scenario(DisplayMode.Wide).Show())
        {
            var w = s.Window;
            w.Activate();
            Pump();
            Expect(w.WideButton.Focus(), "the window takes keyboard focus");
            var reached = new List<object>();
            for (var i = 0; i < 60; i++)
            {
                var focused = System.Windows.Input.Keyboard.FocusedElement as UIElement;
                if (focused == null) break;
                reached.Add(focused);
                focused.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                Pump();
            }
            var view = s.Wide;
            Expect(reached.Contains(w.WideButton) && reached.Contains(w.CompactButton), "Tab reaches both mode buttons");
            Expect(reached.Contains(view.ActiveFilterButton) && reached.Contains(view.AllFilterButton), "Tab reaches the filter");
            Expect(reached.OfType<ListBoxItem>().Any(item => ItemsControl.ItemsControlFromItemContainer(item) == view.List), "Tab reaches the list");
            Expect(reached.Contains(view.CopyButton) && reached.Contains(view.DeleteButton), "Tab reaches Copy and Delete");

            var first = (ListBoxItem)view.List.ItemContainerGenerator.ContainerFromItem(s.ViewModel.Reports[0]);
            Expect(first.Focus(), "the selected row takes focus");
            Key(first, System.Windows.Input.Key.Down);
            Expect(ReferenceEquals(s.ViewModel.SelectedReport, s.ViewModel.Reports[1]), "Down selects the next report");
            Key((UIElement)System.Windows.Input.Keyboard.FocusedElement, System.Windows.Input.Key.Up);
            Expect(ReferenceEquals(s.ViewModel.SelectedReport, s.ViewModel.Reports[0]), "Up selects the previous report");
        }
        using (var s = new Scenario(DisplayMode.Compact).Show())
        {
            var card = Cards(s.Compact).First(c => ReferenceEquals(c.DataContext, s.ViewModel.Reports[1]));
            Expect(!card.IsOpen && card.Header.Focusable, "compact card starts closed and its header is focusable");
            Key(card.Header, System.Windows.Input.Key.Enter);
            Expect(card.IsOpen && ReferenceEquals(s.ViewModel.SelectedReport, s.ViewModel.Reports[1]), "Enter opens the card");
            Key(card.Header, System.Windows.Input.Key.Enter);
            Expect(!card.IsOpen && s.ViewModel.SelectedReport == null, "Enter again closes it");
        }
    }

    /// <summary>
    /// 19. Text size: the "Aa" card steps 90–150 % and resets; Ctrl+Plus/Minus/0 and Ctrl+wheel do the same. Only the
    /// content area scales; every change is saved, a mode switch keeps it, and a saved size is applied on open.
    /// </summary>
    public static void TextSize()
    {
        using (var s = new Scenario(DisplayMode.Wide).Show())
        {
            var w = s.Window;
            Expect(w.ContentScale == 1 && w.TextSizeValue.Text == L.T("reports.textSize.value", ("percent", 100)) && !w.ResetTextSizeButton.IsEnabled,
                "starts at 100 % with Reset disabled");
            Expect((w.TextSizeButton.ToolTip as string) == L.T("reports.textSize.tooltip"), "Aa names the shortcuts in its tooltip");
            Click(w.TextSizeButton);
            Expect(WaitFor(() => w.TextSizePopup.IsOpen), "Aa opens the text size card");
            Click(w.LargerButton);
            Expect(Math.Abs(w.ContentScale - 1.1) < 0.001 && s.Saved.Last().TextScale == 110 && w.TextSizeValue.Text == L.T("reports.textSize.value", ("percent", 110)),
                "Larger: 110 %, applied and saved");
            Pump();
            Rect Rendered(FrameworkElement e) => e.TransformToAncestor(w.Frame).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
            Expect(Math.Abs(Rendered(s.Wide).Width - s.Wide.ActualWidth * 1.1) < 1 && Math.Abs(Rendered(w.StatusBar).Height - w.StatusBar.ActualHeight) < 0.5,
                "the content area scales and the status bar does not");
            Click(w.LargerButton);
            Click(w.LargerButton);
            Click(w.LargerButton);
            Expect(w.State.TextScale == 150 && !w.LargerButton.IsEnabled, "Larger stops at 150 %");
            Click(w.ResetTextSizeButton);
            Expect(w.ContentScale == 1 && s.Saved.Last().TextScale == 100 && !w.ResetTextSizeButton.IsEnabled, "Reset goes back to 100 %");
            w.TextSizePopup.IsOpen = false;
            Pump();

            Expect(w.TextSizeKey(System.Windows.Input.Key.OemMinus, ModifierKeys.Control) && w.State.TextScale == 90 && !w.SmallerButton.IsEnabled, "Ctrl+Minus: 90 %");
            var saves = s.Saved.Count;
            Expect(w.TextSizeKey(System.Windows.Input.Key.Subtract, ModifierKeys.Control) && w.State.TextScale == 90 && s.Saved.Count == saves,
                "at 90 % Ctrl+Minus changes and saves nothing");
            Expect(w.TextSizeKey(System.Windows.Input.Key.Add, ModifierKeys.Control | ModifierKeys.Shift) && w.TextSizeKey(System.Windows.Input.Key.OemPlus, ModifierKeys.Control) && w.State.TextScale == 110,
                "Ctrl+Plus on the number pad and on the main row (Shift allowed)");
            Expect(w.TextSizeKey(System.Windows.Input.Key.NumPad0, ModifierKeys.Control) && w.State.TextScale == 100, "Ctrl+0 resets");
            Expect(!w.TextSizeKey(System.Windows.Input.Key.OemPlus, ModifierKeys.None) && !w.TextSizeKey(System.Windows.Input.Key.OemPlus, ModifierKeys.Control | ModifierKeys.Alt)
                && !w.TextSizeKey(System.Windows.Input.Key.A, ModifierKeys.Control) && w.State.TextScale == 100, "other keys and modifiers do nothing");
            Expect(w.TextSizeWheel(120, ModifierKeys.Control) && w.State.TextScale == 110 && w.TextSizeWheel(-120, ModifierKeys.Control) && w.State.TextScale == 100,
                "Ctrl+wheel steps up and down");
            Expect(!w.TextSizeWheel(120, ModifierKeys.None) && w.State.TextScale == 100, "a plain wheel scrolls and does not zoom");

            w.TextSizeKey(System.Windows.Input.Key.OemPlus, ModifierKeys.Control);
            Click(w.CompactButton);
            Expect(Math.Abs(w.ContentScale - 1.1) < 0.001 && s.Saved.Last().TextScale == 110 && s.Saved.Last().Mode == ReportsWindowMode.Compact,
                "a mode switch keeps the text size");
        }
        using (var s = new Scenario(DisplayMode.Wide, new ReportsWindowState(ReportsWindowMode.Wide, textScale: 125)).Show())
        {
            Expect(Math.Abs(s.Window.ContentScale - 1.25) < 0.001 && s.Window.TextSizeValue.Text == L.T("reports.textSize.value", ("percent", 125)),
                "a saved 125 % is applied on open");
        }
    }

    /// <summary>17. Hide/show three times, real close on shutdown, no handlers left behind.</summary>
    public static void Lifecycle()
    {
        var languageBaseline = LanguageHandlers();
        var s = new Scenario(DisplayMode.Wide).Show();
        var vm = s.ViewModel;
        try
        {
            var w = s.Window;
            for (var i = 1; i <= 3; i++)
            {
                var loads = s.Runtime.LoadCount;
                if (i == 1)
                {
                    // The close button posts SC_CLOSE, the same path as Alt+F4; let the shown window settle first (see Maximize).
                    WaitFor(() => false, 400);
                    Click(w.CloseButton);
                    Expect(WaitFor(() => !w.IsVisible), "close button hides the window");
                }
                else w.Close();
                Pump();
                Expect(!w.IsVisible && new WindowInteropHelper(w).Handle != IntPtr.Zero, "close " + i + " hides without destroying");
                Expect(s.Saved.Count == i, "close " + i + " saves the state");
                w.Show();
                s.WaitForRefresh();
                Expect(w.IsVisible && s.Runtime.LoadCount > loads, "show " + i + " reloads the reports");
                Expect(Bounds(w, 245, 70, 790, 540) && s.Wide != null, "show " + i + " keeps bounds and view");
            }
            var settled = ViewModelHandlers(vm);
            for (var i = 0; i < 3; i++)
            {
                Click(w.CompactButton);
                Click(w.WideButton);
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Pump();
            Expect(ViewModelHandlers(vm) == settled, $"mode switches leave no view handlers ({ViewModelHandlers(vm)} vs {settled})");
            Expect(LanguageHandlers() == languageBaseline + 1, "one language handler while open");

            w.CloseForShutdown();
            Pump();
            Expect(!w.IsVisible && new WindowInteropHelper(w).Handle == IntPtr.Zero, "CloseForShutdown closes for real");
            Expect(s.Saved.Count == 3 + 6 + 1, "shutdown saves once more");
            Expect(ViewModelHandlers(vm) == 0, $"no PropertyChanged handlers after Dispose ({ViewModelHandlers(vm)})");
            Expect(LanguageHandlers() == languageBaseline, "no L.Changed handler after Dispose");
            w.Dispose();
            w.CloseForShutdown();
        }
        finally
        {
            s.Dispose();
        }
    }

    internal static IEnumerable<ReportCard> Cards(ReportsCompactView view) => Visuals<ReportCard>(view.Feed);

    internal static string PillText(DependencyObject root)
    {
        var pill = Visuals<Border>(root).FirstOrDefault(b => (b.Tag as string) == "OutcomePill");
        return pill == null ? null : Visuals<TextBlock>(pill).Select(t => t.Text).FirstOrDefault();
    }

    internal static List<string> Texts(DependencyObject root) => Visuals<TextBlock>(root).Select(t => t.Text).ToList();

    internal static IEnumerable<System.Windows.Documents.Hyperlink> BodyLinks(ReportBodyView body) =>
        Elements(body.Document).OfType<System.Windows.Documents.Hyperlink>();

    internal static void Key(UIElement target, Key key)
    {
        target.RaiseEvent(new KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), 0, key)
            { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
        Pump();
    }

    private static void ExpectStatus(ReportsWindow w, string text, uint fore, uint dot, uint back, string tone)
    {
        Expect(w.StatusText.Text == text, tone + ": status text '" + w.StatusText.Text + "'");
        Expect(ColorOf(w.StatusText.Foreground) == Hex(fore) && ColorOf(w.StatusDot.Fill) == Hex(dot) && ColorOf(w.StatusBar.Background) == Hex(back),
            $"{tone}: colours {ColorOf(w.StatusText.Foreground)}/{ColorOf(w.StatusDot.Fill)}/{ColorOf(w.StatusBar.Background)}");
    }
}
