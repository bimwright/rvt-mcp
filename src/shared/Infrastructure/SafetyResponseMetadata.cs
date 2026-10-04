using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    /// <summary>Safety receipts survive compaction, including unsuccessful tool results.</summary>
    public static class SafetyResponseMetadata
    {
        public static void Attach(JObject target, JObject source, bool compact = false)
        {
            foreach (var key in new[] { "_failure", "_operation" })
            {
                var receipt = source?[key] as JObject ?? (source?["data"] as JObject)?[key] as JObject;
                if (receipt != null) target[key] = Bound(receipt, key == "_failure" ? 16384 : 4096, compact);
            }
        }

        public static JObject Bound(JObject receipt, int budget, bool compact = false)
        {
            var result = (JObject)receipt.DeepClone();
            if (result["groups"] is JArray groups)
            {
                if (groups.Count > 20) { result["groups"] = new JArray(groups.Take(20)); result["summaryTruncated"] = true; }
                foreach (var group in ((JArray)result["groups"]).OfType<JObject>())
                    if (group["sampleElementIds"] is JArray ids && ids.Count > 20)
                    { group["sampleElementIds"] = new JArray(ids.Take(20)); result["summaryTruncated"] = true; }
            }
            if (!compact && Bytes(result) <= budget) return result;
            var minimal = new JObject();
            foreach (var key in new[] { "schemaVersion", "operationId", "hostInstanceId", "incidentIds", "modelScopeKey", "action", "execution", "outcome", "mutationApplied", "gate", "persistence", "complete", "requiresReview", "messageCount", "observationCoverage" })
                if (result[key] != null) minimal[key] = result[key].DeepClone();
            minimal["summaryTruncated"] = true;
            // Identifiers are bounded by the producer; still defend against malformed older plugins.
            foreach (var property in minimal.Properties().ToList())
            {
                if (property.Value.Type == JTokenType.String && property.Value.Value<string>().Length > 256)
                    property.Value = property.Value.Value<string>().Substring(0, 256);
                if (property.Value is JArray array && array.Count > 20) property.Value = new JArray(array.Take(20));
            }
            if (Bytes(minimal) > budget && minimal["incidentIds"] is JArray)
            { minimal["incidentIds"] = new JArray(); minimal["incidentIdsOmitted"] = true; }
            return minimal;
        }
        private static int Bytes(JToken value) => Encoding.UTF8.GetByteCount(value.ToString(Formatting.None));
    }
}
