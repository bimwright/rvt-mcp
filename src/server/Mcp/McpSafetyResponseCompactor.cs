using System;
using System.Linq;
using System.Text;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    internal static class McpSafetyResponseCompactor
    {
        // Receipt-local bounds cannot account for MCP text escaping or a smaller user cap.
        // Shed queryable detail first; never exchange outcome/gate facts for a success summary.
        internal static CallToolResult Fit(JObject data, bool failed, int limit)
        {
            CallToolResult Result() => new CallToolResult
            {
                Content = new[] { new TextContentBlock { Text = data.ToString(Formatting.None) } }, IsError = failed
            };
            bool Fits() => Encoding.UTF8.GetByteCount(RuntimeToolFilter.Serialize(Result())) <= limit;
            if (Fits()) return Result();

            var failure = data["_failure"] as JObject;
            var operation = data["_operation"] as JObject;
            if (failure?["incidentIds"] is JArray ids)
            {
                while (ids.Count > 0 && !Fits())
                {
                    failure["incidentIdsOmitted"] = true;
                    ids.RemoveAt(ids.Count - 1);
                }
                if (ids.Count == 0) failure.Remove("incidentIds");
            }
            foreach (var key in new[] { "_changes", "_history" })
            {
                if (Fits()) return Result();
                data.Remove(key);
            }
            foreach (var key in new[] { "hostInstanceId", "schemaVersion", "messageCount", "observationCoverage", "modelScopeKey", "requiresReview", "summaryTruncated" })
            {
                if (Fits()) return Result();
                failure?.Remove(key);
                operation?.Remove(key);
            }
            if (!Fits() && operation?["operationId"] != null && JToken.DeepEquals(failure?["operationId"], operation["operationId"]))
                failure.Remove("operationId"); // The same issued ID remains on the operation receipt.
            if (!Fits()) failure?.Remove("mutationApplied"); // Native outcome stays on _operation.
            if (!Fits() && data["error"]?.Type == JTokenType.String)
            {
                var error = data.Value<string>("error");
                var separator = error.IndexOfAny(new[] { ':', '\r', '\n' });
                data["error"] = error.Substring(0, Math.Min(separator < 0 ? error.Length : separator, 128));
            }
            if (!Fits()) data.Remove("error");
            if (!Fits()) data.Remove("response_compacted"); // incidentIdsOmitted still explicitly signals omitted evidence.
            if (!Fits() && data["target"] is JObject target) target.Remove("summaryTruncated");
            if (!Fits())
                throw new InvalidOperationException("The response cap cannot fit the operation safety receipt. Inspect the model; do not retry the operation.");
            return Result();
        }
    }
}
