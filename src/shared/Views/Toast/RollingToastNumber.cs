using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RvtMcp.Plugin.Views.Toast
{
    /// <summary>A fixed-size counter slot. New values replace in-flight rolls rather than queueing them.</summary>
    internal sealed class RollingToastNumber : Grid
    {
        internal const double SlotHeight = 23;
        private readonly TextBlock _outgoingText;
        private readonly TextBlock _currentText;
        private readonly Viewbox _outgoing;
        private readonly Viewbox _current;
        private readonly TranslateTransform _outgoingOffset = new TranslateTransform();
        private readonly TranslateTransform _currentOffset = new TranslateTransform();
        private bool _initialized;

        internal int Value { get; private set; }

        internal RollingToastNumber()
        {
            Width = 25;
            Height = SlotHeight;
            ClipToBounds = true;
            _outgoingText = CreateText();
            _currentText = CreateText();
            _outgoing = CreateSlot(_outgoingText, _outgoingOffset);
            _current = CreateSlot(_currentText, _currentOffset);
            _outgoing.Visibility = Visibility.Hidden;
            Children.Add(_outgoing);
            Children.Add(_current);
        }

        internal void SetValue(int value, Brush foreground, bool animate)
        {
            _outgoingText.Foreground = _currentText.Foreground = foreground;
            if (_initialized && value == Value)
                return; // localization/colour refreshes must not replay or interrupt the roll

            StopAnimation();
            _outgoingText.Text = Value.ToString(CultureInfo.CurrentCulture);
            _currentText.Text = value.ToString(CultureInfo.CurrentCulture);
            ToolTip = _currentText.Text;
            var shouldRoll = animate && _initialized;
            _initialized = true;
            Value = value;
            if (!shouldRoll)
                return;

            _outgoing.Visibility = Visibility.Visible;
            var duration = TimeSpan.FromMilliseconds(280);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _outgoingOffset.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, -SlotHeight, duration) { EasingFunction = ease });
            _currentOffset.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(SlotHeight, 0, duration) { EasingFunction = ease });
        }

        internal void StopAnimation()
        {
            _outgoingOffset.BeginAnimation(TranslateTransform.YProperty, null);
            _currentOffset.BeginAnimation(TranslateTransform.YProperty, null);
            _outgoing.Visibility = Visibility.Hidden;
            _currentOffset.Y = 0;
        }

        private static TextBlock CreateText()
        {
            var text = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold };
            Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
            return text;
        }

        private static Viewbox CreateSlot(TextBlock text, TranslateTransform offset) => new Viewbox
        {
            Child = text,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = offset
        };
    }
}
