using System;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    /// <summary>Guards a completed response without replaying the operation.</summary>
    public static class ResponseEnvelopeGuard
    {
        public static JObject Apply(string command, string paramsJson, JObject envelope, RvtMcpConfig config,
            ResponseSpillWriter writer = null, string measuredPayload = null)
        {
            config = config ?? new RvtMcpConfig();
            config.ValidateResponseLimits();
            var budget = config.EnableResponseGuardOrDefault
                ? config.ResponseBudgetBytesOrDefault : config.MaxResponseBytesOrDefault;
            var response = (JObject)envelope.DeepClone();
            var operationError = ResponseSizePolicyCatalog.GetOperationError(command, response["data"] as JObject);
            if (operationError != null)
            {
                response["success"] = false;
                response["error"] = McpResponsePrivacy.RedactErrorForResponse(operationError);
            }
            var payload = measuredPayload ?? response.ToString(Formatting.None);
            var originalBytes = Encoding.UTF8.GetByteCount(payload);
            var spill = new ResponseSpillProcessor(writer ?? new ResponseSpillWriter()).Process(
                command, paramsJson, response.Value<bool>("success"), response["data"], payload,
                budget, automaticSpill: true);
            if (spill.Spilled || spill.Data != response["data"])
            {
                response["data"] = spill.Data == null ? JValue.CreateNull() : JToken.FromObject(spill.Data);
                payload = response.ToString(Formatting.None);
            }

            var size = ResponseSizeGuard.Evaluate(command, payload, (response["data"] as JObject)?.Count ?? 0,
                config.EnableResponseGuardOrDefault ? config.ResponseWarnBytesOrDefault : int.MaxValue,
                budget,
                config.EnableResponseGuardOrDefault ? config.ResponseStrongWarnBytesOrDefault : int.MaxValue);
            if (size.Warning != null) Console.Error.WriteLine(size.Warning);
            if (size.Reject)
            {
                var completed = response.Value<bool>("success")
                    && ResponseSizePolicyCatalog.ShouldPreserveSuccessfulMutation(command, paramsJson,
                        !ToolReadPolicy.IsReadOnly(command));
                if (!completed)
                {
                    response.Remove("data");
                    response["success"] = false;
                    response["error"] = size.RejectError;
                    return response;
                }

                var compact = MutationResponseCompactor.Compact(response["data"], originalBytes);
                if (ResponseSizePolicyCatalog.IsMutationOutcomeIndeterminate(command))
                    compact["mutation_applied"] = JValue.CreateNull();
                response["data"] = compact;
                response["warning"] = compact.Value<string>("warning");
                if (Encoding.UTF8.GetByteCount(response.ToString(Formatting.None)) > budget)
                {
                    response["data"] = new JObject
                    {
                        ["success"] = true,
                        ["mutation_applied"] = compact["mutation_applied"],
                        ["response_compacted"] = true,
                        ["original_byte_count"] = compact["original_byte_count"]
                    };
                    var outputPath = compact["output_path"] ?? compact["summary"]?["output_path"];
                    if (outputPath != null) response["data"]["output_path"] = outputPath.DeepClone();
                }
            }
            else if (size.AgentWarning != null)
            {
                response["warning"] = size.AgentWarning;
                if (Encoding.UTF8.GetByteCount(response.ToString(Formatting.None)) > budget)
                    response.Remove("warning");
            }
            return response;
        }
    }
}
