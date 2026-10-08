using System;
using System.Text.Json;
using System.Threading.Tasks;
using RvtMcp.Server.Bake;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("toolbaker")]
    public class ToolbakerTools
    {
        internal static Func<BakeDb> RunStoreFactory = () => new BakeDb(new BakePaths());
        [McpServerTool(Name = "revit_list_baked_tools", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description(
            "List baked tools with nameFilter and bounded paging. Use startIndex + limit (hard max 500).")]
        public static async Task<string> ListBakedTools(string nameFilter = "", int startIndex = 0, int limit = 100)
            => await ToolGateway.CallAsync("list_baked_tools", new { name_filter = nameFilter, start_index = startIndex, limit });

        [McpServerTool(Name = "revit_run_baked_tool", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), System.ComponentModel.Description(
            "Run a baked tool by name. Call list_baked_tools first to discover. " +
            "Params: name, params (tool-specific), output=inline|file. File mode writes a sniffed local same-machine artifact; oversized inline output auto-spills. Remote clients receive preview only." +
            " May change documents and files; Undo cannot restore arbitrary file writes. Review its source and back up affected files before running.")]
        public static async Task<string> RunBakedTool(string name, object @params = null, string output = "inline")
        {
            var revitVersionBeforeConnect = ToolGateway.CurrentRevitVersion ?? "unknown";
            try
            {
                var normalizedParams = NormalizeRunBakedToolParams(@params);
                var result = await ToolGateway.SendToRevit("run_baked_tool", new { name, @params = normalizedParams, output });
                var revitVersion = ToolGateway.CurrentRevitVersion ?? revitVersionBeforeConnect;
                if (TryGetDefinitiveRunOutcome(result, out var succeeded))
                    RecordBakedToolRun(name, revitVersion, succeeded, succeeded ? null : result.Value<string>("error"));
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (TargetException ex)
            {
                // Target/transport failures do not establish a native execution outcome.
                // The MCP filter still returns the target and operation from the call context.
                return $"Error: {ex.Message}";
            }
            catch (Exception ex)
            {
                var revitVersion = ToolGateway.CurrentRevitVersion ?? revitVersionBeforeConnect;
                RecordBakedToolRun(name, revitVersion, success: false, error: ex.Message);
                return $"Error: {ex.Message}";
            }
        }

        public static JObject NormalizeRunBakedToolParams(object @params)
        {
            if (@params == null)
                return new JObject();

            if (@params is JObject obj)
                return obj;

            if (@params is JToken token)
            {
                if (token is JObject tokenObj)
                    return tokenObj;
                throw new ArgumentException("params must be a JSON object.");
            }

            if (!(@params is JsonElement element))
            {
                var converted = McpJsonInput.FromObject(@params);
                if (converted is JObject convertedObj)
                    return convertedObj;
                throw new ArgumentException("params must be a JSON object.");
            }

            switch (element.ValueKind)
            {
                case JsonValueKind.Undefined:
                case JsonValueKind.Null:
                    return new JObject();
                case JsonValueKind.Object:
                    return JObject.Parse(element.GetRawText());
                default:
                    throw new ArgumentException("params must be a JSON object.");
            }
        }

        // Stats describe a finished run only. Pending and not-started receipts are
        // neither success nor failure; recording them would corrupt compat data.
        internal static bool TryGetDefinitiveRunOutcome(JObject result, out bool succeeded)
        {
            succeeded = false;
            if (ToolGateway.IsPendingOrUnknown(result)
                || string.Equals((result?["_operation"] as JObject)?.Value<string>("outcome"), "not_started", StringComparison.Ordinal))
                return false;
            succeeded = result?.Value<bool?>("success") != false;
            return true;
        }

        private static void RecordBakedToolRun(string name, string revitVersion, bool success, string error)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;

            try
            {
                using var db = RunStoreFactory();
                db.Migrate();
                db.TryRecordRegistryRun(name, revitVersion, success, error, DateTimeOffset.UtcNow);
            }
            catch
            {
                // Server owns durable bake.db writes, but run_baked_tool should not fail
                // just because lifecycle stats could not be updated.
            }
        }
    }
}
