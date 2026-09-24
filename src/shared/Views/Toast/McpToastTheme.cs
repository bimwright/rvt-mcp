using System;
using System.Windows;
using System.Windows.Media;

namespace RvtMcp.Plugin.Views.Toast
{
    /// <summary>
    /// Light toast palette — shared design tokens, self-contained.
    /// </summary>
    internal static class McpToastTheme
    {
        public static readonly FontFamily UiFont = new FontFamily("Segoe UI, Noto Sans, Arial");
        public static readonly FontFamily IconFont = new FontFamily("Segoe MDL2 Assets");

        public static readonly SolidColorBrush Background = Brush("#F5F5F5");
        public static readonly SolidColorBrush Text = Brush("#1E293B");
        public static readonly SolidColorBrush TextSecondary = Brush("#64748B");
        public static readonly SolidColorBrush CloseIcon = Brush("#64748B");
        public static readonly SolidColorBrush CloseHover = Brush("#F0F4F8");
        public static readonly SolidColorBrush Primary = Brush("#007ACC");
        public static readonly SolidColorBrush Success = Brush("#38A169");
        public static readonly SolidColorBrush Error = Brush("#E53E3E");
        public static readonly SolidColorBrush MutedAccent = Brush("#94A3B8");
        // Brand wordmark colours come from the logo: navy "BIM" + green "wright".
        public static readonly SolidColorBrush BrandBim = Brush("#0C3F76");
        public static readonly SolidColorBrush BrandWright = Brush("#589039");
        // Lighter tints (brand blended 45% toward white) for the glint band that
        // sweeps through the wordmark on the brand reveal.
        public static readonly SolidColorBrush BrandBimShine = Frozen(Lighten(BrandBim.Color, 0.45));
        public static readonly SolidColorBrush BrandWrightShine = Frozen(Lighten(BrandWright.Color, 0.45));

        public static Brush BuildAccentBrush(McpToastViewModel vm)
        {
            Color baseColor;
            if (!vm.Success)
                baseColor = ((SolidColorBrush)Error).Color;
            else if (vm.Kind == ToolActivityKind.Write)
                baseColor = ((SolidColorBrush)Success).Color;
            else
                baseColor = ((SolidColorBrush)Primary).Color;

            return BuildAccentGradient(baseColor);
        }

        public static Brush BuildIconBrush(McpToastViewModel vm)
        {
            if (!vm.Success)
                return Error;
            return vm.Kind == ToolActivityKind.Write ? Success : Primary;
        }

        public static string GetIconGlyph(McpToastViewModel vm)
        {
            return vm.Success ? "\uE73E" : "\uE783";
        }

        private static Brush BuildAccentGradient(Color baseColor)
        {
            var topColor = Lighten(baseColor, 0.28);
            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                MappingMode = BrushMappingMode.RelativeToBoundingBox
            };
            gradient.GradientStops.Add(new GradientStop(topColor, 0));
            gradient.GradientStops.Add(new GradientStop(baseColor, 1));
            gradient.Freeze();
            return gradient;
        }

        private static Color Lighten(Color color, double amount)
        {
            amount = Math.Max(0, Math.Min(1, amount));
            return Color.FromArgb(
                color.A,
                (byte)(color.R + (255 - color.R) * amount),
                (byte)(color.G + (255 - color.G) * amount),
                (byte)(color.B + (255 - color.B) * amount));
        }

        private static SolidColorBrush Brush(string hex)
        {
            return Frozen((Color)ColorConverter.ConvertFromString(hex));
        }

        private static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
