using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using RvtMcp.Plugin.Reports.Presentation;

namespace RvtMcp.Plugin.Reports.Views
{
    /// <summary>
    /// Report body: a read-only RichTextBox that grows with its document inside the outer scroll
    /// region, keeps text selectable and links clickable, and never scrolls by itself.
    /// </summary>
    internal sealed class ReportBodyView : RichTextBox
    {
        private ReportDetailViewModel _detail;

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
        }

        public ReportDetailViewModel Detail => _detail;

        /// <summary>Rebuilds only for a different detail object, so a reload that keeps the detail keeps the scroll position.</summary>
        public void SetDetail(ReportDetailViewModel detail)
        {
            if (ReferenceEquals(detail, _detail)) return;
            _detail = detail;
            Document = detail == null ? new FlowDocument() : ReportFlowDocumentBuilder.Build(detail.Body, detail.Links);
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
