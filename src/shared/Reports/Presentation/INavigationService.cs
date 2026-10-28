using System;
using System.Collections.Generic;
using RvtMcp.Plugin.Reports.Model;

namespace RvtMcp.Plugin.Reports.Presentation
{
    public interface INavigationService
    {
        void Navigate(ReportNavigationRequest request);
        event EventHandler<ReportNavigationResult> Completed;
    }

    public sealed class ReportNavigationRequest
    {
        public ReportDocument Document { get; set; }
        public IReadOnlyList<ReportElement> Elements { get; set; }
        public ReportView ViewHint { get; set; }
    }

    public enum ReportNavigationOutcome
    {
        Selected, ViewActivated, ElementsGone, NoGraphics, NoViewFound, NotMatched, Failed
    }

    public sealed class ReportNavigationResult : EventArgs
    {
        public ReportNavigationOutcome Outcome { get; set; }
        public int SelectedCount { get; set; }
        public int MissingCount { get; set; }
        public string ViewName { get; set; }
    }
}
