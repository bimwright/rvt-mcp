using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Bimwright.Targeting;
using RvtMcp.Plugin; // RvtMcpConfig
using RvtMcp.Server.Bake;
using RvtMcp.Server.Handlers;
using RvtMcp.Server.Prompts;
using RvtMcp.ToolCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    [McpServerToolType, Toolset("toolbaker")]
    public class ToolbakerTools
    {
        [McpServerTool(Name = "revit_list_baked_tools", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), System.ComponentModel.Description(
            "List baked tools with nameFilter and bounded paging. Use startIndex + limit (hard max 500).")]
        public static async Task<string> ListBakedTools(string nameFilter = "", int startIndex = 0, int limit = 100)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_baked_tools", new { name_filter = nameFilter, start_index = startIndex, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

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
                RecordBakedToolRun(name, revitVersion, success: true, error: null);
                return JsonConvert.SerializeObject(result, Formatting.Indented);
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

        private static void RecordBakedToolRun(string name, string revitVersion, bool success, string error)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;

            try
            {
                using var db = new BakeDb(new BakePaths());
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
