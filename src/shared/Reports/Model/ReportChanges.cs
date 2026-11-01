using System.Collections.Generic;
using Newtonsoft.Json;

namespace RvtMcp.Plugin.Reports.Model
{
    public sealed class ReportChanges
    {
        public const string ProcessScope = "process";
        [JsonProperty("scope")]
        public string Scope { get; set; } = ProcessScope;
        [JsonProperty("calls", NullValueHandling = NullValueHandling.Ignore)]
        public List<ReportCallReference> Calls { get; set; }
        [JsonProperty("capture_sequence", NullValueHandling = NullValueHandling.Ignore)]
        public long? CaptureSequence { get; set; }
        [JsonProperty("complete")]
        public bool Complete { get; set; }
        [JsonProperty("truncated")]
        public bool Truncated { get; set; }
        [JsonProperty("documents")]
        public List<ReportDocumentChanges> Documents { get; set; } = new List<ReportDocumentChanges>();
    }

    public sealed class ReportCallReference
    {
        [JsonProperty("call_id")] public string CallId { get; set; }
        [JsonProperty("client_session")] public string Session { get; set; }
        [JsonProperty("tool")] public string Tool { get; set; }
        [JsonProperty("at")] public string At { get; set; }
        [JsonProperty("model_key")] public string ModelKey { get; set; }
        [JsonProperty("history_requested")] public bool HistoryRequested { get; set; }
    }

    public sealed class ReportDocumentChanges
    {
        [JsonProperty("doc")]
        public string Doc { get; set; }
        [JsonProperty("added")]
        public List<ReportChangedElement> Added { get; set; } = new List<ReportChangedElement>();
        [JsonProperty("modified")]
        public List<ReportChangedElement> Modified { get; set; } = new List<ReportChangedElement>();
        [JsonProperty("deleted")]
        public List<ReportChangedElement> Deleted { get; set; } = new List<ReportChangedElement>();
    }

    public sealed class ReportChangedElement
    {
        [JsonProperty("id")]
        public long Id { get; set; }
        [JsonProperty("unique_id")]
        public string UniqueId { get; set; }
        [JsonProperty("category")]
        public string Category { get; set; }
    }
}
