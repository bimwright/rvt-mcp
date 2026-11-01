using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RvtMcp.Plugin.Reports.Presentation;
using RvtMcp.Plugin.Views;

namespace RvtMcp.Plugin.Reports.Views
{
    /// <summary>
    /// Report body: a read-only RichTextBox that grows with its document inside the outer scroll
    /// region, keeps text selectable and links clickable, and never scrolls by itself.
    /// </summary>
    internal sealed class ReportBodyView : RichTextBox
    {
        private ReportDetailViewModel _detail;
        private bool _pointerLinkFocus;

        public ReportBodyView()
        {
            IsReadOnly = true;
            IsDocumentEnabled = true;
            IsUndoEnabled = false;
            BorderThickness = new Thickness(0);
            Background = Brushes.Transparent;
            Padding = new Thickness(0);
            Foreground = ReportsStyles.Text;
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            Document = new FlowDocument();
            AddHandler(RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler(OnLinkBringIntoView), true);
        }

        public ReportDetailViewModel Detail => _detail;

        /// <summary>Rebuilds only for a different detail object, so a reload that keeps the detail keeps the scroll position.</summary>
        public void SetDetail(ReportDetailViewModel detail)
        {
            if (ReferenceEquals(detail, _detail)) return;
            _detail = detail;
            Document = detail == null ? new FlowDocument() : ReportFlowDocumentBuilder.Build(detail.Body, detail.Links,
                detail.ExpandedBodySections, detail.BodyTablePages);
        }

        private void OnLinkBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        {
            if (!(e.TargetObject is Hyperlink link)) return;
            // RichTextBox's inner viewer forwards a rectangle at (0,0) when the link takes focus.
            // Reveal the actual text in the outer viewer instead. A visible pointer target stays put;
            // keyboard focus on an off-screen link still scrolls it into view.
            e.Handled = true;
            for (var parent = VisualTreeHelper.GetParent(this); parent != null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (!(parent is ScrollViewer viewer)) continue;
                HistoryChrome.StopWheel(viewer);
                if (_pointerLinkFocus) return;
                var rect = link.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                if (rect.IsEmpty) return;
                var point = TransformToAncestor(viewer).Transform(new Point(rect.X, rect.Y));
                var delta = point.Y < 0 ? point.Y : Math.Max(0, point.Y + rect.Height - viewer.ViewportHeight);
                var offset = Math.Max(0, Math.Min(viewer.ScrollableHeight, viewer.VerticalOffset + delta));
                // Calling RichTextBox.BringIntoView here re-enters text focus on .NET Framework.
                // Move only the outer viewport, with tolerance for fractional text-layout rounding.
                if (Math.Abs(offset - viewer.VerticalOffset) > 1) viewer.ScrollToVerticalOffset(offset);
                return;
            }
        }

        protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
        {
            for (var source = e.OriginalSource as FrameworkContentElement; source != null; source = source.Parent as FrameworkContentElement)
            {
                if (!(source is Hyperlink)) continue;
                _pointerLinkFocus = true;
                Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => _pointerLinkFocus = false));
                break;
            }
            base.OnPreviewMouseDown(e);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            _pointerLinkFocus = false;
            base.OnPreviewKeyDown(e);
        }

        /// <summary>
        /// The template's ScrollViewer handles every wheel event even with its bars disabled.
        /// Take the tunnelling event first and re-raise it on the parent so the outer scroll region moves.
        /// </summary>
        protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
        {
            base.OnPreviewMouseWheel(e);
            if (e.Handled) return;
            e.Handled = true;
            var parent = VisualTreeHelper.GetParent(this) as UIElement;
            parent?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = this });
        }
    }
}
