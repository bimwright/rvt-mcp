using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Reports.Model;
using RvtMcp.Plugin.Reports.Presentation;

namespace RvtMcp.Plugin.Reports.Views
{
    // The plugin shells also compile RvtMcp.Plugin.Reports.Binding; name the WPF type explicitly.
    using Binding = System.Windows.Data.Binding;

    /// <summary>
    /// Tooltip of a report body link: what it points at (category, name, element id, view hint), read from the
    /// stored report, and a last line that says what a click does or, while dimmed, which model it belongs to.
    /// </summary>
    internal static class ReportLinkTooltip
    {
        /// <summary>A multi-element link lists this many elements, then "+N more".</summary>
        internal const int MaxListed = 5;

        public static ToolTip Create(ReportLinkViewModel link)
        {
            var panel = new StackPanel { MaxWidth = 340 };
            var elements = link.Elements;
            if (elements.Count == 1)
            {
                panel.Children.Add(Line(Describe(elements[0]), true));
                panel.Children.Add(Line(L.T("reports.link.tip.id", ("id", Id(elements[0]))), false));
            }
            else if (elements.Count > 1)
            {
                panel.Children.Add(Line(L.T("reports.link.tip.elements", ("count", elements.Count)), true));
                foreach (var element in elements.Take(MaxListed))
                    panel.Children.Add(Line(Describe(element) + " · " + Id(element), false));
                if (elements.Count > MaxListed)
                    panel.Children.Add(Line(L.T("reports.link.tip.more", ("count", elements.Count - MaxListed)), false));
            }
            if (link.ViewHintId.HasValue)
                panel.Children.Add(Line(L.T("reports.link.tip.viewHint", ("id", link.ViewHintId.Value.ToString(CultureInfo.InvariantCulture))), false));
            if (panel.Children.Count > 0)
                panel.Children.Add(new Border { Height = 1, Background = ReportsStyles.BorderBrush, Margin = new Thickness(0, 5, 0, 4) });

            var opens = link.TargetKind == ReportLinkTargetKind.View || link.TargetKind == ReportLinkTargetKind.Sheet;
            var footer = new TextBlock { Foreground = ReportsStyles.TextSecondary, TextWrapping = TextWrapping.Wrap, Tag = "LinkTipFooter" };
            footer.SetBinding(TextBlock.TextProperty, new Binding(nameof(ReportLinkViewModel.IsLive))
            {
                Source = link,
                Converter = new FooterText(L.T(opens ? "reports.link.tip.open" : "reports.link.tip.select"),
                    L.T("reports.link.otherModel", ("model", link.DimTooltip)))
            });
            panel.Children.Add(footer);
            // Set explicitly: a link inside the RichTextBox document should not depend on implicit-style lookup to find the window's.
            return new ToolTip { Content = panel, Style = (Style)ReportsStyles.Dictionary[typeof(ToolTip)] };
        }

        private static string Describe(ReportElement element)
        {
            var parts = new[] { element?.Category, element?.Name }.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
            return parts.Length > 0 ? string.Join(" · ", parts) : L.T("reports.changes.element");
        }

        private static string Id(ReportElement element) => (element?.Id ?? 0).ToString(CultureInfo.InvariantCulture);

        private static TextBlock Line(string text, bool strong) => new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, Foreground = strong ? ReportsStyles.Text : ReportsStyles.TextSecondary,
            FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 0, 0, 1)
        };

        private sealed class FooterText : IValueConverter
        {
            private readonly string _live, _dim;
            public FooterText(string live, string dim) { _live = live; _dim = dim; }
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool live && live ? _live : _dim;
            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
        }
    }
}
