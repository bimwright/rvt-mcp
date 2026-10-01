#nullable enable
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    public sealed class ResponseSpillDecision
    {
        public bool ShouldSpill { get; set; }
        public bool Automatic { get; set; }
        public ResponseSpillFormat Format { get; set; }
    }

    /// <summary>
    /// Boundary policy for the eight approved bulk commands and arbitrary-code output.
    /// All other commands retain the inline response-size policy.
    /// </summary>
    public static class ResponseSpillPolicy
    {
        private static readonly IReadOnlyDictionary<string, ResponseSpillFormat> OptInFormats =
            new Dictionary<string, ResponseSpillFormat>(StringComparer.OrdinalIgnoreCase)
            {
                ["compute_room_finishes"] = ResponseSpillFormat.Sqlite,
                ["export_room_data"] = ResponseSpillFormat.Sqlite,
                ["get_material_takeoff"] = ResponseSpillFormat.Sqlite,
                ["workflow_takeoff_report"] = ResponseSpillFormat.Sqlite,
                ["batch_execute"] = ResponseSpillFormat.Ndjson,
                ["export_shared_parameter_file"] = ResponseSpillFormat.Json,
                ["run_baked_tool"] = ResponseSpillFormat.Auto,
                ["workflow_data_roundtrip"] = ResponseSpillFormat.Ndjson
            };

        public static ResponseSpillDecision Evaluate(string? commandName, string? paramsJson, int compactResponseBytes,
            int budgetBytes = ResponseSizeGuard.EnforcementBudgetBytes, bool automaticSpill = true)
        {
            var command = commandName ?? string.Empty;
            var output = ReadOutputMode(paramsJson);
            if (OptInFormats.TryGetValue(command, out var format)
                && string.Equals(output, "file", StringComparison.OrdinalIgnoreCase))
            {
                return new ResponseSpillDecision
                {
                    ShouldSpill = true,
                    Automatic = false,
                    Format = format
                };
            }

            if ((string.Equals(command, "send_code_to_revit", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(command, "run_baked_tool", StringComparison.OrdinalIgnoreCase))
                && automaticSpill && compactResponseBytes > budgetBytes)
            {
                return new ResponseSpillDecision
                {
                    ShouldSpill = true,
                    Automatic = true,
                    Format = ResponseSpillFormat.Auto
                };
            }

            return new ResponseSpillDecision
            {
                ShouldSpill = false,
                Automatic = false,
                Format = format
            };
        }

        public static object? SelectPayload(string? commandName, object? data)
        {
            if (!string.Equals(commandName, "send_code_to_revit", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(commandName, "run_baked_tool", StringComparison.OrdinalIgnoreCase))
            {
                return data;
            }

            var token = data as JToken;
            if (token == null && data != null)
                token = JToken.FromObject(data);
            return token is JObject obj && obj.TryGetValue("result", out var output)
                ? output
                : data;
        }

        private static string ReadOutputMode(string? paramsJson)
        {
            if (string.IsNullOrWhiteSpace(paramsJson))
                return "inline";
            try { return JObject.Parse(paramsJson ?? "{}").Value<string>("output") ?? "inline"; }
            catch { return "inline"; }
        }
    }
}
