using System.Collections.Generic;
using Newtonsoft.Json;

namespace RvtMcp.Plugin.Reports.Model
{
    public sealed class ReportChanges
    {
        [JsonProperty("complete")]
        public bool Complete { get; set; }
        [JsonProperty("truncated")]
        public bool Truncated { get; set; }
        [JsonProperty("documents")]
        public List<ReportDocumentChanges> Documents { get; set; } = new List<ReportDocumentChanges>();
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
