using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RvtMcp.Plugin.Views.Toast
{
    /// <summary>
    /// A counter that rolls like an odometer. Each digit is its own column, so only the digits that
    /// change move. A new value continues from where the last roll had got to instead of restarting,
    /// so a burst of results reads as one continuous roll. A value that gains a digit widens the
    /// number gradually instead of shifting its neighbours in one jump.
    /// </summary>
    internal sealed class RollingToastNumber : StackPanel
    {
        internal const double SlotHeight = 23;
        internal const double TextSize = 12;

        // One step is a single digit change; a larger jump shows only its last few steps.
        private const int MaxRollSteps = 6;
        private const double StepMilliseconds = 300;
        private const double ExtraStepMilliseconds = 55;
        private const double MaxRollMilliseconds = 620;
        private const double GrowMilliseconds = 240;

        private static readonly string[] DigitStrings = { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" };
        private static double _digitWidth;

        private readonly List<DigitColumn> _columns = new List<DigitColumn>(); // least significant first
        private Brush _foreground = Brushes.Black;
        private bool _initialized;

        internal int Value { get; private set; }

        internal int ColumnCount => _columns.Count;

        internal bool IsRolling
        {
            get
            {
                foreach (var column in _columns)
                    if (column.HasAnimatedProperties)
                        return true;
                return false;
            }
        }

        /// <summary>How far the furthest-along digit has rolled toward its next value, 0 to 1; 0 at rest.</summary>
        internal double RollProgress
        {
            get
            {
                var progress = 0.0;
                foreach (var column in _columns)
                    progress = Math.Max(progress, column.RollFraction);
                return progress;
            }
        }

        /// <summary>Where the ones digit is on its strip: 5.4 is 40% of the way from 5 to 6.</summary>
        internal double RollPosition => _columns.Count == 0 ? 0 : _columns[0].CurrentPosition;

        /// <summary>How many digit columns are moving right now.</summary>
        internal int RollingColumnCount
        {
            get
            {
                var count = 0;
                foreach (var column in _columns)
                    if (column.HasAnimatedProperties)
                        count++;
                return count;
            }
        }

        internal RollingToastNumber()
        {
            Orientation = Orientation.Horizontal;
            Height = SlotHeight;
            ClipToBounds = true;
        }

        internal void SetValue(int value, Brush foreground, bool animate)
        {
            if (value < 0)
                value = 0;
            ApplyForeground(foreground);
            if (_initialized && value == Value)
                return; // localization/colour refreshes must not replay or interrupt the roll

            var roll = animate && _initialized;
            _initialized = true;
            Value = value;
            ToolTip = value.ToString(CultureInfo.CurrentCulture);

            var digits = value.ToString(CultureInfo.InvariantCulture).Length;
            while (_columns.Count > digits)
            {
                var last = _columns.Count - 1;
                _columns[last].Stop();
                Children.Remove(_columns[last]);
                _columns.RemoveAt(last);
            }
            var firstNew = _columns.Count;
            while (_columns.Count < digits)
            {
                var column = new DigitColumn(_foreground);
                _columns.Add(column);
                Children.Insert(0, column);
                column.GrowIn(roll);
            }

            long divisor = 1;
            for (var k = 0; k < _columns.Count; k++, divisor *= 10)
            {
                var target = value / divisor;
                if (k >= firstNew)
                    _columns[k].StartOneStepBefore(target, roll);
                _columns[k].RollTo(target, roll);
            }
        }

        internal void StopAnimation()
        {
            foreach (var column in _columns)
                column.Stop();
        }

        private void ApplyForeground(Brush foreground)
        {
            if (foreground == null || ReferenceEquals(foreground, _foreground))
                return;
            _foreground = foreground;
            foreach (var column in _columns)
                column.SetForeground(foreground);
        }

        private static double DigitWidth
        {
            get
            {
                if (_digitWidth > 0)
                    return _digitWidth;
                var widest = 0.0;
                foreach (var digit in DigitStrings)
                {
                    var probe = CreateText(new TranslateTransform());
                    probe.Text = digit;
                    probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    widest = Math.Max(widest, probe.DesiredSize.Width);
                }
                _digitWidth = Math.Max(1, Math.Ceiling(widest));
                return _digitWidth;
            }
        }

        private static TextBlock CreateText(Transform shift)
        {
            var text = new TextBlock
            {
                FontFamily = McpToastTheme.UiFont,
                FontSize = TextSize,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = shift
            };
            Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
            return text;
        }

        /// <summary>
        /// One digit. <see cref="PositionProperty"/> is the column's place on the digit strip: the whole
        /// part picks the digit (modulo 10) and the fraction is how far it has rolled toward the next one.
        /// </summary>
        private sealed class DigitColumn : Grid
        {
            internal static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
                "Position", typeof(double), typeof(DigitColumn),
                new PropertyMetadata(0.0, (d, e) => ((DigitColumn)d).Render((double)e.NewValue)));

            private readonly TranslateTransform _lowerShift = new TranslateTransform();
            private readonly TranslateTransform _upperShift = new TranslateTransform();
            private readonly TextBlock _lower;
            private readonly TextBlock _upper;
            private long _shownFloor = long.MinValue;
            private long _target = long.MinValue;
            private int _rollToken;
            private int _sizeToken;

            internal DigitColumn(Brush foreground)
            {
                Width = DigitWidth;
                Height = SlotHeight;
                ClipToBounds = true;
                _lower = CreateText(_lowerShift);
                _upper = CreateText(_upperShift);
                _lower.Foreground = _upper.Foreground = foreground;
                Children.Add(_lower);
                Children.Add(_upper);
                Render(0);
            }

            private double Position => (double)GetValue(PositionProperty);

            internal double CurrentPosition => Position;

            internal double RollFraction
            {
                get
                {
                    var position = Position;
                    return position - Math.Floor(position);
                }
            }

            internal void SetForeground(Brush foreground)
            {
                _lower.Foreground = _upper.Foreground = foreground;
            }

            /// <summary>Rolls to <paramref name="target"/> from wherever the strip is right now.</summary>
            internal void RollTo(long target, bool animate)
            {
                if (target == _target)
                    return;
                _target = target;
                var token = ++_rollToken;
                if (!animate)
                {
                    Settle();
                    return;
                }

                var start = Position;
                var distance = target - start;
                if (Math.Abs(distance) > MaxRollSteps)
                    start = target - Math.Sign(distance) * MaxRollSteps;
                var steps = Math.Max(1.0, Math.Abs(target - start));
                var duration = Math.Min(MaxRollMilliseconds, StepMilliseconds + ExtraStepMilliseconds * (steps - 1));
                var roll = new DoubleAnimation(start, target, TimeSpan.FromMilliseconds(duration))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                roll.Completed += (_, __) =>
                {
                    if (token == _rollToken)
                        Settle();
                };
                BeginAnimation(PositionProperty, roll);
            }

            /// <summary>A new column has no old digit to leave, so its digit rolls in by a single step.</summary>
            internal void StartOneStepBefore(long target, bool roll)
            {
                if (roll)
                    SetValue(PositionProperty, (double)(target - 1));
            }

            /// <summary>A column added for a new digit opens up from nothing.</summary>
            internal void GrowIn(bool animate)
            {
                if (!animate)
                    return;
                var token = ++_sizeToken;
                Width = 0;
                Opacity = 0;
                var open = new DoubleAnimation(0, DigitWidth, TimeSpan.FromMilliseconds(GrowMilliseconds))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                open.Completed += (_, __) =>
                {
                    if (token == _sizeToken)
                        SettleSize();
                };
                BeginAnimation(WidthProperty, open);
                BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(GrowMilliseconds * 0.8)));
            }

            internal void Stop()
            {
                _rollToken++;
                _sizeToken++;
                SettleSize();
                if (_target != long.MinValue)
                    Settle();
            }

            private void Settle()
            {
                BeginAnimation(PositionProperty, null);
                SetValue(PositionProperty, (double)_target);
            }

            private void SettleSize()
            {
                BeginAnimation(WidthProperty, null);
                BeginAnimation(OpacityProperty, null);
                Width = DigitWidth;
                Opacity = 1;
            }

            private void Render(double position)
            {
                var floor = Math.Floor(position);
                var fraction = position - floor;
                var whole = (long)floor;
                if (whole != _shownFloor)
                {
                    _shownFloor = whole;
                    _lower.Text = DigitAt(whole);
                    _upper.Text = DigitAt(whole + 1);
                }
                // The digit rolls out through the top of the slot while the next one rolls in from
                // below, each fading as it leaves or arrives so the clipped edge never looks abrupt.
                _lowerShift.Y = -fraction * SlotHeight;
                _upperShift.Y = (1 - fraction) * SlotHeight;
                _lower.Opacity = 1 - fraction;
                _upper.Opacity = fraction;
            }

            private static string DigitAt(long n) => DigitStrings[(int)(((n % 10) + 10) % 10)];
        }
    }
}
