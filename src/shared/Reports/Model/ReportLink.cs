using System.Collections.Generic;
using Newtonsoft.Json;

namespace RvtMcp.Plugin.Reports.Model
{
    public static class ReportElementKinds
    {
        public const string Element = "element";
        public const string View = "view";
        public const string Sheet = "sheet";
        public const string Type = "type";
        public const string Other = "other";
    }

    public sealed class ReportLink
    {
        [JsonProperty("label")]
        public string Label { get; set; }
        [JsonProperty("doc")]
        public string Doc { get; set; }
        [JsonProperty("view")]
        public ReportView View { get; set; }
        [JsonProperty("elements")]
        public List<ReportElement> Elements { get; set; } = new List<ReportElement>();
    }

    public sealed class ReportView
    {
        [JsonProperty("id")]
        public long Id { get; set; }
        [JsonProperty("unique_id")]
        public string UniqueId { get; set; }
    }

    public sealed class ReportElement
    {
        [JsonProperty("id")]
        public long Id { get; set; }
        [JsonProperty("unique_id")]
        public string UniqueId { get; set; }
        [JsonProperty("kind")]
        public string Kind { get; set; }
        [JsonProperty("category")]
        public string Category { get; set; }
        [JsonProperty("name")]
        public string Name { get; set; }
    }
}
