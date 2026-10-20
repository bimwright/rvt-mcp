using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RvtMcp.Plugin.Localization;

namespace RvtMcp.Plugin.Views
{
    /// <summary>
    /// Owned, scrollable review shown before a decision that permits a later attempt.
    /// Every line of the locked constraint text is shown; the layout only splits "Label: value" lines into two columns.
    /// </summary>
    internal sealed class FailureDecisionReviewWindow : Window
    {
        private static readonly Brush Text = Brush("#1E293B"), Secondary = Brush("#64748B"), Line = Brush("#E2E8F0"),
            Page = Brush("#F5F7FA"), Card = Brushes.White, WarningBack = Brush("#FFF7E6"), WarningLine = Brush("#F0B44C"), WarningText = Brush("#7A4B00");

        internal FailureDecisionReviewWindow(Window owner, string action, string warning, string constraintsHeading, string constraints, string details, string reason)
        {
            Title = L.T("security.failure.confirmTitle");
            Owner = owner; ShowInTaskbar = false;
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
            Width = 660; MinWidth = 480; MinHeight = 320;
            SizeToContent = SizeToContent.Height;
            MaxHeight = SystemParameters.WorkArea.Height * 0.9; MaxWidth = SystemParameters.WorkArea.Width;
            FontFamily = new FontFamily("Segoe UI"); FontSize = 12.5; Foreground = Text; Background = Page;
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            Resources.MergedDictionaries.Add(BimwrightStyles.Dictionary);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new Border { Background = Card, BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(20, 16, 20, 14) };
            header.Child = new TextBlock { Text = action, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            root.Children.Add(header);

            var body = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
            if (!string.IsNullOrWhiteSpace(warning))
                body.Children.Add(new Border {
                    Background = WarningBack, BorderBrush = WarningLine, BorderThickness = new Thickness(3, 0, 0, 0),
                    CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 16),
                    Child = new TextBlock { Text = warning.Trim(), TextWrapping = TextWrapping.Wrap, Foreground = WarningText }
                });
            var blocks = Blocks(constraints);
            if (blocks.Count > 0)
            {
                body.Children.Add(Heading(constraintsHeading));
                foreach (var block in blocks) body.Children.Add(CardFor(FactGrid(block)));
            }
            body.Children.Add(Heading(L.T("security.failure.review.reason")));
            body.Children.Add(CardFor(Selectable(string.IsNullOrWhiteSpace(reason) ? "—" : reason, false)));
            if (!string.IsNullOrWhiteSpace(details))
            {
                var detailText = Selectable(details.Trim(), true);
                detailText.MaxHeight = 220; detailText.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                body.Children.Add(new Expander {
                    Header = new TextBlock { Text = L.T("security.failure.review.details"), FontWeight = FontWeights.SemiBold, Foreground = Secondary },
                    Margin = new Thickness(0, 4, 0, 0), Content = CardFor(detailText)
                });
            }
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body };
            Grid.SetRow(scroll, 1); root.Children.Add(scroll);

            var footer = new Grid { Margin = new Thickness(20, 12, 20, 14) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.Children.Add(new TextBlock {
                Text = L.T("security.failure.review.note"), TextWrapping = TextWrapping.Wrap, Foreground = Secondary,
                FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0)
            });
            // Both buttons share one width: the wider label decides, never narrower than 110.
            var buttons = new Grid { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetIsSharedSizeScope(buttons, true);
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "DecisionButton" });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "DecisionButton" });
            var cancel = new Button { Content = L.T("security.failure.review.cancel"), IsCancel = true, MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
            var approve = new Button { Content = action, MinWidth = 110, Style = (Style)BimwrightStyles.Dictionary[BimwrightStyles.PrimaryButtonKey] };
            approve.Click += (s, e) => DialogResult = true;
            Grid.SetColumn(approve, 1);
            buttons.Children.Add(cancel); buttons.Children.Add(approve);
            Grid.SetColumn(buttons, 1); footer.Children.Add(buttons);
            var footerHost = new Border { Background = Card, BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = footer };
            Grid.SetRow(footerHost, 2); root.Children.Add(footerHost);

            Content = root;
            // Approval is never the default key; focus starts on Cancel.
            Loaded += (s, e) => cancel.Focus();
        }

        // Constraint text holds one block per rule, separated by blank lines.
        private static List<List<string>> Blocks(string text)
        {
            return (text ?? "").Replace("\r\n", "\n").Split(new[] { "\n\n" }, System.StringSplitOptions.RemoveEmptyEntries)
                .Select(b => b.Split('\n').Where(l => l.Trim().Length > 0).ToList()).Where(b => b.Count > 0).ToList();
        }

        private static Grid FactGrid(List<string> lines)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto), MaxWidth = 230 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < lines.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var line = lines[i];
                var split = line.IndexOfAny(new[] { ':', '：' });
                var margin = new Thickness(0, i == 0 ? 0 : 6, 0, 0);
                if (split <= 0)
                {
                    var whole = Selectable(line.Trim(), false); whole.Margin = margin;
                    Grid.SetRow(whole, i); Grid.SetColumnSpan(whole, 2); grid.Children.Add(whole); continue;
                }
                var label = new TextBlock { Text = line.Substring(0, split).Trim(), Foreground = Secondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, margin.Top, 16, 0) };
                var value = Selectable(line.Substring(split + 1).Trim(), false); value.Margin = margin;
                Grid.SetRow(label, i); Grid.SetRow(value, i); Grid.SetColumn(value, 1);
                grid.Children.Add(label); grid.Children.Add(value);
            }
            return grid;
        }

        private static TextBlock Heading(string text) => new TextBlock {
            Text = text, FontWeight = FontWeights.SemiBold, Foreground = Secondary, Margin = new Thickness(0, 0, 0, 6)
        };

        private static Border CardFor(UIElement child) => new Border {
            Background = Card, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 14), Child = child
        };

        // Read-only text the user can select and copy, such as fingerprints and element IDs.
        private static TextBox Selectable(string text, bool monospace) => new TextBox {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Foreground = Text, Padding = new Thickness(0),
            FontFamily = new FontFamily(monospace ? "Consolas" : "Segoe UI"), FontSize = monospace ? 11.5 : 12.5
        };

        private static SolidColorBrush Brush(string hex)
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex); brush.Freeze(); return brush;
        }
    }
}
