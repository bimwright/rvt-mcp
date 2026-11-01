using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace RvtMcp.Plugin.Reports.Views
{
    internal static class ReportMotion
    {
        internal static bool? EnabledForTests { get; set; }
        internal static bool Enabled => EnabledForTests ?? SystemParameters.ClientAreaAnimation;
        internal static void FadeIn(UIElement element)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            if (!Enabled || !element.IsVisible) return;
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.65, 1, TimeSpan.FromMilliseconds(140))
                { FillBehavior = FillBehavior.Stop, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }
    }
}
