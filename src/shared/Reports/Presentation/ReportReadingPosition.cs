using System.Collections.Generic;

namespace RvtMcp.Plugin.Reports.Presentation
{
    // Transient per-window state; never retains a WPF control or writes user configuration.
    internal sealed class ReportReadingPosition
    {
        public string SelectedId { get; set; }
        public string AnchorId { get; set; }
        public double Top { get; set; }
        public double Offset { get; set; }
        public double ListOffset { get; set; }
        public bool Body { get; set; }
        public bool AtTop { get; set; }
        public List<string> Neighbours { get; set; }
    }
}
