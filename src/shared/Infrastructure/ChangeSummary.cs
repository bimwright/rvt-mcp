using System.Linq;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    /// <summary>Allowlist at the server boundary; model paths and future private identity fields never pass through.</summary>
    public static class ChangeSummary
    {
        public static JObject ForAgent(JToken changes)
        {
            if (!(changes is JObject source)) return null;
            var result = new JObject();
            CopyScalars(source, result, "complete", "note", "truncated");
            var documents = new JArray();
            foreach (var doc in (source["documents"] as JArray ?? new JArray()).OfType<JObject>().Take(8))
            {
                var safe = new JObject();
                CopyScalars(doc, safe, "document", "status", "note", "truncated");
                safe["transactions"] = new JArray((doc["transactions"] as JArray ?? new JArray())
                    .Where(x => x.Type == JTokenType.String).Take(20).Select(x => (JToken)x.DeepClone()));
                foreach (var kind in new[] { "added", "modified", "deleted" })
                {
                    var set = doc[kind] as JObject ?? new JObject();
                    safe[kind] = new JObject
                    {
                        ["count"] = set["count"]?.Type == JTokenType.Integer ? set["count"].DeepClone() : JValue.CreateNull(),
                        ["ids"] = new JArray((set["ids"] as JArray ?? new JArray())
                            .Where(x => x.Type == JTokenType.Integer).Take(DocumentChangeAccumulator.IdLimit).Select(x => x.DeepClone()))
                    };
                }
                var categories = new JObject();
                foreach (var p in (doc["by_category"] as JObject ?? new JObject()).Properties().Where(p => p.Value.Type == JTokenType.Integer))
                    categories[p.Name] = p.Value.DeepClone();
                safe["by_category"] = categories;
                documents.Add(safe);
            }
            result["documents"] = documents;
            return result;
        }

        public static JObject Omitted() => new JObject
        {
            ["complete"] = false,
            ["truncated"] = true,
            ["note"] = "Change details exceeded the response budget. Changes may have occurred; inspect the model before another write."
        };

        private static void CopyScalars(JObject source, JObject destination, params string[] names)
        {
            foreach (var name in names)
                if (source[name] is JValue value) destination[name] = value.DeepClone();
        }
    }
}
