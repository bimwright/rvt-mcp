using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

namespace RvtMcp.Plugin
{
    /// <summary>Authenticated gateway metadata, not an agent-supplied tool parameter.</summary>
    public sealed class ReportCallContext
    {
        [JsonProperty("call_id")] public string CallId { get; set; }
        [JsonProperty("client_session")] public string Session { get; set; }
        [JsonProperty("history_requested")] public bool HistoryRequested { get; set; }
        [JsonIgnore] public bool IsValid => IsId(CallId) && IsId(Session);
        public static bool IsId(string value) => value?.Length == 32 && Guid.TryParseExact(value, "N", out _);
        public static IReadOnlyList<string> ParseCallIds(string json)
        {
            if (json == null) return null;
            JArray array;
            try { array = JArray.Parse(json); }
            catch { throw new ArgumentException("call_ids must be a JSON array of capture IDs."); }
            if (array.Count > 256 || array.Any(v => v.Type != JTokenType.String || !IsId(v.Value<string>())))
                throw new ArgumentException("call_ids must contain at most 256 server-issued capture IDs.");
            var ids = array.Values<string>().Select(s => s.ToLowerInvariant()).ToArray();
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw new ArgumentException("call_ids must not contain duplicates.");
            return ids;
        }
    }

    public static class ReportExecutionContext
    {
        [ThreadStatic] private static ReportCallContext _current;
        public static ReportCallContext Current => _current;
        public static IDisposable Enter(ReportCallContext value) => new Scope(value);
        private sealed class Scope : IDisposable
        {
            private readonly ReportCallContext _previous;
            public Scope(ReportCallContext value) { _previous = _current; _current = value; }
            public void Dispose() { _current = _previous; }
        }
    }
}
