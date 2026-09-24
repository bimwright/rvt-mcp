namespace RvtMcp.Plugin.Views.Toast
{
    public class McpToastViewModel
    {
        public string CommandName { get; set; }
        public string Title { get; set; }
        /// <summary>Short badge, e.g. "MCP · Query".</summary>
        public string CategoryLabel { get; set; }
        /// <summary>Primary outcome line.</summary>
        public string Summary { get; set; }
        /// <summary>Secondary context (filters, path, view name, etc.).</summary>
        public string Detail { get; set; }
        /// <summary>Optional local image path for thumbnail preview (capture/export).</summary>
        public string ThumbnailPath { get; set; }
        public ToolActivityKind Kind { get; set; }
        public bool Success { get; set; }
        public long DurationMs { get; set; }
        /// <summary>Optional override for auto-dismiss delay (seconds).</summary>
        public int? AutoDismissSeconds { get; set; }
    }
}
