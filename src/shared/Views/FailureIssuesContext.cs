namespace RvtMcp.Plugin.Views
{
    /// <summary>Snapshot captured on Revit's API thread; modeless Issues never reads Document.</summary>
    internal sealed class FailureIssuesContext
    {
        public string ModelScopeKey { get; set; }
        public string ModelTitle { get; set; }
        public int RevitYear { get; set; }
        public string DocumentRevision { get; set; }
        public string RevitUser { get; set; }
        public string HostInstanceId { get; set; }
        public bool IsAvailable { get; set; }
    }
}
