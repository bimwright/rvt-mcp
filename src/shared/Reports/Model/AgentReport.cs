using System.Collections.Generic;
using Newtonsoft.Json;

namespace RvtMcp.Plugin.Reports.Model
{
    public sealed class AgentReport
    {
        [JsonProperty("schema_version")]
        public int SchemaVersion { get; set; } = 1;
        [JsonProperty("report_id")]
        public string ReportId { get; set; }
        [JsonProperty("task_id")]
        public string TaskId { get; set; }
        [JsonProperty("outcome")]
        public string Outcome { get; set; }
        [JsonProperty("summary")]
        public string Summary { get; set; }
        [JsonProperty("body")]
        public string Body { get; set; }
        [JsonProperty("created_utc")]
        public string CreatedUtc { get; set; }
        [JsonProperty("updated_utc")]
        public string UpdatedUtc { get; set; }
        [JsonProperty("source")]
        public ReportSource Source { get; set; }
        [JsonProperty("documents")]
        public List<ReportDocument> Documents { get; set; } = new List<ReportDocument>();
        [JsonProperty("links")]
        public List<ReportLink> Links { get; set; } = new List<ReportLink>();
        [JsonProperty("changes")]
        public ReportChanges Changes { get; set; }
    }

    public sealed class ReportSource
    {
        [JsonProperty("target_id")]
        public string TargetId { get; set; }
        [JsonProperty("pid")]
        public int Pid { get; set; }
        [JsonProperty("host_year")]
        public int HostYear { get; set; }
        [JsonProperty("process_start_utc")]
        public string ProcessStartUtc { get; set; }
    }
}
