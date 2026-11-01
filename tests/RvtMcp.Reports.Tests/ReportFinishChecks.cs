using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using RvtMcp.Plugin.Reports.Presentation;
using RvtMcp.Plugin.Reports.Views;
using RvtMcp.Plugin.Views;
using static H;

internal static class ReportFinishChecks
{
    public static void HostTitle()
    {
        using (var s = new Scenario(DisplayMode.Wide).Show())
        {
            var w = s.Window;
            Expect(w.Title == "BIMwright - Agent report" && Caption(w) == w.Title, "idle caption and taskbar title are short");
            Expect(w.CaptionArea.ToolTip == null && w.TitleText.ToolTip == null, "title details do not use a tooltip");
            Expect(System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(w.CaptionArea), "caption receives real hover input through WindowChrome");
            var runs = w.TitleText.Inlines.OfType<Run>().ToList();
            Expect(runs[0].Text == "BIM" && ReferenceEquals(runs[0].Foreground, ReportAssets.Navy)
                && runs[1].Text == "wright" && ReferenceEquals(runs[1].Foreground, ReportAssets.Green), "wordmark keeps brand colours");
            s.Select("r02");
            Expect(w.HostDetailsText.Contains("Process: 12345 - Pipe: synthetic-revit-12345"), "details identify the owning Revit, independent of report selection");
            s.Select(DemoData.LongReport);
            s.Wide.DetailScroll.ScrollToVerticalOffset(120); Pump();
            var viewTop = w.View.TranslatePoint(new Point(), w).Y;
            var offset = s.Wide.DetailScroll.VerticalOffset;
            Expect(offset > 20, "hover check starts partway through a long report");
            var focus = System.Windows.Input.Keyboard.FocusedElement;
            Hover(w, true);
            Expect(Caption(w).Contains("Agent report - PID 12345 - Pipe: synthetic-revit-12345"), "hover reveals host details on the title itself");
            Expect(w.Title == "BIMwright - Agent report" && Near(w.View.TranslatePoint(new Point(), w).Y, viewTop)
                && Near(s.Wide.DetailScroll.VerticalOffset, offset) && ReferenceEquals(System.Windows.Input.Keyboard.FocusedElement, focus),
                "inline hover changes neither header height, reading position nor focus");
            s.Runtime.SetHost(new ReportHostStatus(12345, true, "TCP:12390")); Pump();
            Expect(Caption(w).Contains("PID 12345 - TCP: 12390") && !Caption(w).Contains("synthetic-revit"), "hovered title updates to the actual transport");
            Hover(w, false);
            Expect(Caption(w) == "BIMwright - Agent report", "mouse leave restores the short title");
            s.Runtime.SetHost(new ReportHostStatus(12345, true, "TCP:9999")); Pump();
            Expect(Caption(w) == w.Title, "host updates while idle do not expand the title");
            Click(w.CompactButton); w.Width = w.MinWidth;
            s.Runtime.SetHost(new ReportHostStatus(12345, true, "Pipe:" + new string('p', 160))); Pump();
            Hover(w, true);
            Expect(Caption(w).StartsWith("BIMwright - PID 12345 - Pipe:") && Near(w.CaptionArea.ActualHeight, 32), "Compact prioritizes host information on the same row");
            var prefix = new FormattedText("BIMwright - PID 12345 - Pipe:", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(w.TitleText.FontFamily, w.TitleText.FontStyle, w.TitleText.FontWeight, w.TitleText.FontStretch), w.TitleText.FontSize,
                w.TitleText.Foreground, VisualTreeHelper.GetDpi(w.TitleText).PixelsPerDip);
            Expect(prefix.Width <= w.TitleText.ActualWidth, "PID and transport fit visibly at minimum Compact width");
            Expect(w.CloseButton.IsVisible && w.CloseButton.TranslatePoint(new Point(), w).X + w.CloseButton.ActualWidth <= w.ActualWidth,
                "long endpoint does not push controls outside Compact");
            Expect(w.TitleText.TextTrimming == TextTrimming.CharacterEllipsis && Caption(w).Contains(new string('p', 160))
                && AutomationProperties.GetHelpText(w).Contains(new string('p', 160)), "long inline details trim visually and remain available to accessibility");
            s.Runtime.SetHost(new ReportHostStatus(12345, false, "Pipe:stale")); Pump();
            Expect(Caption(w).Contains("PID 12345 - Stopped") && !Caption(w).Contains("stale"), "stopped state clears the old endpoint in the hovered title");
            w.Hide(); Pump();
            Expect(Caption(w) == "BIMwright - Agent report", "hiding the window resets the hover title");
        }
    }
    internal static void Hover(ReportsWindow window, bool entered)
    {
        window.CaptionArea.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount)
        { RoutedEvent = entered ? System.Windows.Input.Mouse.MouseEnterEvent : System.Windows.Input.Mouse.MouseLeaveEvent });
        Pump();
    }
    private static string Caption(ReportsWindow window) => string.Concat(window.TitleText.Inlines.OfType<Run>().Select(r => r.Text));

    public static void CopyFeedback()
    {
        using (var s = new Scenario(DisplayMode.Wide).Show())
        {
            Click(s.Wide.CopyButton);
            Expect((string)s.Wide.CopyButton.Content == "✓" && s.Runtime.Copied.Count == 1, "Copy confirms at the originating button");
            Expect(WaitFor(() => (string)s.Wide.CopyButton.Content == "", 3500), "Copy feedback returns to its icon");
            s.Runtime.CopyResult = false;
            Click(s.Wide.CopyButton);
            Expect((string)s.Wide.CopyButton.Content == "!" && s.ViewModel.StatusTone == ReportStatusTone.Error, "clipboard failure is visible at the button and status");
            Click(s.Window.CompactButton);
            Expect((string)s.Compact.OpenBody.CopyButton.Content == "", "a new view does not inherit stale Copy feedback");
        }
    }

    public static void Motion()
    {
        try
        {
            using (var s = new Scenario(DisplayMode.Compact).Show())
            {
                var card = Visuals<ReportCard>(s.Compact).First(c => c.IsVisible);
                var height = card.ActualHeight;
                ReportMotion.EnabledForTests = true;
                ReportMotion.FadeIn(card);
                Expect(WaitFor(() => card.Opacity < 0.999, 600), "light effects animate opacity on a visible card");
                ReportMotion.EnabledForTests = false;
                ReportMotion.FadeIn(card);
                Expect(!DependencyPropertyHelper.GetValueSource(card, UIElement.OpacityProperty).IsAnimated && card.Opacity == 1 && Near(card.ActualHeight, height),
                    "reduced motion cancels effects without changing layout height");
            }
        }
        finally { ReportMotion.EnabledForTests = null; }
    }

    public static void ModeReading()
    {
        using (var s = new Scenario(DisplayMode.Wide).Show())
        {
            s.Select(DemoData.LongReport);
            s.Wide.DetailScroll.ScrollToVerticalOffset(300);
            Pump();
            Click(s.Window.CompactButton);
            var compactScroll = InnerScroll(s.Compact.Feed);
            compactScroll.ScrollToVerticalOffset(compactScroll.VerticalOffset + 180);
            Pump();
            var compactTop = s.Compact.OpenBody.Body.TranslatePoint(new Point(), compactScroll).Y;
            Click(s.Window.WideButton);
            Expect(Near(s.Wide.DetailScroll.VerticalOffset, 300), "Wide restores its own reading offset");
            Click(s.Window.CompactButton);
            var restoredTop = s.Compact.OpenBody.Body.TranslatePoint(new Point(), InnerScroll(s.Compact.Feed)).Y;
            Expect(Near(restoredTop, compactTop), $"Compact restores its reading body position ({compactTop:0.##} -> {restoredTop:0.##})");
            Expect(s.ViewModel.SelectedReport.ReportId == DemoData.LongReport, "mode bookmarks preserve the selected report");
        }
    }
}
