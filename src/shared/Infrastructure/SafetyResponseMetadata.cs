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
            // Dialogs the user handled outside MCP: information only, dropped first when space is short.
            var userDialogs = source?["_userDialogs"] as JObject ?? (source?["data"] as JObject)?["_userDialogs"] as JObject;
            if (userDialogs != null && !compact && Bytes(userDialogs) <= 8192) target["_userDialogs"] = userDialogs.DeepClone();
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
            if (!compact && ShrinkDialogs(result) && Bytes(result) <= budget) return result;
            var minimal = new JObject();
            foreach (var key in new[] { "schemaVersion", "operationId", "hostInstanceId", "incidentIds", "modelScopeKey", "action", "execution", "outcome", "mutationApplied", "gate", "persistence", "complete", "requiresReview", "messageCount", "observationCoverage" })
                if (result[key] != null) minimal[key] = result[key].DeepClone();
            minimal["summaryTruncated"] = true;
            // Identifiers are bounded by the producer; still defend against malformed older plugins.
            foreach (var property in minimal.Properties().ToList())
            {
                if (property.Value.Type == JTokenType.String && property.Value.Value<string>().Length > 256)
                    property.Value = property.Value.Value<string>().Substring(0, 256);
                if (property.Value is JArray array && array.Count > 20)
                {
                    property.Value = new JArray(array.Take(20));
                    if (property.Name == "incidentIds") minimal["incidentIdsOmitted"] = true;
                }
            }
            if (Bytes(minimal) > budget && minimal["incidentIds"] is JArray)
            { minimal["incidentIds"] = new JArray(); minimal["incidentIdsOmitted"] = true; }
            return minimal;
        }
        // Long dialog text in another language can exceed the receipt budget; keep the code and the start of the text.
        private static bool ShrinkDialogs(JObject receipt)
        {
            bool changed = false;
            foreach (var dialog in new[] { receipt["dialog"], receipt["blockedBy"]?["dialog"] }.OfType<JObject>())
            {
                dialog.Remove("buttons");
                if (dialog["text"]?.Type == JTokenType.String && dialog.Value<string>("text").Length > 240)
                    dialog["text"] = dialog.Value<string>("text").Substring(0, 228) + " [truncated]";
                changed = true;
            }
            return changed;
        }

        private static int Bytes(JToken value) => Encoding.UTF8.GetByteCount(value.ToString(Formatting.None));
    }
}
