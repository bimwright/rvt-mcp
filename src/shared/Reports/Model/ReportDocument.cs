using Newtonsoft.Json;

namespace RvtMcp.Plugin.Reports.Model
{
    public static class ReportDocumentKinds
    {
        public const string Cloud = "cloud";
        public const string Central = "central";
        public const string File = "file";
        public const string Detached = "detached";
        public const string Unsaved = "unsaved";
    }

    public sealed class ReportDocument
    {
        [JsonProperty("ref")]
        public string Ref { get; set; }
        [JsonProperty("token")]
        public string Token { get; set; }
        [JsonProperty("model_key")]
        public string ModelKey { get; set; }
        [JsonProperty("title")]
        public string Title { get; set; }
        [JsonProperty("is_family")]
        public bool IsFamily { get; set; }
        [JsonProperty("kind")]
        public string Kind { get; set; }
    }
}
