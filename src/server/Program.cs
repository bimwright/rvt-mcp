// Usage:
//   stdio (default):  RvtMcp.Server.exe              — spawned by Claude/GPT/Cursor
//   HTTP SSE:          RvtMcp.Server.exe --http 8200  — for Ollama/LM Studio/custom
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RvtMcp.Plugin; // RvtMcpConfig
using RvtMcp.Server.Bake;
using RvtMcp.Server.Handlers;
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
    class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Any(a => a == "--help" || a == "-h"))
            {
                PrintHelp();
                return;
            }

            // A9 3-layer config precedence (JSON < env < CLI). AuthToken.Target + transport
            // mode (--http) stay as separate CLI parses for now; A3 toolsets gating uses
            // RvtMcpConfig.
            LegacyDataMigration.MigrateOnce();
            AuthToken.CleanupLegacyDiscoveryFiles();
            var config = RvtMcpConfig.Load(args);
            ServerState.Config = config;
            if (!string.IsNullOrWhiteSpace(config.Target))
            {
                var target = config.Target.Trim();
                if (Array.IndexOf(AuthToken.AllVersions, target) < 0)
                {
                    Console.Error.WriteLine(
                        "[RvtMcp] Invalid --target value '" + config.Target + "'. " +
                        "Expected a 4-digit Revit calendar year: 2022 | 2023 | 2024 | 2025 | 2026 | 2027. " +
                        "Note: legacy R-codes (R22..R27) are no longer accepted in v0.5; use the year directly.");
                    Environment.Exit(1);
                    return;
                }
                AuthToken.Target = target;
            }

            var bakePaths = new BakePaths();
            TryInitializeBakeStorage(bakePaths, out _);

            // Initialize memory system (shared across tool classes + resources)
            var session = new Memory.SessionContext();
            ToolGateway.Session = session;
            ToolGateway.UsageLogger = new UsageEventLogger(bakePaths, config);
            RevitResources.Session = session;

            int httpIndex = Array.IndexOf(args, "--http");
            if (httpIndex >= 0)
            {
                if (httpIndex + 1 >= args.Length || !int.TryParse(args[httpIndex + 1], out var port)
                    || port < 1 || port > 65535)
                {
                    Console.Error.WriteLine("[RvtMcp] Invalid --http argument. Expected: --http <port> (1-65535)");
                    Environment.Exit(1);
                    return;
                }
                await RunHttpSse(config, port);
            }
            else
            {
                await RunStdio(config);
            }
        }

        internal static LegacyBakedToolImportResult InitializeBakeStorage(BakePaths paths)
        {
            if (paths == null)
                throw new ArgumentNullException(nameof(paths));

            using var db = new BakeDb(paths);
            db.Migrate();
            var importer = new LegacyBakedToolImporter(paths, db, new ToolBakerAuditLog(paths.AuditJsonl));
            return importer.ImportIfNeeded();
        }

        internal static bool TryInitializeBakeStorage(BakePaths paths, out LegacyBakedToolImportResult result)
        {
            try
            {
                result = InitializeBakeStorage(paths);
                return true;
            }
            catch (Exception ex)
            {
                result = null;
                var pathHint = paths?.Root ?? "(unknown path)";
                Console.Error.WriteLine(
                    $"[RvtMcp] Warning: ToolBaker bake storage initialization failed for {pathHint}. " +
                    "The MCP server will continue; baked-tool migration/import can be retried on next startup. " +
                    $"{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static async Task RunStdio(RvtMcpConfig config)
        {
            var enabled = ToolsetFilter.Resolve(config);
            var builder = Host.CreateApplicationBuilder();
            // stdio MCP stdout must contain JSON-RPC only. ClearProviders() removes default
            // host logging, but the MCP SDK (and any transitive package) may re-add a Console
            // provider that writes to stdout. AddConsole with LogToStandardErrorThreshold=Trace
            // forces every console log line — from any provider re-added downstream — to stderr.
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(opts => opts.LogToStandardErrorThreshold = LogLevel.Trace);
            var mcp = builder.Services
                .AddMcpServer(ConfigureMcpServerOptions)
                .WithStdioServerTransport();
            mcp = RegisterToolsets(mcp, enabled, config);
            mcp.WithResources<RevitResources>();
            var app = builder.Build();
            await app.RunAsync();
        }

        private static async Task RunHttpSse(RvtMcpConfig config, int port)
        {
            var enabled = ToolsetFilter.Resolve(config);
            var builder = WebApplication.CreateBuilder();
            var mcp = builder.Services
                .AddMcpServer(ConfigureMcpServerOptions)
                .WithHttpTransport();
            mcp = RegisterToolsets(mcp, enabled, config);
            mcp.WithResources<RevitResources>();

            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

            var app = builder.Build();

            app.Use(async (context, next) =>
            {
                var host = context.Request.Host.Host;
                if (host != "127.0.0.1" && host != "localhost")
                {
                    context.Response.StatusCode = 403;
                    context.Response.ContentType = "text/plain";
                    await context.Response.WriteAsync("Forbidden: non-localhost host");
                    return;
                }
                await next();
            });

            app.MapMcp();

            Console.Error.WriteLine($"[RvtMcp] SSE server listening on http://127.0.0.1:{port}");
            Console.Error.WriteLine($"[RvtMcp] Toolsets enabled: {string.Join(",", enabled.OrderBy(n => n))}");
            await app.RunAsync();
        }

        private static void PrintHelp()
        {
            var usage = string.Join("\n", new[]
            {
                "rvt-mcp — Revit MCP server (bimwright.dev)",
                "",
                "Usage: rvt-mcp [options]",
                "",
                "Transport:",
                "  --http <port>           Run HTTP SSE on 127.0.0.1:<port> (1-65535). Default = stdio.",
                "",
                "Routing:",
                "  --target 2022|2023|2024|2025|2026|2027",
                "                          Pin to a specific Revit calendar-year version when multiple",
                "                          Revits run. Use the 4-digit year — legacy R-codes (R22..R27)",
                "                          are rejected in v0.5+.",
                "                          Default: auto-detect via revit-YYYY.json files in",
                "                          %LOCALAPPDATA%\\RvtMcp\\.",
                "",
                "Tool exposure (A3 Progressive Disclosure):",
                "  --toolsets <csv>        Comma list of toolsets to enable. Default: " + string.Join(",", ToolsetFilter.DefaultOn) + ".",
                "                          Known toolsets: " + string.Join(", ", ToolsetFilter.KnownToolsets),
                "                          Use 'all' to expose every toolset.",
                "  --read-only             Shortcut that strips every configured write-capable toolset.",
                "",
                "ToolBaker:",
                "  --enable-toolbaker      Allow ToolBaker tools (default ON).",
                "  --disable-toolbaker     Disable ToolBaker tools.",
                "  --enable-adaptive-bake  Enable adaptive ToolBaker suggestions (default OFF).",
                "  --disable-adaptive-bake Disable adaptive ToolBaker suggestions.",
                "  --cache-send-code-bodies",
                "                          Cache send_code_to_revit code bodies locally (default OFF).",
                "  --no-cache-send-code-bodies",
                "                          Disable local send_code_to_revit code body caching.",
                "  --persist-send-code-bodies",
                "                          Opt-in TTL journal for send_code bodies (default OFF).",
                "                          Stamps %LOCALAPPDATA%\\RvtMcp\\rvtmcp.config.json so the",
                "                          Revit plugin (sole journal writer) can see the window.",
                "  --persist-send-code-bodies-for <ttl>",
                "                          Duration for the journal window (1h–2d; e.g. 4h, 2d, 90m).",
                "                          Out-of-range values are clamped. Default 4h.",
                "  --no-persist-send-code-bodies",
                "                          Disable send_code body journal and clear the JSON stamp.",
                "",
                "Transport security (S7):",
                "  --allow-lan-bind        (plugin-side only — set BIMWRIGHT_ALLOW_LAN_BIND env var in",
                "                          the Revit process environment; server-side flag is documented",
                "                          here for future cross-process propagation.)",
                "",
                "Env vars (override JSON, overridden by CLI):",
                "  BIMWRIGHT_TARGET, BIMWRIGHT_TOOLSETS, BIMWRIGHT_READ_ONLY,",
                "  BIMWRIGHT_ALLOW_LAN_BIND, BIMWRIGHT_ENABLE_TOOLBAKER,",
                "  BIMWRIGHT_ENABLE_ADAPTIVE_BAKE, BIMWRIGHT_CACHE_SEND_CODE_BODIES,",
                "  BIMWRIGHT_PERSIST_SEND_CODE_BODIES, BIMWRIGHT_PERSIST_SEND_CODE_BODIES_TTL",
                "  (Persist env stamps JSON on first enable; after TTL expiry sticky env=1 does",
                "   NOT revive — use --persist-send-code-bodies to re-enable explicitly.)",
                "",
                "Config file (lowest precedence):",
                "  %LOCALAPPDATA%\\RvtMcp\\rvtmcp.config.json",
                "",
                "Other:",
                "  -h, --help              Show this help and exit.",
            });
            Console.WriteLine(usage);
        }

        // MCP InitializeResult metadata. ServerInstructions is the single most important
        // signal for Tool Search discoverability — Claude Code/Desktop's Tool Search ranks
        // servers by (1) instructions, (2) literal tool name. Without this populated, an
        // agent asking "list Revit tools" returns nothing even though 224 tools are exposed.
        // Anthropic truncates this field at 2KB; the keyword-dense first paragraph carries
        // the discoverability load if the SDK or proxy truncates later.
        private static void ConfigureMcpServerOptions(ModelContextProtocol.Server.McpServerOptions opts)
        {
            opts.ServerInfo = new ModelContextProtocol.Protocol.Implementation
            {
                Name = "rvt-mcp",
                Title = "Revit MCP",
                Version = "0.6.1",
                Description = "Model Context Protocol gateway for Autodesk Revit 2022-2027",
                WebsiteUrl = "https://github.com/bimwright/rvt-mcp"
            };
            opts.ServerInstructions = ServerInstructionsText;
        }

        // Anthropic Tool Search truncates this at 2 KB; the constant below is kept
        // under 2048 UTF-8 bytes. Lead with the keyword paragraph (highest discriminative
        // signal for queries like "list Revit tools"), then a compact toolset-name index
        // — 2 examples per toolset — so semantic search for individual ops still resolves.
        private const string ServerInstructionsText =
@"rvt-mcp — MCP gateway for Autodesk Revit 2022-2027. Use whenever user works with .rvt, Revit, BIM, walls, doors, windows, floors, ceilings, roofs, levels, grids, rooms, sheets, schedules, families, views, view templates, view filters, MEP (ducts, pipes, trays, conduits, HVAC, lighting, plumbing), structural (columns, beams, foundations, rebar), dimensions, tags, annotations, keynotes, worksets, phases, links, parameters, materials, IFC, DWG, NWC, PDF.

Multi-Revit: if >1 Revit may be open, call revit_list_available_targets THEN revit_switch_target. Years are 2022-2027, not R-codes. Defaults: query,create,view,meta. --toolsets all for export/clash. Do not retry clash/export after 60s timeout.

Tools (prefix revit_<verb>_<noun>, lengths in mm):
- query: get_current_view_info, ai_element_filter, get_element_details
- create: create_grid, create_level, create_room
- modify: operate_element, set_element_parameter_values
- delete: delete_element
- view: create_view, capture_view_image
- sheets: create_sheet, renumber_sheets
- schedule: create_schedule, list_schedules
- families: list_loaded_families, load_family_from_path
- mep: create_duct, create_pipe, analyze_mep_network
- annotation: tag_elements, create_dimensions
- graphics: create_view_filter, override_element_graphics
- export: export_pdf, export_dwg, export_ifc, export_nwc
- materials: list_materials, assign_material_to_element
- geometry: clash_detection, measure_distance_between_elements
- rooms: list_rooms, compute_room_finishes
- links: project coordinates, link/acquire/publish
- parameters: create_shared_parameter, create_project_parameter
- organization: apply_view_template, save_selection
- workflows: workflow_clash_review, workflow_model_audit
- structural: create_structural_column, create_rebar_set
- kei: query_kei_database, import_project_equipment
- meta: send_code_to_revit, batch_execute, list_available_targets, switch_target
- lint: find_untagged_elements, get_model_warnings_summary
- toolbaker: list_baked_tools, run_baked_tool";

        private static bool IncludeSendCode(HashSet<string> enabled, RvtMcpConfig config)
        {
            var bakerOn = config == null || config.EnableToolbakerOrDefault;
            var writable = config == null || !config.ReadOnlyOrDefault;
            return bakerOn && writable
                && (enabled.Contains("meta") || enabled.Contains("toolbaker"));
        }

        private static IMcpServerBuilder RegisterToolsets(IMcpServerBuilder mcp, HashSet<string> enabled, RvtMcpConfig config)
        {
            if (enabled.Contains("query"))      mcp = mcp.WithTools<QueryTools>();
            if (enabled.Contains("create"))     mcp = mcp.WithTools<CreateTools>();
            if (enabled.Contains("modify"))     mcp = mcp.WithTools<ModifyTools>();
            if (enabled.Contains("delete"))     mcp = mcp.WithTools<DeleteTools>();
            if (enabled.Contains("view"))       mcp = mcp.WithTools<ViewTools>();
            if (enabled.Contains("schedule"))   mcp = mcp.WithTools<ScheduleTools>();
            if (enabled.Contains("families"))   mcp = mcp.WithTools<FamiliesTools>();
            if (enabled.Contains("graphics"))   mcp = mcp.WithTools<GraphicsTools>();
            if (enabled.Contains("export"))     mcp = mcp.WithTools<ExportTools>();
            if (enabled.Contains("annotation")) mcp = mcp.WithTools<AnnotationTools>();
            if (enabled.Contains("mep"))        mcp = mcp.WithTools<MepTools>();
            if (enabled.Contains("sheets"))      mcp = mcp.WithTools<SheetsTools>();
            if (enabled.Contains("materials"))   mcp = mcp.WithTools<MaterialsTools>();
            if (enabled.Contains("geometry"))    mcp = mcp.WithTools<GeometryTools>();
            if (enabled.Contains("rooms"))       mcp = mcp.WithTools<RoomsTools>();
            if (enabled.Contains("links"))       mcp = mcp.WithTools<LinksTools>();
            if (enabled.Contains("parameters"))   mcp = mcp.WithTools<ParametersTools>();
            if (enabled.Contains("organization")) mcp = mcp.WithTools<OrganizationTools>();
            if (enabled.Contains("workflows"))    mcp = mcp.WithTools<WorkflowsTools>();
            if (enabled.Contains("toolbaker"))  mcp = mcp.WithTools<ToolbakerTools>();
            if (enabled.Contains("toolbaker") && config?.EnableAdaptiveBakeOrDefault == true)
                mcp = mcp.WithTools<AdaptiveBakeTools>();
            if (IncludeSendCode(enabled, config)) mcp = mcp.WithTools<SendCodeTools>();
            if (enabled.Contains("meta"))       mcp = mcp.WithTools<MetaTools>();
            if (enabled.Contains("lint"))       mcp = mcp.WithTools<LintTools>();
            if (enabled.Contains("structural")) mcp = mcp.WithTools<StructuralTools>();
            if (enabled.Contains("kei"))        mcp = mcp.WithTools<KeiTools>();
            return mcp;
        }

        private static Type[] ResolveRegisteredToolTypes(HashSet<string> enabled, RvtMcpConfig config)
        {
            var types = new List<Type>();
            if (enabled.Contains("query"))      types.Add(typeof(QueryTools));
            if (enabled.Contains("create"))     types.Add(typeof(CreateTools));
            if (enabled.Contains("modify"))     types.Add(typeof(ModifyTools));
            if (enabled.Contains("delete"))     types.Add(typeof(DeleteTools));
            if (enabled.Contains("view"))       types.Add(typeof(ViewTools));
            if (enabled.Contains("schedule"))   types.Add(typeof(ScheduleTools));
            if (enabled.Contains("families"))   types.Add(typeof(FamiliesTools));
            if (enabled.Contains("graphics"))   types.Add(typeof(GraphicsTools));
            if (enabled.Contains("export"))     types.Add(typeof(ExportTools));
            if (enabled.Contains("annotation")) types.Add(typeof(AnnotationTools));
            if (enabled.Contains("mep"))        types.Add(typeof(MepTools));
            if (enabled.Contains("sheets"))      types.Add(typeof(SheetsTools));
            if (enabled.Contains("materials"))   types.Add(typeof(MaterialsTools));
            if (enabled.Contains("geometry"))    types.Add(typeof(GeometryTools));
            if (enabled.Contains("rooms"))       types.Add(typeof(RoomsTools));
            if (enabled.Contains("links"))       types.Add(typeof(LinksTools));
            if (enabled.Contains("parameters"))   types.Add(typeof(ParametersTools));
            if (enabled.Contains("organization")) types.Add(typeof(OrganizationTools));
            if (enabled.Contains("workflows"))    types.Add(typeof(WorkflowsTools));
            if (enabled.Contains("toolbaker"))  types.Add(typeof(ToolbakerTools));
            if (enabled.Contains("toolbaker") && config?.EnableAdaptiveBakeOrDefault == true)
                types.Add(typeof(AdaptiveBakeTools));
            if (IncludeSendCode(enabled, config)) types.Add(typeof(SendCodeTools));
            if (enabled.Contains("meta"))       types.Add(typeof(MetaTools));
            if (enabled.Contains("lint"))       types.Add(typeof(LintTools));
            if (enabled.Contains("structural")) types.Add(typeof(StructuralTools));
            if (enabled.Contains("kei"))        types.Add(typeof(KeiTools));
            return types.ToArray();
        }
    }

    /// <summary>
    /// Shared plugin-connection plumbing used by every toolset class. Owns the socket/
    /// pipe lifecycle, response read loop, pending-request correlation, and session
    /// call recording. Toolset classes contain only the MCP tool-method shells.
    /// </summary>
    internal static class ToolGateway
    {
        public static Memory.SessionContext Session { get; set; }
        public static UsageEventLogger UsageLogger { get; set; }
        public static string CurrentRevitVersion { get; private set; }

        private static TcpClient _client;
        private static NamedPipeClientStream _pipeStream;
        private static StreamReader _reader;
        private static StreamWriter _writer;
        private static readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new ConcurrentDictionary<string, TaskCompletionSource<string>>();
        private static readonly object _connectLock = new object();
        private static readonly JsonSerializerSettings RequestJsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        };
        private static volatile bool _connected;
        private static string _token;

        private static void EnsureConnected()
        {
            if (_connected && (_client?.Connected == true || _pipeStream?.IsConnected == true))
                return;

            lock (_connectLock)
            {
                if (_connected && (_client?.Connected == true || _pipeStream?.IsConnected == true))
                    return;

                _connected = false;
                try { _client?.Close(); } catch { }
                try { _pipeStream?.Close(); } catch { }
                _client = null;
                _pipeStream = null;

                Stream stream = null;

                var target = AuthToken.Target; // null = auto, "2022"-"2027" = specific version

                // Try Named Pipe first (R25-R27).
                // If the discovery file exists but the connect itself fails (plugin unloaded
                // while Revit stayed alive, or some transient state), fall through to TCP
                // rather than giving up the whole connection attempt.
                if (AuthToken.TryReadPipe(out var pipeName, out var pipeToken, out var pipeVer))
                {
                    try
                    {
                        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                            PipeOptions.Asynchronous);
                        pipe.Connect(5000);
                        _token = pipeToken;
                        CurrentRevitVersion = pipeVer;
                        _pipeStream = pipe;
                        stream = pipe;
                        Console.Error.WriteLine($"[RvtMcp] Connected to Revit {pipeVer} via Named Pipe: {pipeName}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[RvtMcp] Pipe connect failed ({pipeVer}: {ex.Message}) — falling back to TCP");
                        try { _pipeStream?.Close(); } catch { }
                        _pipeStream = null;
                    }
                }

                // Fall back to TCP (R22-R24) if pipe did not connect.
                if (stream == null && AuthToken.TryReadTcp(out var port, out var tcpToken, out var tcpVer))
                {
                    _token = tcpToken;
                    CurrentRevitVersion = tcpVer;
                    _client = new TcpClient();
                    _client.Connect("127.0.0.1", port);
                    stream = _client.GetStream();
                    Console.Error.WriteLine($"[RvtMcp] Connected to Revit {tcpVer} via TCP on port {port}");
                }

                if (stream == null)
                {
                    var which = target != null ? $"(target={target})" : "(auto-detect R22-R27)";
                    throw new InvalidOperationException(
                        $"Revit MCP plugin not running {which}. Check discovery files in %LOCALAPPDATA%\\RvtMcp\\");
                }

                _reader = new StreamReader(stream, Encoding.UTF8);
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                _connected = true;

                var readThread = new Thread(ReadLoop) { IsBackground = true, Name = "RvtMcp.ResponseReader" };
                readThread.Start();
            }
        }

        private static void ReadLoop()
        {
            try
            {
                while (_connected)
                {
                    var line = _reader?.ReadLine();
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        var obj = JObject.Parse(line);
                        var id = obj.Value<string>("id");
                        if (id != null && _pending.TryRemove(id, out var tcs))
                        {
                            tcs.TrySetResult(line);
                        }
                    }
                    catch { }
                }
            }
            catch { }
            finally
            {
                _connected = false;
            }
        }

        /// <summary>
        /// Close the current Server↔Plugin connection and set a new target version.
        /// Next <see cref="SendToRevit"/> call will reconnect against the new target.
        /// Pass <c>null</c> to clear the pin and re-enable auto-detect.
        /// Cancels any in-flight requests — they'd be routed to the now-dead connection.
        /// </summary>
        public static void Reconnect(string newTarget)
        {
            lock (_connectLock)
            {
                _connected = false;
                try { _client?.Close(); } catch { }
                try { _pipeStream?.Close(); } catch { }
                _client = null;
                _pipeStream = null;
                _reader = null;
                _writer = null;
                _token = null;
                CurrentRevitVersion = null;
                foreach (var kv in _pending)
                {
                    kv.Value.TrySetException(new OperationCanceledException(
                        "switch_target initiated — in-flight request cancelled."));
                }
                _pending.Clear();
                AuthToken.Target = newTarget;
            }
        }

        public static async Task<JObject> SendToRevit(string command, object parameters = null)
        {
            EnsureConnected();

            var id = $"req-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid().ToString("N").Substring(0, 6)}";
            var request = JsonConvert.SerializeObject(new { id, command, @params = parameters ?? new { }, token = _token }, RequestJsonSettings);

            var tcs = new TaskCompletionSource<string>();
            _pending[id] = tcs;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            _writer.WriteLine(request);

            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(60));
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                _pending.TryRemove(id, out _);
                sw.Stop();
                var paramsStr = parameters != null ? JsonConvert.SerializeObject(parameters, RequestJsonSettings) : null;
                Session?.RecordCall(command, paramsStr, false, sw.ElapsedMilliseconds, "Timeout (60s)");
                UsageLogger?.RecordToolCall(command, paramsStr, false);
                throw new TimeoutException("Request timed out (60s). Revit may be in a modal dialog.");
            }

            sw.Stop();
            var responseLine = await tcs.Task;
            var response = JObject.Parse(responseLine);
            var paramsJson = parameters != null ? JsonConvert.SerializeObject(parameters, RequestJsonSettings) : null;

            if (response.Value<bool>("success"))
            {
                var data = response["data"] as JObject ?? new JObject();
                var responseWarning = response.Value<string>("warning");
                if (!string.IsNullOrWhiteSpace(responseWarning))
                    data["_response_warning"] = responseWarning;
                Session?.RecordCall(command, paramsJson, true, sw.ElapsedMilliseconds,
                    resultJson: data.ToString(Formatting.None));
                UsageLogger?.RecordToolCall(command, paramsJson, true);
                return data;
            }
            else
            {
                var error = response.Value<string>("error") ?? "Unknown error from Revit";
                Session?.RecordCall(command, paramsJson, false, sw.ElapsedMilliseconds, error);
                UsageLogger?.RecordToolCall(command, paramsJson, false);
                throw new InvalidOperationException(error);
            }
        }
    }

    // =====================================================================
    // Toolset classes — one per aspect #3 §A3 group. Registration happens in
    // Program.RegisterToolsets() driven by config.Toolsets. Each method wraps
    // ToolGateway.SendToRevit with a catch-all that surfaces the error to the
    // MCP client as plain text instead of throwing.
    // =====================================================================

    [McpServerToolType, Toolset("query")]
    public class QueryTools
    {
        [McpServerTool(Name = "revit_get_current_view_info", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Get active view info. Returns viewName, viewType (FloorPlan/Section/3D/Sheet), level, scale, detailLevel, displayStyle. Call before creating elements to know active level.")]
        public static async Task<string> GetCurrentViewInfo()
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_current_view_info");
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_selected_elements", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Get currently selected Revit elements with paging. Use startIndex + maxResults (default 200, hard max 1000).")]
        public static async Task<string> GetSelectedElements(int startIndex = 0, int maxResults = 200)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_selected_elements", new { start_index = startIndex, max_results = maxResults });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_available_family_types", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List loadable family types. Returns {familyName, typeName, typeId} grouped by category. Optional: filter by category (e.g. 'Walls', 'Doors', 'Pipes' — NOT 'OST_Walls'). Feed typeId into create_point_based_element.")]
        public static async Task<string> GetAvailableFamilyTypes(string category = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_available_family_types", new { category });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_ai_element_filter", Destructive = false, Idempotent = true), System.ComponentModel.Description("Filter elements by category + parameter. Numeric values in mm (auto-converted). category uses human name ('Pipes', NOT 'OST_Pipes'). Operators: equals/contains/startswith/greaterthan/lessthan. select=true highlights results. Example: category='Pipes', parameterName='Diameter', parameterValue='200', operator='greaterthan', select=true.")]
        public static async Task<string> AiElementFilter(string category, string parameterName = "", string parameterValue = "", string @operator = "equals", int limit = 100, bool select = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("ai_element_filter", new { category, parameterName, parameterValue, @operator, limit, select });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_analyze_model_statistics", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Count elements grouped by category (Walls, Doors, Pipes, etc.). Call to understand project scope before detailed queries.")]
        public static async Task<string> AnalyzeModelStatistics()
        {
            try
            {
                var result = await ToolGateway.SendToRevit("analyze_model_statistics");
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_material_quantities", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Sum material quantities (area m², volume m³) by category. Narrow with materialNameFilter and page with startIndex + maxResults (hard max 1000).")]
        public static async Task<string> GetMaterialQuantities(string category, string materialNameFilter = "", int startIndex = 0, int maxResults = 200)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_material_quantities", new { category, material_name_filter = materialNameFilter, start_index = startIndex, max_results = maxResults });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_element_details", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Read detailed metadata for one or more elements. Returns identity, category, type, level, workset, phase, owner view, design option, group/assembly ids, location, and bounding box in mm.")]
        public static async Task<string> GetElementDetails(long[] elementIds)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_element_details", new { elementIds });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_element_parameters", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Read instance parameters for one or more elements. Returns storage type, read-only state, display value, raw value, and data/spec ids.")]
        public static async Task<string> GetElementParameters(long[] elementIds, bool includeReadOnly = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_element_parameters", new { elementIds, includeReadOnly });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_type_parameters", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Read type parameters from explicit type ids or from the types of provided element ids.")]
        public static async Task<string> GetTypeParameters(long[] elementIds = null, long[] typeIds = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_type_parameters", new { elementIds, typeIds });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_project_parameters", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List project/shared parameter bindings, including instance/type binding kind and bound categories.")]
        public static async Task<string> ListProjectParameters(bool includeCategories = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_project_parameters", new { includeCategories });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_element_relationships", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Read host, group, assembly, owner view, design option, family nesting, and dependent-element relationships for elements.")]
        public static async Task<string> GetElementRelationships(long[] elementIds, bool includeDependents = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_element_relationships", new { elementIds, includeDependents });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_groups", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List model/detail/attached groups with type, owner view, parent, and optional member ids.")]
        public static async Task<string> ListGroups(string groupKind = "all", bool includeMembers = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_groups", new { groupKind, includeMembers });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_group_members", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Read a group instance and a page of member elements. Use startIndex + maxResults (hard max 1000).")]
        public static async Task<string> GetGroupMembers(long groupId, int startIndex = 0, int maxResults = 200)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_group_members", new { groupId, start_index = startIndex, max_results = maxResults });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_assemblies", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List a page of assembly instances. maxMembersPerAssembly caps optional member-id previews.")]
        public static async Task<string> ListAssemblies(bool includeMembers = false, int startIndex = 0, int maxResults = 100, int maxMembersPerAssembly = 50)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_assemblies", new { includeMembers, start_index = startIndex, max_results = maxResults, max_members_per_assembly = maxMembersPerAssembly });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_assembly_members", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Read an assembly instance and a page of members. Use startIndex + maxResults (hard max 1000).")]
        public static async Task<string> GetAssemblyMembers(long assemblyId, int startIndex = 0, int maxResults = 200)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_assembly_members", new { assemblyId, start_index = startIndex, max_results = maxResults });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_worksets", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List document worksets and active workset. Optionally includes per-workset element counts.")]
        public static async Task<string> ListWorksets(bool includeElementCounts = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_worksets", new { includeElementCounts });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("schedule")]
    public class ScheduleTools
    {
        [McpServerTool(Name = "revit_list_schedules", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List all schedules in the project. Optional filters: categoryFilter (case-insensitive substring on resolved category name), namePattern (case-insensitive substring on schedule name).")]
        public static async Task<string> ListSchedules(string categoryFilter = "", string namePattern = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_schedules", new { categoryFilter, namePattern });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_schedule_definition", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Get the full structural definition of a schedule: fields (parameter/formula/combined), filters, sort/group, and settings. Identify schedule by `scheduleId` (long) or `scheduleName`.")]
        public static async Task<string> GetScheduleDefinition(long? scheduleId = null, string scheduleName = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_schedule_definition", new { scheduleId, scheduleName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_schedule_data", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Get the rendered tabular content of a schedule (header row + body rows) with pagination. Optional cell metadata (cell type + merged cells).")]
        public static async Task<string> GetScheduleData(long? scheduleId = null, string scheduleName = "", int startRow = 0, int maxRows = 200, bool includeCellMeta = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_schedule_data", new { scheduleId, scheduleName, startRow, maxRows, includeCellMeta });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_schedule_formulas", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Extract all calculated (formula) and combined-parameter fields from a schedule, with parsed formula dependencies. Useful for auditing, debugging, or copying formulas between schedules.")]
        public static async Task<string> GetScheduleFormulas(long? scheduleId = null, string scheduleName = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_schedule_formulas", new { scheduleId, scheduleName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_schedulable_fields", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List parameters that CAN be added as fields to a schedule but have not been added yet. Pre-step for add_schedule_field — call this to discover valid parameter names.")]
        public static async Task<string> GetSchedulableFields(long? scheduleId = null, string scheduleName = "", string[] kindFilter = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_schedulable_fields", new { scheduleId, scheduleName, kindFilter });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_find_schedule_elements", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Find Revit elements aggregated by a schedule (using FilteredElementCollector scoped to the schedule's id). Returns count grouped by category and per-element {id, name, category, typeName}. Optional includeParameters returns each element's visible parameters with unit-corrected values.")]
        public static async Task<string> FindScheduleElements(long? scheduleId = null, string scheduleName = "", bool groupByCategory = true, bool includeParameters = false, int limit = 500)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("find_schedule_elements", new { scheduleId, scheduleName, groupByCategory, includeParameters, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_schedule", Destructive = false), System.ComponentModel.Description("Create a new schedule from a declarative spec. Supports three field kinds in one transaction: parameter (existing Revit param), formula (calculated value field), and combined (concatenated parameters with separators). Optional filters, sort/group, and isItemized.")]
        public static async Task<string> CreateSchedule(string category, string name, string fields, string filters = null, string sortGroup = null, bool isItemized = true)
        {
            try
            {
                var parsedFields = JArray.Parse(fields);
                var parsedFilters = string.IsNullOrWhiteSpace(filters) ? null : JArray.Parse(filters);
                var parsedSortGroup = string.IsNullOrWhiteSpace(sortGroup) ? null : JArray.Parse(sortGroup);
                var result = await ToolGateway.SendToRevit("create_schedule", new { category, name, fields = parsedFields, filters = parsedFilters, sortGroup = parsedSortGroup, isItemized });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_add_schedule_field", Destructive = false), System.ComponentModel.Description("Add one new field to an existing schedule. Supports parameter, formula, or combined-parameter kinds via a discriminated-union spec. Optional insertIndex, columnHeading, hidden, columnWidth (mm).")]
        public static async Task<string> AddScheduleField(string field, long? scheduleId = null, string scheduleName = "", int? insertIndex = null, string columnHeading = "", bool hidden = false, double? columnWidth = null)
        {
            try
            {
                var parsedField = JObject.Parse(field);
                var result = await ToolGateway.SendToRevit("add_schedule_field", new { scheduleId, scheduleName, field = parsedField, insertIndex, columnHeading, hidden, columnWidth });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_update_schedule_field", Destructive = false), System.ComponentModel.Description("Modify an existing schedule field's properties: columnHeading, hidden, columnWidth, horizontalAlignment, headingOrientation, formula (only if calculated), combinedParameters (only if combined), isTotal, isPercentage, displayType. Cannot change the underlying parameter of a parameter field — use remove + add instead.")]
        public static async Task<string> UpdateScheduleField(string fieldRef, string changes, long? scheduleId = null, string scheduleName = "")
        {
            try
            {
                var parsedFieldRef = JObject.Parse(fieldRef);
                var parsedChanges = JObject.Parse(changes);
                var result = await ToolGateway.SendToRevit("update_schedule_field", new { scheduleId, scheduleName, fieldRef = parsedFieldRef, changes = parsedChanges });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_apply_schedule_filter_sort", Destructive = false), System.ComponentModel.Description("Partially update a schedule's filters, sort/group, and settings. filters/sortGroup replace only when supplied; omitted sections are preserved.")]
        public static async Task<string> ApplyScheduleFilterSort(long? scheduleId = null, string scheduleName = "", string filters = null, string sortGroup = null, string settings = null)
        {
            try
            {
                var parsedFilters = string.IsNullOrWhiteSpace(filters) ? null : JArray.Parse(filters);
                var parsedSortGroup = string.IsNullOrWhiteSpace(sortGroup) ? null : JArray.Parse(sortGroup);
                var parsedSettings = string.IsNullOrWhiteSpace(settings) ? null : JObject.Parse(settings);
                var result = await ToolGateway.SendToRevit("apply_schedule_filter_sort", new { scheduleId, scheduleName, filters = parsedFilters, sortGroup = parsedSortGroup, settings = parsedSettings });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("families")]
    public class FamiliesTools
    {
        [McpServerTool(Name = "revit_list_loaded_families", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List all loaded families (loadable + in-place + system) in the active document grouped by category. Returns id, name, category, kind (system|loadable|inplace), type_count, optional instance_count, and is_editable. Filter via categoryFilter (case-insensitive substring) and kindFilter (all|system|loadable|inplace).")]
        public static async Task<string> ListLoadedFamilies(string categoryFilter = "", string kindFilter = "all", bool includeInstanceCount = false, int limit = 1000)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_loaded_families", new { category_filter = categoryFilter, kind_filter = kindFilter, include_instance_count = includeInstanceCount, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_load_family_from_path", Destructive = false), System.ComponentModel.Description("Load an .rfa family. Response is compact by default; set includeSymbols=true for a bounded symbol preview (maxSymbolResults hard max 1000).")]
        public static async Task<string> LoadFamilyFromPath(string path, bool overwriteExisting = true, bool overwriteParameterValues = false, bool includeSymbols = false, int maxSymbolResults = 200)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("load_family_from_path", new { path, overwrite_existing = overwriteExisting, overwrite_parameter_values = overwriteParameterValues, include_symbols = includeSymbols, max_symbol_results = maxSymbolResults });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_unload_family", Destructive = true), System.ComponentModel.Description("Remove (purge) a loadable family from the document. Identify by familyId or familyName. cascadeDeleteInstances=true to also delete placed instances; otherwise error if instances exist. dryRun=true returns the projected effect without changing the model. System families cannot be unloaded.")]
        public static async Task<string> UnloadFamily(long? familyId = null, string familyName = "", bool cascadeDeleteInstances = false, bool dryRun = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("unload_family", new { family_id = familyId, family_name = familyName, cascade_delete_instances = cascadeDeleteInstances, dry_run = dryRun });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_duplicate_family_type", Destructive = false), System.ComponentModel.Description("Duplicate a FamilySymbol or system type within its family under newTypeName, optionally setting type parameter overrides (JSON object as string, parameter name → value). Returns the new type id. Works for FamilySymbol and ElementType subclasses (WallType, FloorType, etc.).")]
        public static async Task<string> DuplicateFamilyType(long sourceTypeId, string newTypeName, string typeParameterOverrides = "")
        {
            try
            {
                var parsedOverrides = string.IsNullOrWhiteSpace(typeParameterOverrides) ? null : JObject.Parse(typeParameterOverrides);
                var result = await ToolGateway.SendToRevit("duplicate_family_type", new { source_type_id = sourceTypeId, new_type_name = newTypeName, type_parameter_overrides = parsedOverrides });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_rename_family_type", Destructive = false), System.ComponentModel.Description("Rename a FamilySymbol or system type. Must be unique within the family. Catches Autodesk.Revit.Exceptions.ArgumentException for duplicate/invalid names and returns a clean error DTO without throwing.")]
        public static async Task<string> RenameFamilyType(long typeId, string newName)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("rename_family_type", new { type_id = typeId, new_name = newName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_audit_families", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Read-only family audit with bounded sections. Page each section with startIndex + limitPerSection (hard max 500).")]
        public static async Task<string> AuditFamilies(bool includeUnused = true, bool includeInplace = true, bool includeDuplicateNames = true, bool includeHighTypeCount = true, int highTypeCountThreshold = 20, int startIndex = 0, int limitPerSection = 100)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("audit_families", new { include_unused = includeUnused, include_inplace = includeInplace, include_duplicate_names = includeDuplicateNames, include_high_type_count = includeHighTypeCount, high_type_count_threshold = highTypeCountThreshold, start_index = startIndex, limit_per_section = limitPerSection });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_replace_family_type", Destructive = false), System.ComponentModel.Description("Replace all instances of FamilySymbol A with FamilySymbol B across the project, active view, or selection. Both types must be the same category. dryRun=true previews counts without changing the model. Target symbol is auto-activated.")]
        public static async Task<string> ReplaceFamilyType(long fromTypeId, long toTypeId, string scope = "all", long? viewId = null, bool dryRun = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("replace_family_type", new { from_type_id = fromTypeId, to_type_id = toTypeId, scope, view_id = viewId, dry_run = dryRun });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_family_instances", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List placed instances of a Family (or a specific type within it) with location/host/level DTOs in mm. viewOnly=true restricts to the active view. Returns location_kind (point|line|null), coordinates in mm, host_id/name, mark.")]
        public static async Task<string> GetFamilyInstances(long? familyId = null, string familyName = "", string typeName = "", bool viewOnly = false, int limit = 1000)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_family_instances", new { family_id = familyId, family_name = familyName, type_name = typeName, view_only = viewOnly, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_family_types_in_family", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List a bounded page of types in one family. Use parameterNames to return only needed values; maxTypes hard max 500.")]
        public static async Task<string> ListFamilyTypesInFamily(long? familyId = null, string familyName = "", bool includeParameterValues = true, bool includeBuiltInOnly = false, int startIndex = 0, int maxTypes = 100, string[] parameterNames = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_family_types_in_family", new { family_id = familyId, family_name = familyName, include_parameter_values = includeParameterValues, include_built_in_only = includeBuiltInOnly, start_index = startIndex, max_types = maxTypes, parameter_names = parameterNames });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_export_family_to_path", Destructive = false), System.ComponentModel.Description("Save a loadable family from the current project back to an .rfa file at outputPath. Writes to disk (not ReadOnly). Rejects in-place and system families. overwriteExisting=false errors if the file already exists. Uses doc.EditFamily + Document.SaveAs.")]
        public static async Task<string> ExportFamilyToPath(string outputPath, long? familyId = null, string familyName = "", bool overwriteExisting = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_family_to_path", new { family_id = familyId, family_name = familyName, output_path = outputPath, overwrite_existing = overwriteExisting });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("create")]
    public class CreateTools
    {
        [McpServerTool(Name = "revit_create_line_based_element", Destructive = false), System.ComponentModel.Description("Create a line-based element (wall). Params: elementType, startX/Y, endX/Y (mm), level (name), typeId (optional), height (mm, default 3000).")]
        public static async Task<string> CreateLineBasedElement(string elementType, double startX, double startY, double endX, double endY, string level = "", long? typeId = null, double height = 3000)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_line_based_element", new { elementType, startX, startY, endX, endY, level, typeId, height });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_point_based_element", Destructive = false), System.ComponentModel.Description("Create a point-based element (door, window, furniture). Params: typeId (from get_available_family_types), x/y/z (mm), level (name).")]
        public static async Task<string> CreatePointBasedElement(long typeId, double x, double y, double z = 0, string level = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_point_based_element", new { typeId, x, y, z, level });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_surface_based_element", Destructive = false), System.ComponentModel.Description("Create a surface-based element (floor, ceiling). Params: elementType, points (JSON array of {x,y} in mm, min 3), level (name), typeId (optional). Example points: [{\"x\":0,\"y\":0},{\"x\":6000,\"y\":0},{\"x\":6000,\"y\":4000},{\"x\":0,\"y\":4000}].")]
        public static async Task<string> CreateSurfaceBasedElement(string elementType, string points, string level = "", long? typeId = null)
        {
            try
            {
                var parsedPoints = JArray.Parse(points);
                var result = await ToolGateway.SendToRevit("create_surface_based_element", new { elementType, points = parsedPoints, level, typeId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_level", Destructive = false), System.ComponentModel.Description("Create a level at specified elevation. Params: elevation (mm), name (optional).")]
        public static async Task<string> CreateLevel(double elevation, string name = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_level", new { elevation, name });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_grid", Destructive = false), System.ComponentModel.Description("Create a grid line. Params: startX/Y, endX/Y (mm), name (optional).")]
        public static async Task<string> CreateGrid(double startX, double startY, double endX, double endY, string name = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_grid", new { startX, startY, endX, endY, name });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_room", Destructive = false), System.ComponentModel.Description("Create and place a room. Params: x/y (mm), level (name), name (optional), number (optional).")]
        public static async Task<string> CreateRoom(double x, double y, string level = "", string name = "", string number = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_room", new { x, y, level, name, number });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_group_from_elements", Destructive = false), System.ComponentModel.Description("Create a Revit group from two or more element ids. Optional name renames the generated group type.")]
        public static async Task<string> CreateGroupFromElements(long[] elementIds, string name = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_group_from_elements", new { elementIds, name });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("modify")]
    public class ModifyTools
    {
        [McpServerTool(Name = "revit_operate_element", Destructive = false), System.ComponentModel.Description("Select/hide/isolate/color elements in current view. operation: select (highlight), hide, unhide, isolate (hide everything else), setcolor (RGB override). elementIds: JSON int array e.g. '[12345, 67890]'. For setcolor: r/g/b 0-255 (default red 255,0,0).")]
        public static async Task<string> OperateElement(string operation, string elementIds, byte r = 255, byte g = 0, byte b = 0)
        {
            try
            {
                var parsedIds = JArray.Parse(elementIds).ToObject<long[]>();
                var result = await ToolGateway.SendToRevit("operate_element", new { operation, elementIds = parsedIds, r, g, b });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_color_elements", Destructive = false, Idempotent = true), System.ComponentModel.Description("Color-code all matching elements by parameter value. maxGroups caps response detail only; mutation summary remains complete.")]
        public static async Task<string> ColorElements(string category, string parameterName, int maxGroups = 100)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("color_elements", new { category, parameterName, max_groups = maxGroups });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_set_element_parameter_values", Destructive = false), System.ComponentModel.Description("Set an instance parameter on multiple elements. valueType can be auto/string/integer/double/elementId; length-like doubles use mm input.")]
        public static async Task<string> SetElementParameterValues(long[] elementIds, string parameterName, string value, string valueType = "auto")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_element_parameter_values", new { elementIds, parameterName, value, valueType });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_set_type_parameter_values", Destructive = false), System.ComponentModel.Description("Set a type parameter on explicit type ids or on the types resolved from element ids.")]
        public static async Task<string> SetTypeParameterValues(string parameterName, string value, long[] typeIds = null, long[] elementIds = null, string valueType = "auto")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_type_parameter_values", new { parameterName, value, typeIds, elementIds, valueType });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_change_element_type", Destructive = false), System.ComponentModel.Description("Change one or more elements to a target ElementType id after validating type compatibility.")]
        public static async Task<string> ChangeElementType(long[] elementIds, long typeId)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("change_element_type", new { elementIds, typeId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_assign_elements_to_workset", Destructive = false), System.ComponentModel.Description("Assign elements to a user workset by worksetId or worksetName in a workshared document.")]
        public static async Task<string> AssignElementsToWorkset(long[] elementIds, long? worksetId = null, string worksetName = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("assign_elements_to_workset", new { elementIds, worksetId, worksetName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("delete")]
    public class DeleteTools
    {
        [McpServerTool(Name = "revit_delete_element", Idempotent = true), System.ComponentModel.Description("Delete elements by ID. DESTRUCTIVE — cannot be undone via MCP. elementIds: JSON int array e.g. '[12345, 67890]'. Fetch IDs from get_selected_elements or ai_element_filter first.")]
        public static async Task<string> DeleteElement(string elementIds)
        {
            try
            {
                var parsedIds = JArray.Parse(elementIds).ToObject<long[]>();
                var result = await ToolGateway.SendToRevit("delete_element", new { elementIds = parsedIds });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("view")]
    public class ViewTools
    {
        [McpServerTool(Name = "revit_create_view", Destructive = false), System.ComponentModel.Description("Create a view (floorplan or 3d). Params: viewType ('floorplan' or '3d'), level (name, required for floorplan), name (optional).")]
        public static async Task<string> CreateView(string viewType, string level = "", string name = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_view", new { viewType, level, name });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_place_view_on_sheet", Destructive = false), System.ComponentModel.Description("Place a view on a sheet. Auto-creates sheet if sheetId omitted. Params: viewId (required), sheetId (optional), sheetNumber (optional), sheetName (optional).")]
        public static async Task<string> PlaceViewOnSheet(long viewId, long? sheetId = null, string sheetNumber = "", string sheetName = "MCP Generated Sheet")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("place_view_on_sheet", new { viewId, sheetId, sheetNumber, sheetName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_analyze_sheet_layout", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Analyze title block and a bounded viewport page in mm. Use startViewport + maxViewports (hard max 500).")]
        public static async Task<string> AnalyzeSheetLayout(string sheetNumber = "", long? sheetId = null, int startViewport = 0, int maxViewports = 100)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("analyze_sheet_layout", new { sheetNumber, sheetId, start_viewport = startViewport, max_viewports = maxViewports });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_capture_view_image"), System.ComponentModel.Description("Export a view to a raster image. output_path is optional; if provided it must be absolute and inside %TEMP% or %LOCALAPPDATA%\\RvtMcp\\captures\\. Params: view_id (optional, default active), output_path (optional, defaults under captures), pixel_size (default 1600), image_format ('png'|'jpeg', default 'png').")]
        public static async Task<string> CaptureViewImage(
            string output_path = null,
            long? view_id = null, int pixel_size = 1600, string image_format = "png")
        {
            var blocked = ServerState.BlockIfReadOnly("capture_view_image");
            if (blocked != null) return blocked;

            try
            {
                var result = await ToolGateway.SendToRevit("capture_view_image", new
                {
                    view_id,
                    output_path,
                    pixel_size,
                    image_format
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_set_view_crop"), System.ComponentModel.Description("Modify view crop: enabled, visible, explicit bounds_json (mm), or fit_element_ids with padding_mm. Params: view_id (optional, default active), enabled, visible, bounds_json, fit_element_ids, padding_mm (default 100).")]
        public static async Task<string> SetViewCrop(
            long? view_id = null, bool? enabled = null, bool? visible = null,
            string bounds_json = null, long[] fit_element_ids = null, double padding_mm = 100)
        {
            var blocked = ServerState.BlockIfReadOnly("set_view_crop");
            if (blocked != null) return blocked;

            try
            {
                object boundsObj = null;
                if (!string.IsNullOrWhiteSpace(bounds_json))
                    boundsObj = JObject.Parse(bounds_json);

                var result = await ToolGateway.SendToRevit("set_view_crop", new
                {
                    view_id,
                    enabled,
                    visible,
                    bounds = boundsObj,
                    fit_element_ids,
                    padding_mm
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_set_view_scale"), System.ComponentModel.Description("Set the graphical scale denominator of a view (e.g., 50 for 1:50). Params: view_id (optional, default active), scale (required, positive integer).")]
        public static async Task<string> SetViewScale(int scale, long? view_id = null)
        {
            var blocked = ServerState.BlockIfReadOnly("set_view_scale");
            if (blocked != null) return blocked;

            try
            {
                var result = await ToolGateway.SendToRevit("set_view_scale", new { view_id, scale });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_activate_view"), System.ComponentModel.Description("Set the active view in Revit UI. UI-only operation. Params: view_id OR view_name (required).")]
        public static async Task<string> ActivateView(long? view_id = null, string view_name = null)
        {
            var blocked = ServerState.BlockIfReadOnly("activate_view");
            if (blocked != null) return blocked;

            try
            {
                var result = await ToolGateway.SendToRevit("activate_view", new { view_id, view_name });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_show_element_in_view"), System.ComponentModel.Description("Activate view, select elements, zoom to elements. UI-only. Params: element_ids (required), view_id (optional), activate_view (default true), select (default true), zoom (default true).")]
        public static async Task<string> ShowElementInView(
            long[] element_ids,
            long? view_id = null, bool activate_view = true, bool select = true, bool zoom = true)
        {
            var blocked = ServerState.BlockIfReadOnly("show_element_in_view");
            if (blocked != null) return blocked;

            try
            {
                var result = await ToolGateway.SendToRevit("show_element_in_view", new
                {
                    element_ids,
                    view_id,
                    activate_view,
                    select,
                    zoom
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("export")]
    public class ExportTools
    {
        [McpServerTool(Name = "revit_export_room_data", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Export all rooms. output=inline returns DTO data; output=file writes SQLite to a local same-machine absolute path with schema and bounded preview. Remote clients can use preview but cannot read the local file.")]
        public static async Task<string> ExportRoomData(string output = "inline")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_room_data", new { output });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_export_pdf", Destructive = false), System.ComponentModel.Description("Export sheets or views to PDF. outputFolder must be an existing absolute path. viewIds defaults to the active view. combine=true produces one combined PDF. Do not retry on 60s timeout; Revit may still be running the command.")]
        public static async Task<string> ExportPdf(string outputFolder, long[] viewIds = null, bool combine = false, string fileName = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_pdf", new { output_folder = outputFolder, view_ids = viewIds, combine, file_name = fileName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_export_dwg", Destructive = false), System.ComponentModel.Description("Export sheets or views to AutoCAD DWG. outputFolder must be an existing absolute path. viewIds defaults to the active view. settingsName optionally selects a saved ExportDWGSettings. Do not retry on 60s timeout; Revit may still be running the command.")]
        public static async Task<string> ExportDwg(string outputFolder, long[] viewIds = null, string settingsName = "", string fileNamePrefix = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_dwg", new { output_folder = outputFolder, view_ids = viewIds, settings_name = settingsName, file_name_prefix = fileNamePrefix });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_export_dgn", Destructive = false), System.ComponentModel.Description("Export sheets or views to MicroStation DGN. outputFolder must be an existing absolute path. viewIds defaults to the active view. Do not retry on 60s timeout; Revit may still be running the command.")]
        public static async Task<string> ExportDgn(string outputFolder, long[] viewIds = null, string fileNamePrefix = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_dgn", new { output_folder = outputFolder, view_ids = viewIds, file_name_prefix = fileNamePrefix });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_export_dwf", Destructive = false), System.ComponentModel.Description("Export sheets or views to Autodesk DWF/DWFx. outputFolder must be an existing absolute path. viewIds defaults to the active view. useDwfx=true exports DWFx. Do not retry on 60s timeout; Revit may still be running the command.")]
        public static async Task<string> ExportDwf(string outputFolder, long[] viewIds = null, string fileName = "", bool useDwfx = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_dwf", new { output_folder = outputFolder, view_ids = viewIds, file_name = fileName, use_dwfx = useDwfx });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_export_ifc", Destructive = false), System.ComponentModel.Description("Export the model to IFC. outputFolder must be an existing absolute path. ifcVersion: IFC2x3|IFC4|default. Do not retry on 60s timeout; Revit may still be running the command.")]
        public static async Task<string> ExportIfc(string outputFolder, string fileName, string ifcVersion = "default")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_ifc", new { output_folder = outputFolder, file_name = fileName, ifc_version = ifcVersion });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_export_nwc", Destructive = false), System.ComponentModel.Description("Export the model to Navisworks NWC. outputFolder must be an existing absolute path. Optional exportScopeViewId scopes the export to one view. Requires the Navisworks NWC exporter add-in installed. Do not retry on 60s timeout; Revit may still be running the command.")]
        public static async Task<string> ExportNwc(string outputFolder, string fileName, long? exportScopeViewId = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_nwc", new { output_folder = outputFolder, file_name = fileName, export_scope_view_id = exportScopeViewId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_export_fbx", Destructive = false), System.ComponentModel.Description("Export a 3D view to Autodesk FBX. outputFolder must be an existing absolute path. viewId must reference a 3D view (defaults to the active view, which must be 3D).")]
        public static async Task<string> ExportFbx(string outputFolder, string fileName, long? viewId = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_fbx", new { output_folder = outputFolder, file_name = fileName, view_id = viewId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_export_gbxml", Destructive = false), System.ComponentModel.Description("Export the model's energy analytical data to gbXML. outputFolder must be an existing absolute path. Requires rooms/spaces with energy settings. Do not retry on 60s timeout; Revit may still be running the command.")]
        public static async Task<string> ExportGbxml(string outputFolder, string fileName)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_gbxml", new { output_folder = outputFolder, file_name = fileName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_export_image", Destructive = false), System.ComponentModel.Description("Export a view to a raster image. outputPath is an absolute file path (.png/.jpg). viewId defaults to the active view. pixelSize sets the longer dimension. imageFormat: png|jpeg.")]
        public static async Task<string> ExportImage(string outputPath, long? viewId = null, int pixelSize = 2048, string imageFormat = "png")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_image", new { output_path = outputPath, view_id = viewId, pixel_size = pixelSize, image_format = imageFormat });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_export_schedule_csv", Destructive = false), System.ComponentModel.Description("Export a Revit schedule's data to a delimited text/CSV file. outputPath is an absolute file path. Identify the schedule by scheduleId or scheduleName.")]
        public static async Task<string> ExportScheduleCsv(string outputPath, long? scheduleId = null, string scheduleName = "", string delimiter = ",")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_schedule_csv", new { output_path = outputPath, schedule_id = scheduleId, schedule_name = scheduleName, delimiter });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_export_elements_data", Destructive = false), System.ComponentModel.Description("Export element parameter data for a category to a JSON or CSV file. outputPath is an absolute file path. parameterNames defaults to a common set. format: json|csv.")]
        public static async Task<string> ExportElementsData(string category, string outputPath, string[] parameterNames = null, string format = "json")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_elements_data", new { category, output_path = outputPath, parameter_names = parameterNames, format });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_batch_export_sheets", Destructive = false), System.ComponentModel.Description("Export many sheets at once to PDF or DWG. outputFolder must be an existing absolute path. format: pdf|dwg. sheetIds defaults to ALL sheets; sheetNumberFilter narrows by sheet-number substring. Do not retry on 60s timeout; Revit may still be running the command.")]
        public static async Task<string> BatchExportSheets(string outputFolder, string format, long[] sheetIds = null, string sheetNumberFilter = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("batch_export_sheets", new { output_folder = outputFolder, format, sheet_ids = sheetIds, sheet_number_filter = sheetNumberFilter });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_export_settings", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List bounded export settings. kindFilter: all|dwg|print|view_sheet_set; use startIndex + maxResults.")]
        public static async Task<string> ListExportSettings(string kindFilter = "all", int startIndex = 0, int maxResults = 50)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_export_settings", new { kind_filter = kindFilter, start_index = startIndex, max_results = maxResults });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_view_sheet_set", Destructive = false), System.ComponentModel.Description("Create a named ViewSheetSet (a saved set of views/sheets) for batch printing/exporting. viewIds are the ViewSheet/View ElementIds to include.")]
        public static async Task<string> CreateViewSheetSet(string name, long[] viewIds)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_view_sheet_set", new { name, view_ids = viewIds });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_print_settings", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Report PrintManager state and bounded setting lists. kindFilter: all|print|view_sheet_set; use startIndex + maxResults.")]
        public static async Task<string> GetPrintSettings(string kindFilter = "all", int startIndex = 0, int maxResults = 50)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_print_settings", new { kind_filter = kindFilter, start_index = startIndex, max_results = maxResults });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("annotation")]
    public class AnnotationTools
    {
        [McpServerTool(Name = "revit_tag_all_walls", Destructive = false, Idempotent = true), System.ComponentModel.Description("Tag all walls in current view at midpoint. Skips already-tagged walls. Returns count of new tags.")]
        public static async Task<string> TagAllWalls()
        {
            try
            {
                var result = await ToolGateway.SendToRevit("tag_all_walls");
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_tag_all_rooms", Destructive = false, Idempotent = true), System.ComponentModel.Description("Tag all rooms in current view at location point. Skips already-tagged rooms. Returns count of new tags.")]
        public static async Task<string> TagAllRooms()
        {
            try
            {
                var result = await ToolGateway.SendToRevit("tag_all_rooms");
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_tag_elements", Destructive = false), System.ComponentModel.Description("Tag one or more elements in a view.")]
        public static async Task<string> TagElements(long[] elementIds, long? viewId = null, long? tagTypeId = null, string orientation = "Horizontal", bool leader = false, double offsetX = 0, double offsetY = 0)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("tag_elements", new { element_ids = elementIds, view_id = viewId, tag_type_id = tagTypeId, orientation, leader, offset_x = offsetX, offset_y = offsetY });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_tag_all_by_category", Destructive = false), System.ComponentModel.Description("Tag all elements of a specific category in a view.")]
        public static async Task<string> TagAllByCategory(string category, long? viewId = null, long? tagTypeId = null, bool skipExisting = true, bool leader = false, bool dryRun = false, int limit = 200)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("tag_all_by_category", new { category, view_id = viewId, tag_type_id = tagTypeId, skip_existing = skipExisting, leader, dry_run = dryRun, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_text_note", Destructive = false), System.ComponentModel.Description("Create a text note element in a view.")]
        public static async Task<string> CreateTextNote(string text, double x, double y, long? viewId = null, long? textTypeId = null, double width = 0, double rotationDeg = 0)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_text_note", new { text, x, y, view_id = viewId, text_type_id = textTypeId, width, rotation_deg = rotationDeg });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_dimensions", Destructive = false), System.ComponentModel.Description("Create a dimension element in a view. references: array of Revit element-reference objects.")]
        public static async Task<string> CreateDimensions(object[] references, long? viewId = null, long? dimensionTypeId = null, object line = null)
        {
            try
            {
                var parsedReferences = McpJsonInput.RequiredArray(references, nameof(references));
                var parsedLine = McpJsonInput.OptionalObject(line, nameof(line));
                var result = await ToolGateway.SendToRevit("create_dimensions", new { references = parsedReferences, view_id = viewId, dimension_type_id = dimensionTypeId, line = parsedLine });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_filled_region", Destructive = false), System.ComponentModel.Description("Create a filled region element in a view. points: array of {x,y,z} point objects (mm).")]
        public static async Task<string> CreateFilledRegion(object[] points, long? viewId = null, long? filledRegionTypeId = null)
        {
            try
            {
                var parsedPoints = McpJsonInput.RequiredArray(points, nameof(points));
                var result = await ToolGateway.SendToRevit("create_filled_region", new { points = parsedPoints, view_id = viewId, filled_region_type_id = filledRegionTypeId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_detail_line", Destructive = false), System.ComponentModel.Description("Create a detail line element in a view.")]
        public static async Task<string> CreateDetailLine(double startX, double startY, double endX, double endY, long? viewId = null, long? lineStyleId = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_detail_line", new { start_x = startX, start_y = startY, end_x = endX, end_y = endY, view_id = viewId, line_style_id = lineStyleId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_callout_view", Destructive = false), System.ComponentModel.Description("Create a callout view in a parent view.")]
        public static async Task<string> CreateCalloutView(long parentViewId, double minX, double minY, double maxX, double maxY, long? viewFamilyTypeId = null, string name = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_callout_view", new { parent_view_id = parentViewId, min_x = minX, min_y = minY, max_x = maxX, max_y = maxY, view_family_type_id = viewFamilyTypeId, name });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_keynotes", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List all keynotes loaded in the model with prefix and search filters.")]
        public static async Task<string> ListKeynotes(string keyPrefix = "", string search = "", int limit = 200)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_keynotes", new { key_prefix = keyPrefix, search, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_apply_keynote_to_element", Destructive = false), System.ComponentModel.Description("Apply keynote parameter values to one or more elements.")]
        public static async Task<string> ApplyKeynoteToElement(long[] elementIds, string keynote, bool dryRun = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("apply_keynote_to_element", new { element_ids = elementIds, keynote, dry_run = dryRun });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_find_untagged_elements", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Find visible elements of a specific category in a view that do not have tags.")]
        public static async Task<string> FindUntaggedElements(string category, long? viewId = null, int limit = 200)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("find_untagged_elements", new { category, view_id = viewId, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_find_undimensioned_elements", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Find visible elements of a specific category in a view that do not have dimensions.")]
        public static async Task<string> FindUndimensionedElements(string category, long? viewId = null, int limit = 200)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("find_undimensioned_elements", new { category, view_id = viewId, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_wipe_empty_tags", Destructive = true), System.ComponentModel.Description("Find and delete empty tags in a view.")]
        public static async Task<string> WipeEmptyTags(long? viewId = null, bool dryRun = true, int limit = 200)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("wipe_empty_tags", new { view_id = viewId, dry_run = dryRun, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("mep")]
    public class MepTools
    {
        [McpServerTool(Name = "revit_detect_system_elements", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Walk an MEP system and page IDs per category with startElement + maxElements (hard max 5000).")]
        public static async Task<string> DetectSystemElements(long elementId, int startElement = 0, int maxElements = 500)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("detect_system_elements", new { elementId, start_element = startElement, max_elements = maxElements });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_duct", Destructive = false), System.ComponentModel.Description("Create an HVAC duct between two points (mm). ductTypeId/systemTypeId/levelId default to first available / nearest level. Provide diameter for round duct OR width+height (mm) for rectangular.")]
        public static async Task<string> CreateDuct(double startX, double startY, double startZ, double endX, double endY, double endZ, long? ductTypeId = null, long? systemTypeId = null, long? levelId = null, double? width = null, double? height = null, double? diameter = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_duct", new { start_x = startX, start_y = startY, start_z = startZ, end_x = endX, end_y = endY, end_z = endZ, duct_type_id = ductTypeId, system_type_id = systemTypeId, level_id = levelId, width, height, diameter });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_pipe", Destructive = false), System.ComponentModel.Description("Create a plumbing pipe between two points (mm). pipeTypeId/systemTypeId/levelId default to first available / nearest level. Optional diameter (mm).")]
        public static async Task<string> CreatePipe(double startX, double startY, double startZ, double endX, double endY, double endZ, long? pipeTypeId = null, long? systemTypeId = null, long? levelId = null, double? diameter = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_pipe", new { start_x = startX, start_y = startY, start_z = startZ, end_x = endX, end_y = endY, end_z = endZ, pipe_type_id = pipeTypeId, system_type_id = systemTypeId, level_id = levelId, diameter });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_cable_tray", Destructive = false), System.ComponentModel.Description("Create an electrical cable tray between two points (mm). cableTrayTypeId/levelId default to first available / nearest level. Optional width+height (mm).")]
        public static async Task<string> CreateCableTray(double startX, double startY, double startZ, double endX, double endY, double endZ, long? cableTrayTypeId = null, long? levelId = null, double? width = null, double? height = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_cable_tray", new { start_x = startX, start_y = startY, start_z = startZ, end_x = endX, end_y = endY, end_z = endZ, cable_tray_type_id = cableTrayTypeId, level_id = levelId, width, height });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_conduit", Destructive = false), System.ComponentModel.Description("Create an electrical conduit between two points (mm). conduitTypeId/levelId default to first available / nearest level. Optional diameter (mm).")]
        public static async Task<string> CreateConduit(double startX, double startY, double startZ, double endX, double endY, double endZ, long? conduitTypeId = null, long? levelId = null, double? diameter = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_conduit", new { start_x = startX, start_y = startY, start_z = startZ, end_x = endX, end_y = endY, end_z = endZ, conduit_type_id = conduitTypeId, level_id = levelId, diameter });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_air_terminal", Destructive = false), System.ComponentModel.Description("Place an air terminal (diffuser/grille) family instance at a point (mm). typeId must be an Air Terminal FamilySymbol. Optional hostId for hosted placement on a duct/ceiling.")]
        public static async Task<string> CreateAirTerminal(long typeId, double x, double y, double z, long? levelId = null, long? hostId = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_air_terminal", new { type_id = typeId, x, y, z, level_id = levelId, host_id = hostId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_lighting_fixture", Destructive = false), System.ComponentModel.Description("Place a lighting fixture family instance at a point (mm). typeId must be a Lighting Fixture FamilySymbol. Optional hostId for hosted placement on a ceiling.")]
        public static async Task<string> CreateLightingFixture(long typeId, double x, double y, double z, long? levelId = null, long? hostId = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_lighting_fixture", new { type_id = typeId, x, y, z, level_id = levelId, host_id = hostId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_mep_systems", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List all MEP systems (mechanical/HVAC, piping/plumbing, electrical). domainFilter: all|mechanical|piping|electrical. Returns id, name, domain, system type, element count, connectivity status.")]
        public static async Task<string> ListMepSystems(string domainFilter = "all", int limit = 1000)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_mep_systems", new { domain_filter = domainFilter, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_system_inventory", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Return the full element inventory of one MEP system: all member elements with category/type plus a category breakdown. Identify by systemId or systemName.")]
        public static async Task<string> GetSystemInventory(long? systemId = null, string systemName = "", bool includeParameters = false, int limit = 2000)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_system_inventory", new { system_id = systemId, system_name = systemName, include_parameters = includeParameters, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_mep_element_connectors", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Inspect all connectors on an MEP element (duct/pipe/fitting/equipment/terminal): domain, shape, position (mm), connection status, flow, direction.")]
        public static async Task<string> GetMepElementConnectors(long elementId)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_mep_element_connectors", new { element_id = elementId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_connect_mep_elements", Destructive = false), System.ComponentModel.Description("Connect the nearest open connectors of two MEP elements. Optionally pin specific connectors via connectorIndex1/connectorIndex2 — these are Connector.Id values (the connector_id field from get_mep_element_connectors), NOT ordinals. Domains must match.")]
        public static async Task<string> ConnectMepElements(long elementId1, long elementId2, long? connectorIndex1 = null, long? connectorIndex2 = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("connect_mep_elements", new { element_id_1 = elementId1, element_id_2 = elementId2, connector_index_1 = connectorIndex1, connector_index_2 = connectorIndex2 });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_mep_fitting", Destructive = false), System.ComponentModel.Description("Insert an MEP fitting at connectors of existing MEP elements. fittingKind: elbow|tee|union|cross|transition. connectors is a JSON array of {element_id, connector_index} where connector_index is the connector_id from get_mep_element_connectors: elbow/union/transition need 2, tee 3, cross 4.")]
        public static async Task<string> CreateMepFitting(string fittingKind, string connectors)
        {
            try
            {
                var parsedConnectors = JArray.Parse(connectors);
                var result = await ToolGateway.SendToRevit("create_mep_fitting", new { fitting_kind = fittingKind, connectors = parsedConnectors });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_set_system_classification", Destructive = false), System.ComponentModel.Description("Add MEP elements to an existing duct/piping system. If systemId omitted, only reports current system membership (read-only). elementIds is an array of MEP element ids.")]
        public static async Task<string> SetSystemClassification(long[] elementIds, long? systemId = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_system_classification", new { element_ids = elementIds, system_id = systemId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_panel_schedule", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Read panel metadata and a bounded circuit page. Use startCircuit + maxCircuits (hard max 1000).")]
        public static async Task<string> GetPanelSchedule(long? panelId = null, string panelName = "", int startCircuit = 0, int maxCircuits = 100)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_panel_schedule", new { panel_id = panelId, panel_name = panelName, start_circuit = startCircuit, max_circuits = maxCircuits });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_find_mep_disconnects", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Find MEP elements with open/unconnected End connectors (potential gaps in ductwork, piping, conduit). domainFilter: all|hvac|piping|electrical. viewOnly restricts to the active view.")]
        public static async Task<string> FindMepDisconnects(string domainFilter = "all", bool viewOnly = false, int limit = 2000)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("find_mep_disconnects", new { domain_filter = domainFilter, view_only = viewOnly, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_analyze_mep_network", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Analyze one MEP system's topology: category breakdown, connectivity health, base equipment, open connector count, and issues/recommendations. Identify by systemId or systemName.")]
        public static async Task<string> AnalyzeMepNetwork(long? systemId = null, string systemName = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("analyze_mep_network", new { system_id = systemId, system_name = systemName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("graphics")]
    public class GraphicsTools
    {
        [McpServerTool(Name = "revit_create_view_filter", Destructive = false), System.ComponentModel.Description("Create a parameter-based view filter (ParameterFilterElement) targeting one or more categories. rules is an optional JSON array of {parameter_name, evaluator, value} where evaluator is equals|not_equals|greater|less|contains|begins_with|ends_with. Omit rules for a category-only filter.")]
        public static async Task<string> CreateViewFilter(string name, string[] categories, string rules = "")
        {
            try
            {
                var parsedRules = string.IsNullOrWhiteSpace(rules) ? null : JArray.Parse(rules);
                var result = await ToolGateway.SendToRevit("create_view_filter", new { name, categories, rules = parsedRules });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_apply_filter_to_view", Destructive = false), System.ComponentModel.Description("Add an existing view filter (ParameterFilterElement) to a view's filter list. viewId defaults to the active view. visible sets the initial visibility of matching elements.")]
        public static async Task<string> ApplyFilterToView(long filterId, long? viewId = null, bool visible = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("apply_filter_to_view", new { filter_id = filterId, view_id = viewId, visible });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_set_filter_overrides", Destructive = false), System.ComponentModel.Description("Set graphic overrides for a filter already applied to a view. Colors are hex '#RRGGBB'. transparency 0-100, projectionLineWeight 1-16. Only supplied properties change; others are preserved. viewId defaults to active view.")]
        public static async Task<string> SetFilterOverrides(long filterId, long? viewId = null, string projectionLineColor = "", string surfaceForegroundColor = "", string cutLineColor = "", int? transparency = null, bool? halftone = null, int? projectionLineWeight = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_filter_overrides", new
                {
                    filter_id = filterId,
                    view_id = viewId,
                    projection_line_color = string.IsNullOrEmpty(projectionLineColor) ? null : projectionLineColor,
                    surface_foreground_color = string.IsNullOrEmpty(surfaceForegroundColor) ? null : surfaceForegroundColor,
                    cut_line_color = string.IsNullOrEmpty(cutLineColor) ? null : cutLineColor,
                    transparency,
                    halftone,
                    projection_line_weight = projectionLineWeight
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_view_filters", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List all view filter definitions (ParameterFilterElement) in the document. If viewId is supplied, only filters applied to that view. includeUsage lists which views each filter is applied to.")]
        public static async Task<string> ListViewFilters(long? viewId = null, bool includeUsage = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_view_filters", new { view_id = viewId, include_usage = includeUsage });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_remove_filter_from_view", Destructive = false), System.ComponentModel.Description("Remove a view filter from a view's filter list. viewId defaults to active view. deleteDefinitionIfUnused deletes the ParameterFilterElement entirely if no other view uses it.")]
        public static async Task<string> RemoveFilterFromView(long filterId, long? viewId = null, bool deleteDefinitionIfUnused = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("remove_filter_from_view", new { filter_id = filterId, view_id = viewId, delete_definition_if_unused = deleteDefinitionIfUnused });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_override_element_graphics", Destructive = false), System.ComponentModel.Description("Apply per-element view-specific graphic overrides (color, transparency, halftone, line weight) to elements in a view. Colors are hex '#RRGGBB'. transparency 0-100, projectionLineWeight 1-16. viewId defaults to active view.")]
        public static async Task<string> OverrideElementGraphics(long[] elementIds, long? viewId = null, string projectionLineColor = "", string surfaceForegroundColor = "", string cutLineColor = "", int? transparency = null, bool? halftone = null, int? projectionLineWeight = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("override_element_graphics", new
                {
                    element_ids = elementIds,
                    view_id = viewId,
                    projection_line_color = string.IsNullOrEmpty(projectionLineColor) ? null : projectionLineColor,
                    surface_foreground_color = string.IsNullOrEmpty(surfaceForegroundColor) ? null : surfaceForegroundColor,
                    cut_line_color = string.IsNullOrEmpty(cutLineColor) ? null : cutLineColor,
                    transparency,
                    halftone,
                    projection_line_weight = projectionLineWeight
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_clear_element_overrides", Destructive = false), System.ComponentModel.Description("Reset per-element view-specific graphic overrides to default. If elementIds is omitted, clears overrides on every element in the view that currently has them. viewId defaults to active view.")]
        public static async Task<string> ClearElementOverrides(long[] elementIds = null, long? viewId = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("clear_element_overrides", new { element_ids = elementIds, view_id = viewId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_view_visibility", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Report a view's visibility/graphics state: hidden categories, applied filters, detail level, discipline, scale, view template, graphics-overrides-allowed. includeCategoryList lists every model category with its hidden state. viewId defaults to active view.")]
        public static async Task<string> GetViewVisibility(long? viewId = null, bool includeCategoryList = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_view_visibility", new { view_id = viewId, include_category_list = includeCategoryList });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_set_category_visibility", Destructive = false), System.ComponentModel.Description("Show or hide model categories in a view. categories is an array of category names. hidden=true hides, false shows. viewId defaults to active view.")]
        public static async Task<string> SetCategoryVisibility(string[] categories, bool hidden, long? viewId = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_category_visibility", new { categories, hidden, view_id = viewId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_phases", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List all project phases (in sequence order) and all phase filters. Call before set_view_phase or set_element_phase to discover valid phase names/ids.")]
        public static async Task<string> ListPhases()
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_phases", new { });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_set_view_phase", Destructive = false), System.ComponentModel.Description("Set a view's Phase and/or Phase Filter. Identify each by id or name. At least one of phase / phase filter must be supplied. viewId defaults to active view.")]
        public static async Task<string> SetViewPhase(long? viewId = null, long? phaseId = null, string phaseName = "", long? phaseFilterId = null, string phaseFilterName = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_view_phase", new { view_id = viewId, phase_id = phaseId, phase_name = phaseName, phase_filter_id = phaseFilterId, phase_filter_name = phaseFilterName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_set_element_phase", Destructive = false), System.ComponentModel.Description("Set the Phase Created and/or Phase Demolished of elements. Identify phases by id or name. Use phaseDemolishedName='None' to clear demolition. At least one phase must be supplied.")]
        public static async Task<string> SetElementPhase(long[] elementIds, long? phaseCreatedId = null, string phaseCreatedName = "", long? phaseDemolishedId = null, string phaseDemolishedName = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_element_phase", new { element_ids = elementIds, phase_created_id = phaseCreatedId, phase_created_name = phaseCreatedName, phase_demolished_id = phaseDemolishedId, phase_demolished_name = phaseDemolishedName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("meta")]
    public class SendCodeTools
    {
        [McpServerTool(Name = "revit_send_code_to_revit"), System.ComponentModel.Description("Compile + run C# inside Revit for workflows not covered by typed tools. Variables: doc (Document), uidoc (UIDocument), app (UIApplication). Write body only, auto-wrapped in static Run(UIApplication). Must end with 'return ...;'. Output above 700 KiB auto-spills to a local same-machine file with schema and preview; there is no output parameter. Remote clients receive preview but cannot read the local file. Namespaces: System, System.Linq, System.Collections.Generic, Autodesk.Revit.DB, Autodesk.Revit.UI.")]
        public static async Task<string> SendCodeToRevit(string code)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("send_code_to_revit", new { code });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("toolbaker")]
    public class ToolbakerTools
    {
        [McpServerTool(Name = "revit_list_baked_tools", ReadOnly = true, Idempotent = true), System.ComponentModel.Description(
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

        [McpServerTool(Name = "revit_run_baked_tool"), System.ComponentModel.Description(
            "Run a baked tool by name. Call list_baked_tools first to discover. " +
            "Params: name, params (tool-specific), output=inline|file. File mode writes a sniffed local same-machine artifact; oversized inline output auto-spills. Remote clients receive preview only.")]
        public static async Task<string> RunBakedTool(string name, object @params = null, string output = "inline")
        {
            var revitVersionBeforeConnect = ToolGateway.CurrentRevitVersion ?? AuthToken.Target ?? "unknown";
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
                var converted = JToken.FromObject(@params);
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

    [McpServerToolType, Toolset("toolbaker")]
    public class AdaptiveBakeTools
    {
        [McpServerTool(Name = "revit_list_bake_suggestions", ReadOnly = true, Idempotent = true), System.ComponentModel.Description(
            "List bounded adaptive ToolBaker suggestions. Filter by state and page with startIndex + limit (hard max 500).")]
        public static string ListBakeSuggestions(string state = "", int startIndex = 0, int limit = 100)
        {
            try
            {
                var paths = new BakePaths();
                using var db = new BakeDb(paths);
                db.Migrate();
                return ListBakeSuggestionsHandler.Handle(db, ToolGateway.UsageLogger, state, startIndex, limit);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_accept_bake_suggestion"), System.ComponentModel.Description(
            "Accept an adaptive ToolBaker suggestion by id. Validates name, schema, and output choice, then prepares a bake request without native tool promotion.")]
        public static async Task<string> AcceptBakeSuggestion(string id, string name, string output_choice = "mcp_only", string params_schema = null)
        {
            try
            {
                var paths = new BakePaths();
                using var db = new BakeDb(paths);
                db.Migrate();
                return await AcceptBakeSuggestionHandler.HandleAsync(
                    db,
                    id,
                    name,
                    output_choice,
                    params_schema,
                    pluginApply: request => ToolGateway.SendToRevit("apply_bake", request));
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_dismiss_bake_suggestion"), System.ComponentModel.Description(
            "Dismiss an adaptive ToolBaker suggestion. action must be snooze_30d, never, or never_with_gap_signal.")]
        public static string DismissBakeSuggestion(string id, string action)
        {
            try
            {
                var paths = new BakePaths();
                using var db = new BakeDb(paths);
                db.Migrate();
                var revitVersion = ToolGateway.CurrentRevitVersion ?? AuthToken.Target ?? "unknown";
                return DismissBakeSuggestionHandler.Handle(
                    db,
                    id,
                    action,
                    currentRevitVersion: revitVersion,
                    auditLog: new ToolBakerAuditLog(paths.AuditJsonl));
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("meta")]
    public class MetaTools
    {
        [McpServerTool(Name = "revit_show_message", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Show a Revit TaskDialog. Message echo is off by default; opt in with echoMessage and cap it with maxEchoChars.")]
        public static async Task<string> ShowMessage(string message = null, string title = null, bool echoMessage = false, int maxEchoChars = 1024)
        {
            try
            {
                var parameters = new { message, title, echo_message = echoMessage, max_echo_chars = maxEchoChars };
                var result = await ToolGateway.SendToRevit("show_message", parameters);
                return JsonConvert.SerializeObject(result);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_available_targets", ReadOnly = true, Idempotent = true), System.ComponentModel.Description(
            "List every Revit instance currently running with the rvt-mcp plugin loaded. " +
            "Reads the discovery directory %LOCALAPPDATA%\\RvtMcp\\ and parses each revit-YYYY.json file. " +
            "Use this BEFORE revit_switch_target so you know which years (4-digit, e.g. 2024) are actually available — do not guess. " +
            "Returns: {discovery_dir, count, targets: [{year, transport ('tcp'|'pipe'), port, pipe_name, pid, discovery_file, is_currently_connected}]}. " +
            "If count == 0, no Revit is running or no plugin is loaded — instruct the user to start Revit and enable the rvt-mcp plugin.")]
        public static string ListAvailableTargets()
        {
            try
            {
                var dir = AuthToken.DiscoveryDir();
                var found = AuthToken.ListAvailable();
                var currentYear = ToolGateway.CurrentRevitVersion;
                var targets = found.Select(d => new
                {
                    year = d.Year,
                    transport = d.Transport,
                    port = d.Transport == "tcp" ? (int?)d.Port : null,
                    pipe_name = d.Transport == "pipe" ? d.PipeName : null,
                    pid = d.Pid,
                    discovery_file = d.DiscoveryFilePath,
                    is_currently_connected = string.Equals(d.Year, currentYear, StringComparison.Ordinal)
                }).ToArray();
                return JsonConvert.SerializeObject(new
                {
                    discovery_dir = dir,
                    count = targets.Length,
                    targets,
                    note = targets.Length == 0
                        ? "No revit-YYYY.json files found. Start Revit and ensure the rvt-mcp plugin is loaded (Add-Ins ribbon)."
                        : "Pass any 'year' value above to revit_switch_target to route subsequent commands to that Revit."
                }, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_current_target", ReadOnly = true, Idempotent = true), System.ComponentModel.Description(
            "Report which Revit instance this MCP server will route the NEXT command to. " +
            "Returns: {pinned_target (4-digit year or 'auto'), currently_connected_year (or null), discovery_dir}. " +
            "Use to verify routing before sending Revit-modifying commands when multiple Revits are open.")]
        public static string GetCurrentTarget()
        {
            try
            {
                return JsonConvert.SerializeObject(new
                {
                    pinned_target = AuthToken.Target ?? "auto",
                    currently_connected_year = ToolGateway.CurrentRevitVersion,
                    discovery_dir = AuthToken.DiscoveryDir(),
                    note = AuthToken.Target == null
                        ? "Auto-detect mode: next reconnect picks the first alive Revit (pipe 2027>2026>2025, then tcp 2024>2023>2022)."
                        : "Pinned to Revit " + AuthToken.Target + ". Call revit_switch_target with version='auto' to clear the pin."
                }, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_switch_target"), System.ComponentModel.Description(
            "Switch active Revit connection to a specific version when multiple Revits are running. " +
            "version: a 4-digit calendar year — '2022'|'2023'|'2024'|'2025'|'2026'|'2027' — or 'auto' to clear the pin and re-enable auto-detect. " +
            "DO NOT pass R-codes like 'R22' or 'R24' — they are rejected with an educational error. " +
            "DO NOT guess. ALWAYS call revit_list_available_targets first to see which versions are actually running and what year string each one uses. " +
            "Immediately closes the current Server↔Plugin connection (cancels in-flight requests) and updates the target. " +
            "The next tool call transparently reconnects against the new target. " +
            "Returns: {ok, previousTarget, newTarget, verified (if verify=true)}. " +
            "verify=true (default): immediately attempts get_current_view_info against the new target to confirm connectivity; " +
            "set verify=false to skip when the new target's document isn't in a view yet (e.g., Revit just launched).")]
        public static async Task<string> SwitchTarget(string version, bool verify = true)
        {
            try
            {
                var previousTarget = AuthToken.Target;
                string newTarget = null;
                if (!string.IsNullOrWhiteSpace(version) && !version.Equals("auto", StringComparison.OrdinalIgnoreCase))
                {
                    var trimmed = version.Trim();

                    // Hard validation: reject legacy R-codes with an educational message that
                    // forces the agent to read revit_list_available_targets instead of guessing.
                    if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[Rr]\d{2}$"))
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            ok = false,
                            error = "Invalid version format '" + version + "'. v0.5+ uses 4-digit calendar years, NOT R-codes. " +
                                    "Translate: R22=2022, R23=2023, R24=2024, R25=2025, R26=2026, R27=2027. " +
                                    "BEFORE calling this tool again, call revit_list_available_targets to see exactly which versions are running on this machine and what year string each one uses. " +
                                    "Do not guess.",
                            allowed_versions = AuthToken.AllVersions,
                            recommended_next_tool = "revit_list_available_targets"
                        });
                    }

                    if (Array.IndexOf(AuthToken.AllVersions, trimmed) < 0)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            ok = false,
                            error = "Invalid version '" + version + "'. Allowed: " + string.Join("|", AuthToken.AllVersions) + " or 'auto'. " +
                                    "Call revit_list_available_targets first to see which years are actually running on this machine.",
                            allowed_versions = AuthToken.AllVersions,
                            recommended_next_tool = "revit_list_available_targets"
                        });
                    }
                    newTarget = trimmed;
                }

                ToolGateway.Reconnect(newTarget);

                if (!verify)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        ok = true,
                        previousTarget = previousTarget ?? "auto",
                        newTarget = newTarget ?? "auto",
                        verified = false,
                        note = "Target updated. Next tool call will connect to new target."
                    });
                }

                try
                {
                    var probe = await ToolGateway.SendToRevit("get_current_view_info");
                    return JsonConvert.SerializeObject(new
                    {
                        ok = true,
                        previousTarget = previousTarget ?? "auto",
                        newTarget = newTarget ?? "auto",
                        verified = true,
                        activeView = probe.Value<string>("viewName")
                    });
                }
                catch (Exception verifyEx)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        ok = true,
                        previousTarget = previousTarget ?? "auto",
                        newTarget = newTarget ?? "auto",
                        verified = false,
                        verifyError = verifyEx.Message,
                        note = "Target set, but verify failed. The next tool call may still succeed."
                    });
                }
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_batch_execute"), System.ComponentModel.Description(
            "Run multiple MCP commands atomically inside one Revit TransactionGroup (single undo on success). " +
            "At most 20 commands; split larger jobs. " +
            "Input: commands — JSON array of {command, params}, e.g. " +
            "'[{\"command\":\"create_level\",\"params\":{\"elevation\":3000}}, " +
            "{\"command\":\"create_grid\",\"params\":{\"startX\":0,\"startY\":0,\"endX\":5000,\"endY\":0}}]'. " +
            "On any failure the whole group rolls back unless continueOnError=true. " +
            "Returns: {results: [{index, ok, data|error}], rolledBack}. output=file writes NDJSON to a local same-machine absolute path with bounded preview; remote clients receive preview only.")]
        public static async Task<string> BatchExecute(string commands, bool continueOnError = false, string output = "inline")
        {
            try
            {
                var parsed = JArray.Parse(commands);
                if (parsed.Count > 20) // keep in sync with BatchExecutor.MaxCommands
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "batch_execute supports at most 20 commands (got " + parsed.Count + "). Split the batch."
                    }, Formatting.Indented);
                }
                var result = await ToolGateway.SendToRevit("batch_execute", new { commands = parsed, continueOnError, output });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_set_project_info"), System.ComponentModel.Description("Set typed fields on doc.ProjectInformation. Params: name, number, client_name, address, status, issue_date (all optional, at least one required). Returns changed_fields and skipped reasons for read-only/missing parameters.")]
        public static async Task<string> SetProjectInfo(
            string name = null, string number = null,
            string client_name = null, string address = null,
            string status = null, string issue_date = null)
        {
            var blocked = ServerState.BlockIfReadOnly("set_project_info");
            if (blocked != null) return blocked;

            try
            {
                var result = await ToolGateway.SendToRevit("set_project_info", new
                {
                    name,
                    number,
                    client_name,
                    address,
                    status,
                    issue_date
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_purge_unused"), System.ComponentModel.Description("Conservative purge of unused loadable family symbols. MVP supports targets=['families'] only. Skips in-place families and symbols with any placed instance. dry_run defaults to true.")]
        public static async Task<string> PurgeUnused(
            string[] targets = null, bool dry_run = true, int limit = 500)
        {
            if (!dry_run)
            {
                var blocked = ServerState.BlockIfReadOnly("purge_unused");
                if (blocked != null) return blocked;
            }

            try
            {
                var result = await ToolGateway.SendToRevit("purge_unused", new
                {
                    targets = targets ?? new[] { "families" },
                    dry_run,
                    limit
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_analyze_usage_patterns", ReadOnly = true, Idempotent = true), System.ComponentModel.Description(
            "Analyze MCP tool usage. Returns session stats (call counts, success rates, top tools, flags) " +
            "plus historical data from journal files. " +
            "Params: days (int, default 1) — days of history to include. " +
            "Use to spot most-used tools, frequent failures, repeated patterns.")]
        public static string AnalyzeUsagePatterns(int days = 1)
        {
            try
            {
                var session = ToolGateway.Session;
                if (session == null) return JsonConvert.SerializeObject(new { error = "No active session" });

                var report = session.GetPatternReport();

                var journal = session.Journal;
                var historicalTools = new Dictionary<string, int>();
                var historicalErrors = new Dictionary<string, int>();
                int historicalTotal = 0;

                var dates = journal.ListDates();
                var cutoff = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd");

                foreach (var date in dates)
                {
                    if (string.Compare(date, cutoff, StringComparison.Ordinal) < 0) continue;
                    var entries = journal.ReadDay(date);
                    foreach (var entry in entries)
                    {
                        historicalTotal++;
                        if (!historicalTools.ContainsKey(entry.Tool)) historicalTools[entry.Tool] = 0;
                        historicalTools[entry.Tool]++;
                        if (!entry.Success)
                        {
                            if (!historicalErrors.ContainsKey(entry.Tool)) historicalErrors[entry.Tool] = 0;
                            historicalErrors[entry.Tool]++;
                        }
                    }
                }

                var result = new
                {
                    session = new
                    {
                        total_calls = report.TotalCalls,
                        total_errors = report.TotalErrors,
                        top_tools = report.TopTools.Select(t => new { t.Tool, t.CallCount, t.ErrorCount, error_rate = t.ErrorRate.ToString("P0") }),
                        error_prone = report.ErrorProne.Select(t => new { t.Tool, t.CallCount, t.ErrorCount, error_rate = t.ErrorRate.ToString("P0") }),
                        flags = report.Flags
                    },
                    history = new
                    {
                        days_included = days,
                        total_calls = historicalTotal,
                        top_tools = historicalTools.OrderByDescending(kv => kv.Value).Take(10)
                            .Select(kv => new { tool = kv.Key, count = kv.Value }),
                        error_tools = historicalErrors.OrderByDescending(kv => kv.Value).Take(5)
                            .Select(kv => new { tool = kv.Key, errors = kv.Value })
                    }
                };

                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }
    }

    [McpServerToolType, Toolset("structural")]
    public class StructuralTools
    {
        [McpServerTool(Name = "revit_create_structural_column"), System.ComponentModel.Description("Create a structural column at a point. Params: type_id OR type_name (structural column family), x_mm/y_mm/z_mm (default 0), level_id OR level_name (default lowest level), height_mm (optional top offset), rotation_deg (optional, default 0).")]
        public static async Task<string> CreateStructuralColumn(
            long? type_id = null, string type_name = null,
            double x_mm = 0, double y_mm = 0, double z_mm = 0,
            long? level_id = null, string level_name = null,
            double? height_mm = null, double rotation_deg = 0)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_structural_column", new {
                    type_id, type_name, x_mm, y_mm, z_mm,
                    level_id, level_name, height_mm, rotation_deg
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_structural_beam"), System.ComponentModel.Description("Create a structural beam between two points. Params: type_id OR type_name (structural framing family), start_x_mm/start_y_mm/start_z_mm (required), end_x_mm/end_y_mm/end_z_mm (required), level_id OR level_name, usage ('beam'|'brace'|'joist', default 'beam').")]
        public static async Task<string> CreateStructuralBeam(
            double start_x_mm, double start_y_mm, double end_x_mm, double end_y_mm,
            long? type_id = null, string type_name = null,
            double start_z_mm = 0, double end_z_mm = 0,
            long? level_id = null, string level_name = null, string usage = "beam")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_structural_beam", new {
                    type_id, type_name, start_x_mm, start_y_mm, start_z_mm,
                    end_x_mm, end_y_mm, end_z_mm, level_id, level_name, usage
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_structural_wall"), System.ComponentModel.Description("Create a structural wall between two points. Params: start_x_mm/start_y_mm/end_x_mm/end_y_mm (required), wall_type_id OR wall_type_name (optional, default current), level_id OR level_name, height_mm (default 3000). Sets isStructural=true.")]
        public static async Task<string> CreateStructuralWall(
            double start_x_mm, double start_y_mm, double end_x_mm, double end_y_mm,
            long? wall_type_id = null, string wall_type_name = null,
            long? level_id = null, string level_name = null, double height_mm = 3000)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_structural_wall", new {
                    wall_type_id, wall_type_name, start_x_mm, start_y_mm,
                    end_x_mm, end_y_mm, level_id, level_name, height_mm
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_foundation_isolated"), System.ComponentModel.Description("Create an isolated/spread footing at a point or under an existing column. Params: type_id OR type_name (StructuralFoundation), x_mm/y_mm (required), z_mm (default 0), level_id OR level_name, host_column_id (optional — when supplied, location is taken from the column), rotation_deg.")]
        public static async Task<string> CreateFoundationIsolated(
            double x_mm, double y_mm,
            long? type_id = null, string type_name = null, double z_mm = 0,
            long? level_id = null, string level_name = null,
            long? host_column_id = null, double rotation_deg = 0)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_foundation_isolated", new {
                    type_id, type_name, x_mm, y_mm, z_mm,
                    level_id, level_name, host_column_id, rotation_deg
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_foundation_wall"), System.ComponentModel.Description("Create a wall foundation under an existing wall. Params: wall_id (required), foundation_type_id OR foundation_type_name (optional, defaults to first WallFoundation type).")]
        public static async Task<string> CreateFoundationWall(
            long wall_id,
            long? foundation_type_id = null, string foundation_type_name = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_foundation_wall", new {
                    wall_id, foundation_type_id, foundation_type_name
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_rebar", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List rebar instances. Optional filters: host_id, view_id, limit (default 500). Returns id, bar_type, diameter_mm, quantity, layout_rule, host_id, host_category.")]
        public static async Task<string> ListRebar(long? host_id = null, long? view_id = null, int limit = 500)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_rebar", new { host_id, view_id, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_structural_loads", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List structural loads (point, line, area). Filter by element_id (host) or load_type ('point'|'line'|'area'). Returns force/moment components per load.")]
        public static async Task<string> GetStructuralLoads(
            long? element_id = null, string load_type = null, int limit = 500)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_structural_loads", new { element_id, load_type, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_set_structural_load"), System.ComponentModel.Description("Update force/moment of an existing structural load. action='update' supported; action='create' returns not_implemented. Params: action ('update'), load_id (required for update), force_x/y/z, moment_x/y/z (optional, units = Revit internal).")]
        public static async Task<string> SetStructuralLoad(
            string action,
            long? load_id = null,
            double? force_x = null, double? force_y = null, double? force_z = null,
            double? moment_x = null, double? moment_y = null, double? moment_z = null)
        {
            if (string.Equals(action, "update", StringComparison.OrdinalIgnoreCase))
            {
                var blocked = ServerState.BlockIfReadOnly("set_structural_load");
                if (blocked != null) return blocked;
            }

            try
            {
                var result = await ToolGateway.SendToRevit("set_structural_load", new
                {
                    action,
                    load_id,
                    force_x,
                    force_y,
                    force_z,
                    moment_x,
                    moment_y,
                    moment_z
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_analyze_structural_connections", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Audit structural joins between columns and beams. Optional element_ids filter (default = all structural framing + columns). Returns joined_count and joined_with per element.")]
        public static async Task<string> AnalyzeStructuralConnections(
            long[] element_ids = null, int limit = 500)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("analyze_structural_connections", new { element_ids, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_tag_structural_framing"), System.ComponentModel.Description("Place structural framing tags on beams in the active or specified view. Params: view_id (optional, default active view), tag_type_id (optional, default first StructuralFramingTags), element_ids (optional, default all framing in view).")]
        public static async Task<string> TagStructuralFraming(
            long? view_id = null, long? tag_type_id = null, long[] element_ids = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("tag_structural_framing", new { view_id, tag_type_id, element_ids });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_rebar_set"), System.ComponentModel.Description("Create a straight rebar set (single bar or arrayed) inside a structural host. Params: host_id (required), bar_type_id OR bar_type_name (optional, default first RebarBarType), layout_rule ('Single'|'FixedNumber'|'MaximumSpacing', default 'Single'), spacing_mm (required for FixedNumber/MaximumSpacing), quantity (required for FixedNumber, default 1), start_x_mm/start_y_mm/start_z_mm + end_x_mm/end_y_mm/end_z_mm (required). Uses Rebar.CreateFromCurves + RebarShapeDrivenAccessor.")]
        public static async Task<string> CreateRebarSet(
            long host_id,
            double start_x_mm, double start_y_mm, double start_z_mm,
            double end_x_mm, double end_y_mm, double end_z_mm,
            long? bar_type_id = null, string bar_type_name = null,
            string layout_rule = "Single",
            double? spacing_mm = null, int quantity = 1)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_rebar_set", new
                {
                    host_id, bar_type_id, bar_type_name, layout_rule, spacing_mm, quantity,
                    start_x_mm, start_y_mm, start_z_mm, end_x_mm, end_y_mm, end_z_mm
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_rebar_stirrup"), System.ComponentModel.Description("Create a shape-driven rebar (typically a closed stirrup) inside a concrete host. Params: host_id (required), bar_type_id OR bar_type_name (optional, default first RebarBarType), shape_id OR shape_name (optional, default first RebarShape — e.g. 'Stirrup T1', 'M_T1'), origin_x_mm/origin_y_mm/origin_z_mm (required), x_vec_x/y/z + y_vec_x/y/z (optional unit-less direction vectors, default world X/Y). Uses Rebar.CreateFromRebarShape.")]
        public static async Task<string> CreateRebarStirrup(
            long host_id,
            double origin_x_mm, double origin_y_mm, double origin_z_mm,
            long? bar_type_id = null, string bar_type_name = null,
            long? shape_id = null, string shape_name = null,
            double x_vec_x = 1, double x_vec_y = 0, double x_vec_z = 0,
            double y_vec_x = 0, double y_vec_y = 1, double y_vec_z = 0)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_rebar_stirrup", new
                {
                    host_id, bar_type_id, bar_type_name, shape_id, shape_name,
                    origin_x_mm, origin_y_mm, origin_z_mm,
                    x_vec_x, x_vec_y, x_vec_z, y_vec_x, y_vec_y, y_vec_z
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("lint")]
    public class LintTools
    {
        [McpServerTool(Name = "revit_analyze_view_naming_patterns", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Infer naming patterns with bounded detail. Page outliers with startOutlier + maxOutliers; maxPatterns hard max 500.")]
        public static async Task<string> AnalyzeViewNamingPatterns(int maxPatterns = 50, int startOutlier = 0, int maxOutliers = 20)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("analyze_view_naming_patterns", new { max_patterns = maxPatterns, start_outlier = startOutlier, max_outliers = maxOutliers });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_suggest_view_name_corrections", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Propose corrected view names for outliers. Optional profile=<id> uses firm-profile library rule; omit to use project-inferred dominant pattern. Returns suggestions array with id/current/suggested/reason.")]
        public static async Task<string> SuggestViewNameCorrections(string profile = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("suggest_view_name_corrections", new { profile });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_detect_firm_profile", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Fingerprint project naming (views + sheets + levels), match against firm-profile library. Returns project_pattern (always) + library_match (null if library empty or no match).")]
        public static async Task<string> DetectFirmProfile()
        {
            try
            {
                var result = await ToolGateway.SendToRevit("detect_firm_profile");
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_model_warnings_summary", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Group warnings by description with bounded types/examples. Use max_warning_types (hard max 1000) and max_examples_per_type (hard max 100).")]
        public static async Task<string> GetModelWarningsSummary(
            bool include_examples = true, int max_examples_per_type = 5, int max_warning_types = 200)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_model_warnings_summary", new { include_examples, max_examples_per_type, max_warning_types });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    internal static class McpJsonInput
    {
        public static JArray RequiredArray(object value, string parameterName)
        {
            var array = OptionalArray(value, parameterName);
            if (array == null)
                throw new ArgumentException($"{parameterName} must be a JSON array.");
            return array;
        }

        public static JArray OptionalArray(object value, string parameterName)
        {
            if (value == null)
                return null;

            if (value is JArray jArray)
                return jArray;

            if (value is JToken token)
            {
                if (token.Type == JTokenType.Null)
                    return null;
                if (token.Type == JTokenType.Array)
                    return (JArray)token;
                throw new ArgumentException($"{parameterName} must be a JSON array when supplied.");
            }

            if (value is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
                    return null;
                if (element.ValueKind != JsonValueKind.Array)
                    throw new ArgumentException($"{parameterName} must be a JSON array when supplied.");
                return JArray.Parse(element.GetRawText());
            }

            if (value is string)
                throw new ArgumentException($"{parameterName} must be a JSON array, not a string.");

            var tokenFromObject = JToken.FromObject(value);
            if (tokenFromObject.Type == JTokenType.Array)
                return (JArray)tokenFromObject;

            throw new ArgumentException($"{parameterName} must be a JSON array when supplied.");
        }

        public static JObject OptionalObject(object value, string parameterName)
        {
            if (value == null)
                return null;

            if (value is JObject jObject)
                return jObject;

            if (value is JToken token)
            {
                if (token.Type == JTokenType.Null)
                    return null;
                if (token.Type == JTokenType.Object)
                    return (JObject)token;
                throw new ArgumentException($"{parameterName} must be a JSON object when supplied.");
            }

            if (value is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
                    return null;
                if (element.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException($"{parameterName} must be a JSON object when supplied.");
                return JObject.Parse(element.GetRawText());
            }

            if (value is string)
                throw new ArgumentException($"{parameterName} must be a JSON object, not a string.");

            var tokenFromObject = JToken.FromObject(value);
            if (tokenFromObject.Type == JTokenType.Object)
                return (JObject)tokenFromObject;

            throw new ArgumentException($"{parameterName} must be a JSON object when supplied.");
        }
    }

    [McpServerToolType, Toolset("sheets")]
    public class SheetsTools
    {
        private static JArray NormalizeOptionalJsonArray(object value, string parameterName)
        {
            if (value == null)
                return null;

            if (value is JArray jArray)
                return jArray;

            if (value is JToken token)
            {
                if (token.Type == JTokenType.Null)
                    return null;
                if (token.Type == JTokenType.Array)
                    return (JArray)token;
                throw new ArgumentException($"{parameterName} must be a JSON array when supplied.");
            }

            if (value is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
                    return null;
                if (element.ValueKind != JsonValueKind.Array)
                    throw new ArgumentException($"{parameterName} must be a JSON array when supplied.");
                return JArray.Parse(element.GetRawText());
            }

            if (value is string)
                throw new ArgumentException($"{parameterName} must be a JSON array, not a string.");

            try
            {
                var tokenFromObject = JToken.FromObject(value);
                if (tokenFromObject.Type == JTokenType.Null)
                    return null;
                if (tokenFromObject.Type == JTokenType.Array)
                    return (JArray)tokenFromObject;
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"{parameterName} could not be converted to a JSON array: {ex.Message}");
            }

            throw new ArgumentException($"{parameterName} must be a JSON array when supplied.");
        }

        [McpServerTool(Name = "revit_create_sheet", Destructive = false), System.ComponentModel.Description("Create a new sheet with a titleblock")]
        public static async Task<string> CreateSheet(string sheetNumber, string sheetName, long? titleBlockTypeId = null, string titleBlockName = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_sheet", new
                {
                    sheet_number = sheetNumber,
                    sheet_name = sheetName,
                    title_block_type_id = titleBlockTypeId,
                    title_block_name = titleBlockName
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_duplicate_sheet", Destructive = false), System.ComponentModel.Description("Duplicate an existing sheet with viewport and schedule layout")]
        public static async Task<string> DuplicateSheet(string newSheetNumber, long? sourceSheetId = null, string sourceSheetNumber = "", string newSheetName = "", string duplicateViewOption = "with_detailing", bool includeSchedules = true, bool includeRevisions = true, bool reuseViewsWhenAllowed = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("duplicate_sheet", new
                {
                    source_sheet_id = sourceSheetId,
                    source_sheet_number = sourceSheetNumber,
                    new_sheet_number = newSheetNumber,
                    new_sheet_name = newSheetName,
                    duplicate_view_option = duplicateViewOption,
                    include_schedules = includeSchedules,
                    include_revisions = includeRevisions,
                    reuse_views_when_allowed = reuseViewsWhenAllowed
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_placeholder_sheet", Destructive = false), System.ComponentModel.Description("Create a new placeholder sheet")]
        public static async Task<string> CreatePlaceholderSheet(string sheetNumber, string sheetName)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_placeholder_sheet", new
                {
                    sheet_number = sheetNumber,
                    sheet_name = sheetName
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_sheets", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List sheets matching filters, with viewport and schedule counts, title blocks, and revisions")]
        public static async Task<string> ListSheets(string numberFilter = "", string namePattern = "", bool includeRevisions = true, bool includeViewports = false, bool includePlaceholders = true, int limit = 1000)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_sheets", new
                {
                    number_filter = numberFilter,
                    name_pattern = namePattern,
                    include_revisions = includeRevisions,
                    include_viewports = includeViewports,
                    include_placeholders = includePlaceholders,
                    limit
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_set_titleblock_parameters", Destructive = false), System.ComponentModel.Description("Set titleblock instance and type parameters for a sheet. parameters: object map of {paramName: value}.")]
        public static async Task<string> SetTitleblockParameters(IDictionary<string, object> parameters, long? sheetId = null, string sheetNumber = "", string target = "instance")
        {
            try
            {
                var parsedParameters = ToolbakerTools.NormalizeRunBakedToolParams(parameters);
                var result = await ToolGateway.SendToRevit("set_titleblock_parameters", new
                {
                    sheet_id = sheetId,
                    sheet_number = sheetNumber,
                    target,
                    parameters = parsedParameters
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_titleblock_parameters", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Get titleblock instance and type parameters for a sheet")]
        public static async Task<string> GetTitleblockParameters(long? sheetId = null, string sheetNumber = "", string target = "both", bool includeReadOnly = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_titleblock_parameters", new
                {
                    sheet_id = sheetId,
                    sheet_number = sheetNumber,
                    target,
                    include_read_only = includeReadOnly
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_titleblocks", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List loaded titleblock family types and count their sheet placements")]
        public static async Task<string> ListTitleblocks(string namePattern = "", bool includeInactive = true, int limit = 1000)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_titleblocks", new
                {
                    name_pattern = namePattern,
                    include_inactive = includeInactive,
                    limit
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_place_schedule_on_sheet", Destructive = false), System.ComponentModel.Description("Place a schedule on a sheet using sheet paper coordinates in millimeters")]
        public static async Task<string> PlaceScheduleOnSheet(double xMm, double yMm, long? sheetId = null, string sheetNumber = "", long? scheduleId = null, string scheduleName = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("place_schedule_on_sheet", new
                {
                    sheet_id = sheetId,
                    sheet_number = sheetNumber,
                    schedule_id = scheduleId,
                    schedule_name = scheduleName,
                    x_mm = xMm,
                    y_mm = yMm
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_revision", Destructive = false), System.ComponentModel.Description("Create a new document revision")]
        public static async Task<string> CreateRevision(string description, string date = "", string issuedTo = "", string issuedBy = "", bool issued = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_revision", new
                {
                    description,
                    date,
                    issued_to = issuedTo,
                    issued_by = issuedBy,
                    issued
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_assign_revision_to_sheet", Destructive = false), System.ComponentModel.Description("Assign or remove a revision on sheets")]
        public static async Task<string> AssignRevisionToSheet(long revisionId, long[] sheetIds = null, string[] sheetNumbers = null, string mode = "append")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("assign_revision_to_sheet", new
                {
                    revision_id = revisionId,
                    sheet_ids = sheetIds,
                    sheet_numbers = sheetNumbers,
                    mode
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_revisions", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List project revisions and optionally their assigned sheets")]
        public static async Task<string> ListRevisions(bool includeSheets = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_revisions", new
                {
                    include_sheets = includeSheets
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_renumber_sheets", Destructive = false), System.ComponentModel.Description("Bulk renumber/rename sheets with collision preflights and cyclic swap support")]
        public static async Task<string> RenumberSheets(object items = null, string find = "", string replace = "", string prefix = "", string suffix = "", bool dryRun = true)
        {
            try
            {
                var normalizedItems = NormalizeOptionalJsonArray(items, nameof(items));
                var result = await ToolGateway.SendToRevit("renumber_sheets", new
                {
                    items = normalizedItems,
                    find,
                    replace,
                    prefix,
                    suffix,
                    dry_run = dryRun
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("materials")]
    public class MaterialsTools
    {
        [McpServerTool(Name = "revit_list_materials", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List and filter materials in the active document")]
        public static async Task<string> ListMaterials(string namePattern = "", string classFilter = "", bool includeAssets = true, bool includeUseCount = false, int limit = 1000)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_materials", new
                {
                    name_pattern = namePattern,
                    class_filter = classFilter,
                    include_assets = includeAssets,
                    include_use_count = includeUseCount,
                    limit
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_material_properties", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Get detailed properties of a material by ID or name")]
        public static async Task<string> GetMaterialProperties(long? materialId = null, string materialName = "", bool includeAssets = true, bool includeParameters = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_material_properties", new
                {
                    material_id = materialId,
                    material_name = materialName,
                    include_assets = includeAssets,
                    include_parameters = includeParameters
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_material", Destructive = false), System.ComponentModel.Description("Create a new material with optional graphics parameters")]
        public static async Task<string> CreateMaterial(string name, string materialClass = "", string materialCategory = "", int? red = null, int? green = null, int? blue = null, int? transparency = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_material", new
                {
                    name,
                    material_class = materialClass,
                    material_category = materialCategory,
                    red,
                    green,
                    blue,
                    transparency
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_duplicate_material", Destructive = false), System.ComponentModel.Description("Duplicate an existing material with a new name")]
        public static async Task<string> DuplicateMaterial(string newName, long? sourceMaterialId = null, string sourceMaterialName = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("duplicate_material", new
                {
                    new_name = newName,
                    source_material_id = sourceMaterialId,
                    source_material_name = sourceMaterialName
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_set_material_appearance", Destructive = false), System.ComponentModel.Description("Set shading, transparency, and pattern assets for a material")]
        public static async Task<string> SetMaterialAppearance(long? materialId = null, string materialName = "", int? red = null, int? green = null, int? blue = null, int? transparency = null, int? shininess = null, int? smoothness = null, bool? useRenderAppearanceForShading = null, long? surfaceForegroundPatternId = null, long? surfaceBackgroundPatternId = null, long? cutForegroundPatternId = null, long? cutBackgroundPatternId = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_material_appearance", new
                {
                    material_id = materialId,
                    material_name = materialName,
                    red,
                    green,
                    blue,
                    transparency,
                    shininess,
                    smoothness,
                    use_render_appearance_for_shading = useRenderAppearanceForShading,
                    surface_foreground_pattern_id = surfaceForegroundPatternId,
                    surface_background_pattern_id = surfaceBackgroundPatternId,
                    cut_foreground_pattern_id = cutForegroundPatternId,
                    cut_background_pattern_id = cutBackgroundPatternId
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_set_material_identity", Destructive = false), System.ComponentModel.Description("Set identity parameters for a material")]
        public static async Task<string> SetMaterialIdentity(long? materialId = null, string materialName = "", string manufacturer = null, string model = null, string cost = null, string keynote = null, string mark = null, string url = null, string materialClass = null, string materialCategory = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_material_identity", new
                {
                    material_id = materialId,
                    material_name = materialName,
                    manufacturer,
                    model,
                    cost,
                    keynote,
                    mark,
                    url,
                    material_class = materialClass,
                    material_category = materialCategory
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_set_material_structural_asset", Destructive = false), System.ComponentModel.Description("Set or create a structural physical property asset for a material")]
        public static async Task<string> SetMaterialStructuralAsset(long? materialId = null, string materialName = "", string assetName = "", string structuralClass = "generic", double? densityKgPerM3 = null, double? youngModulusMpa = null, double? poissonRatio = null, double? shearModulusMpa = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_material_structural_asset", new
                {
                    material_id = materialId,
                    material_name = materialName,
                    asset_name = assetName,
                    structural_class = structuralClass,
                    density_kg_per_m3 = densityKgPerM3,
                    young_modulus_mpa = youngModulusMpa,
                    poisson_ratio = poissonRatio,
                    shear_modulus_mpa = shearModulusMpa
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_set_material_thermal_asset", Destructive = false), System.ComponentModel.Description("Set or create a thermal property asset for a material")]
        public static async Task<string> SetMaterialThermalAsset(long? materialId = null, string materialName = "", string assetName = "", double? conductivityWPerMK = null, double? specificHeatJPerKgK = null, double? emissivity = null, double? permeability = null, double? densityKgPerM3 = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_material_thermal_asset", new
                {
                    material_id = materialId,
                    material_name = materialName,
                    asset_name = assetName,
                    conductivity_w_per_m_k = conductivityWPerMK,
                    specific_heat_j_per_kg_k = specificHeatJPerKgK,
                    emissivity,
                    permeability,
                    density_kg_per_m3 = densityKgPerM3
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_assign_material_to_element", Destructive = false), System.ComponentModel.Description("Assign a material to one or more elements, optionally specifying parameter name or compound layer index")]
        public static async Task<string> AssignMaterialToElement(long[] elementIds, long? materialId = null, string materialName = "", string parameterName = "", int? compoundLayerIndex = null, bool allowTypeMutation = false, string duplicateTypeName = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("assign_material_to_element", new
                {
                    element_ids = elementIds,
                    material_id = materialId,
                    material_name = materialName,
                    parameter_name = parameterName,
                    compound_layer_index = compoundLayerIndex,
                    allow_type_mutation = allowTypeMutation,
                    duplicate_type_name = duplicateTypeName
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_material_takeoff", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Calculate material takeoff. output=file writes SQLite to a local same-machine absolute path with schema and bounded preview; remote clients receive preview only.")]
        public static async Task<string> GetMaterialTakeoff(string categoryFilter = "", string materialNamePattern = "", bool includeElements = false, int elementLimit = 100, string output = "inline")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_material_takeoff", new
                {
                    category_filter = categoryFilter,
                    material_name_pattern = materialNamePattern,
                    include_elements = includeElements,
                    element_limit = elementLimit,
                    output
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("geometry")]
    public class GeometryTools
    {
        [McpServerTool(Name = "revit_get_element_bounding_box", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Get the bounding box of one or more elements, optionally relative to a view, and optionally including transform data")]
        public static async Task<string> GetElementBoundingBox(long[] elementIds, long? viewId = null, bool includeTransform = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_element_bounding_box", new
                {
                    element_ids = elementIds,
                    view_id = viewId,
                    include_transform = includeTransform
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_element_geometry", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Get geometric details and optional vertex samples for one or more elements")]
        public static async Task<string> GetElementGeometry(long[] elementIds, string detailLevel = "Medium", bool includeSamples = false, int sampleLimit = 20)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_element_geometry", new
                {
                    element_ids = elementIds,
                    detail_level = detailLevel,
                    include_samples = includeSamples,
                    sample_limit = sampleLimit
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_measure_distance_between_elements", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Measure shortest distance between two elements based on bounding boxes, element locations, or bounding box pre-filter")]
        public static async Task<string> MeasureDistanceBetweenElements(long elementId1, long elementId2, string strategy = "bbox")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("measure_distance_between_elements", new
                {
                    element_id_1 = elementId1,
                    element_id_2 = elementId2,
                    strategy
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_clash_detection", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Detect clashes between elements of category A and category B. Scope with viewId and keep maxPairs low. Do not retry on 60s timeout; Revit may still be running the command.")]
        public static async Task<string> ClashDetection(string[] categoriesA, string[] categoriesB, long? viewId = null, string strategy = "bbox_then_solid", int maxPairs = 1000, int maxResults = 100)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("clash_detection", new
                {
                    categories_a = categoriesA,
                    categories_b = categoriesB,
                    view_id = viewId,
                    strategy,
                    max_pairs = maxPairs,
                    max_results = maxResults
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_raycast_from_point", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Cast a ray from a 3D origin point in a given direction to find the nearest element hit within a 3D view")]
        public static async Task<string> RaycastFromPoint(double x, double y, double z, double dirX, double dirY, double dirZ, long view3dId, string[] categories = null, double maxDistance = 100000.0)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("raycast_from_point", new
                {
                    x,
                    y,
                    z,
                    dir_x = dirX,
                    dir_y = dirY,
                    dir_z = dirZ,
                    view_3d_id = view3dId,
                    categories,
                    max_distance = maxDistance
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_find_elements_in_volume", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Find elements inside or intersecting an axis-aligned 3D volume or a room's bounding box")]
        public static async Task<string> FindElementsInVolume(object volume = null, long? roomId = null, string[] categories = null, long? viewId = null, string match = "intersects", int limit = 200)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("find_elements_in_volume", new
                {
                    volume,
                    room_id = roomId,
                    categories,
                    view_id = viewId,
                    match,
                    limit
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_compute_element_volume", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Compute the geometric solid volume of one or more elements in cubic meters (m3)")]
        public static async Task<string> ComputeElementVolume(long[] elementIds, string detailLevel = "Medium")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("compute_element_volume", new
                {
                    element_ids = elementIds,
                    detail_level = detailLevel
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_compute_element_area", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Compute the total face area of one or more elements in square meters (m2)")]
        public static async Task<string> ComputeElementArea(long[] elementIds, string detailLevel = "Medium")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("compute_element_area", new
                {
                    element_ids = elementIds,
                    detail_level = detailLevel
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_project_point_onto_face", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Project a 3D point onto a specific face of a Revit element")]
        public static async Task<string> ProjectPointOntoFace(long elementId, double x, double y, double z, int faceIndex = 0, string detailLevel = "Medium")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("project_point_onto_face", new
                {
                    element_id = elementId,
                    x,
                    y,
                    z,
                    face_index = faceIndex,
                    detail_level = detailLevel
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_find_overlapping_elements", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Find potentially overlapping elements of the same category, using bounding box intersection analysis")]
        public static async Task<string> FindOverlappingElements(string category, long? viewId = null, int maxPairs = 1000, int maxResults = 100)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("find_overlapping_elements", new
                {
                    category,
                    view_id = viewId,
                    max_pairs = maxPairs,
                    max_results = maxResults
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_element_centroid", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Compute the geometric centroid of one or more elements")]
        public static async Task<string> GetElementCentroid(long[] elementIds, string strategy = "solid_then_bbox")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_element_centroid", new
                {
                    element_ids = elementIds,
                    strategy
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_analyze_geometry_complexity", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Infer geometry complexity based on solid/face count and complexity metrics")]
        public static async Task<string> AnalyzeGeometryComplexity(long[] elementIds = null, string[] categories = null, long? viewId = null, string detailLevel = "Medium", int limit = 200)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("analyze_geometry_complexity", new
                {
                    element_ids = elementIds,
                    categories,
                    view_id = viewId,
                    detail_level = detailLevel,
                    limit
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("rooms")]
    public class RoomsTools
    {
        [McpServerTool(Name = "revit_list_rooms", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List all rooms in the model with status classification (placed, unplaced, not_enclosed) and optional level/phase filters.")]
        public static async Task<string> ListRooms(string levelName = "", string phaseName = "", string status = "all", bool includeParameters = false, int limit = 5000)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_rooms", new { level_name = levelName, phase_name = phaseName, status, include_parameters = includeParameters, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_room_boundaries", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Report room boundary segments (points in mm) and their bounding elements.")]
        public static async Task<string> GetRoomBoundaries(long roomId, string boundaryLocation = "finish", bool includeBoundaryElements = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_room_boundaries", new { room_id = roomId, boundary_location = boundaryLocation, include_boundary_elements = includeBoundaryElements });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_room_openings", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Retrieve doors and windows belonging to a room boundary.")]
        public static async Task<string> GetRoomOpenings(long roomId, bool includeDoors = true, bool includeWindows = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_room_openings", new { room_id = roomId, include_doors = includeDoors, include_windows = includeWindows });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_room_separator", Destructive = false), System.ComponentModel.Description("Create room separator lines from an array of {x,y,z} points (mm).")]
        public static async Task<string> CreateRoomSeparator(object[] points, long? viewId = null, string levelName = "", bool closeLoop = false)
        {
            try
            {
                var parsedPoints = McpJsonInput.RequiredArray(points, nameof(points));
                var result = await ToolGateway.SendToRevit("create_room_separator", new { points = parsedPoints, view_id = viewId, level_name = levelName, close_loop = closeLoop });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_area", Destructive = false), System.ComponentModel.Description("Create an area element at specified coordinates (mm) in an area plan.")]
        public static async Task<string> CreateArea(double x, double y, long? areaPlanViewId = null, string areaPlanViewName = "", string areaSchemeName = "", string levelName = "", bool createAreaPlanIfMissing = false, string name = "", string number = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_area", new { x, y, area_plan_view_id = areaPlanViewId, area_plan_view_name = areaPlanViewName, area_scheme_name = areaSchemeName, level_name = levelName, create_area_plan_if_missing = createAreaPlanIfMissing, name, number });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_space", Destructive = false), System.ComponentModel.Description("Create an MEP space element at specified coordinates (mm) on a target level.")]
        public static async Task<string> CreateSpace(double x, double y, string levelName = "", string phaseName = "", string name = "", string number = "")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_space", new { x, y, level_name = levelName, phase_name = phaseName, name, number });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_areas", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List area elements in the model with optional scheme/level filters.")]
        public static async Task<string> ListAreas(string areaSchemeName = "", string levelName = "", string status = "all", int limit = 5000)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_areas", new { area_scheme_name = areaSchemeName, level_name = levelName, status, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_compute_room_finishes", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Calculate room finishes and boundary materials. output=file writes relational SQLite to a local same-machine absolute path with schema and bounded preview; remote clients receive preview only.")]
        public static async Task<string> ComputeRoomFinishes(long[] roomIds = null, string levelName = "", bool includeEmpty = true, int limit = 5000, string output = "inline")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("compute_room_finishes", new { room_ids = roomIds, level_name = levelName, include_empty = includeEmpty, limit, output });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_auto_create_rooms_from_walls", Destructive = false), System.ComponentModel.Description("Automatically generate room elements in all enclosed boundary circuits found on the target level.")]
        public static async Task<string> AutoCreateRoomsFromWalls(string levelName, string phaseName = "", string namePrefix = "", string numberPrefix = "", int startNumber = 1, bool dryRun = true, int limit = 500)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("auto_create_rooms_from_walls", new { level_name = levelName, phase_name = phaseName, name_prefix = namePrefix, number_prefix = numberPrefix, start_number = startNumber, dry_run = dryRun, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_tag_all_areas", Destructive = false), System.ComponentModel.Description("Place area tags for a bounded batch of untagged areas. limit hard max 5000; response is a compact mutation summary.")]
        public static async Task<string> TagAllAreas(long? areaPlanViewId = null, string areaPlanViewName = "", bool skipExisting = true, long? tagTypeId = null, int limit = 500)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("tag_all_areas", new { area_plan_view_id = areaPlanViewId, area_plan_view_name = areaPlanViewName, skip_existing = skipExisting, tag_type_id = tagTypeId, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("links")]
    public class LinksTools
    {
        [McpServerTool(Name = "revit_list_linked_models", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List external Revit linked models and instances.")]
        public static async Task<string> ListLinkedModels(bool includeInstances = true, bool includeUnloaded = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_linked_models", new { include_instances = includeInstances, include_unloaded = includeUnloaded });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_linked_cad", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List imported or linked CAD files in the model.")]
        public static async Task<string> ListLinkedCad(bool includeImports = true, bool includeLinks = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_linked_cad", new { include_imports = includeImports, include_links = includeLinks });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_import_cad_to_view", Destructive = false), System.ComponentModel.Description("Import or link a CAD drawing (.dwg, .dxf) into a specific view.")]
        public static async Task<string> ImportCadToView(string path, long? viewId = null, bool link = false, string placement = "origin", string unit = "default", bool thisViewOnly = true, bool visibleLayersOnly = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("import_cad_to_view", new { path, view_id = viewId, link, placement, unit, this_view_only = thisViewOnly, visible_layers_only = visibleLayersOnly });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_link_revit_model", Destructive = false), System.ComponentModel.Description("Link a Revit model (.rvt) into the project.")]
        public static async Task<string> LinkRevitModel(string path, string placement = "origin", bool relative = false, bool reuseExistingType = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("link_revit_model", new { path, placement, relative, reuse_existing_type = reuseExistingType });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_unload_link", Destructive = false), System.ComponentModel.Description("Unload a Revit link type by type or instance ID.")]
        public static async Task<string> UnloadLink(long? linkTypeId = null, long? linkInstanceId = null, string scope = "all_users")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("unload_link", new { link_type_id = linkTypeId, link_instance_id = linkInstanceId, scope });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_reload_link", Destructive = false), System.ComponentModel.Description("Reload a Revit link type by type or instance ID.")]
        public static async Task<string> ReloadLink(long? linkTypeId = null, long? linkInstanceId = null)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("reload_link", new { link_type_id = linkTypeId, link_instance_id = linkInstanceId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_link_elements", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Retrieve elements from a Revit link instance document.")]
        public static async Task<string> GetLinkElements(long linkInstanceId, string category = "", int limit = 500, bool includeBoundingBox = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_link_elements", new { link_instance_id = linkInstanceId, category, limit, include_bounding_box = includeBoundingBox });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_project_coordinate_system", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Read the complete project coordinate setup: immutable Internal Origin, Project Base Point, Survey Point, active and named Project Locations, coordinates under each location, angle to True North, and geographic site data. Lengths are returned in mm; angles and latitude/longitude in degrees.")]
        public static async Task<string> GetProjectCoordinateSystem()
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_project_coordinate_system", new { });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_get_link_coordinate_system", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Inspect a Revit or CAD link's instance and total transforms and map its origins into host internal/shared coordinates. For a loaded Revit link, also returns its Internal Origin, Project Base Point, Survey Point, and all linked Project Locations with ids. Use the returned Project Location id as linkedProjectLocationId for revit_publish_coordinates_to_link.")]
        public static async Task<string> GetLinkCoordinateSystem(long linkInstanceId)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_link_coordinate_system", new { link_instance_id = linkInstanceId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_acquire_coordinates_from_link", Destructive = false), System.ComponentModel.Description("Acquire shared coordinates from a Revit link or linked CAD instance into the host project. Requires confirm=true and modifies the host shared-coordinate system.")]
        public static async Task<string> AcquireCoordinatesFromLink(long linkInstanceId, bool confirm = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("acquire_coordinates_from_link", new { link_instance_id = linkInstanceId, confirm });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_publish_coordinates_to_link", Destructive = false), System.ComponentModel.Description("Publish host shared coordinates to a loaded Revit link. Call revit_get_link_coordinate_system first to discover linkedProjectLocationId; omit it to target the linked active Project Location. Requires confirm=true. CAD links are not supported.")]
        public static async Task<string> PublishCoordinatesToLink(long linkInstanceId, long? linkedProjectLocationId = null, bool confirm = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("publish_coordinates_to_link", new { link_instance_id = linkInstanceId, linked_project_location_id = linkedProjectLocationId, confirm });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_set_project_base_point", Destructive = false), System.ComponentModel.Description("Set project base point or survey point parameters.")]
        public static async Task<string> SetProjectBasePoint(double eastWest, double northSouth, double elevation = 0, double angleToTrueNorth = 0, string pointKind = "project_base_point", bool dryRun = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_project_base_point", new { east_west = eastWest, north_south = northSouth, elevation, angle_to_true_north = angleToTrueNorth, point_kind = pointKind, dry_run = dryRun });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("parameters")]
    public class ParametersTools
    {
        [McpServerTool(Name = "revit_list_shared_parameters", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List all shared parameters defined in the shared parameter file. Returns guid, name, dataTypeId, isBound, bindingKind, categories.")]
        public static async Task<string> ListSharedParameters(string sharedParameterFilePath = "", string groupName = "", bool includeBindings = true, int limit = 1000)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_shared_parameters", new { sharedParameterFilePath, groupName, includeBindings, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_shared_parameter", Destructive = false), System.ComponentModel.Description("Create a shared parameter definition in the shared parameter file.")]
        public static async Task<string> CreateSharedParameter(string name, string dataTypeId, string groupName = "RvtMcp", string guid = "", string sharedParameterFilePath = "", bool createFileIfMissing = true, string description = "", bool visible = true, bool userModifiable = true, bool hideWhenNoValue = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_shared_parameter", new { name, dataTypeId, groupName, guid, sharedParameterFilePath, createFileIfMissing, description, visible, userModifiable, hideWhenNoValue });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_bind_shared_parameter", Destructive = false), System.ComponentModel.Description("Bind a shared parameter from the shared parameter file to categories in the project.")]
        public static async Task<string> BindSharedParameter(string guid, string[] categories, string bindingKind = "instance", string parameterGroupId = "autodesk.parameter.group:pg_data", string sharedParameterFilePath = "", bool allowRebind = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("bind_shared_parameter", new { guid, categories, bindingKind, parameterGroupId, sharedParameterFilePath, allowRebind });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_project_parameter", Destructive = false), System.ComponentModel.Description("Create a pure project parameter. Note: The public Revit API does not support non-shared project parameter creation; this command will fail explicitly stating it is unsupported.")]
        public static async Task<string> CreateProjectParameter(string name, string dataTypeId, string[] categories, string bindingKind = "instance", string parameterGroupId = "autodesk.parameter.group:pg_data")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_project_parameter", new { name, dataTypeId, categories, bindingKind, parameterGroupId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_project_parameter_bindings", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List all project parameter bindings in the document, including name, GUID (if shared), categories, and binding type.")]
        public static async Task<string> ListProjectParameterBindings(bool includeCategories = true, bool includeShared = true, bool includeProject = true, string nameFilter = "", string guid = "", int limit = 1000)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_project_parameter_bindings", new { includeCategories, includeShared, includeProject, nameFilter, guid, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_remove_parameter_binding", Destructive = true), System.ComponentModel.Description("Remove a parameter binding or specific categories from a binding in the document.")]
        public static async Task<string> RemoveParameterBinding(string name = "", string guid = "", string[] categories = null, bool removeAllCategories = false, bool dryRun = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("remove_parameter_binding", new { name, guid, categories, removeAllCategories, dryRun });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_export_shared_parameter_file", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Export a shared parameter file as structured DTO data. output=file writes JSON to a local same-machine absolute path with schema and bounded preview; remote clients receive preview only.")]
        public static async Task<string> ExportSharedParameterFile(string sharedParameterFilePath = "", string output = "inline")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("export_shared_parameter_file", new { sharedParameterFilePath, output });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_set_parameter_value_by_guid", Destructive = false), System.ComponentModel.Description("Set the value of a parameter by its shared GUID on one or more elements.")]
        public static async Task<string> SetParameterValueByGuid(long[] elementIds, string guid, string value, string valueType = "auto", string unit = "auto", string target = "auto", bool allOrNothing = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("set_parameter_value_by_guid", new { elementIds, guid, value, valueType, unit, target, allOrNothing });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("organization")]
    public class OrganizationTools
    {
        [McpServerTool(Name = "revit_list_view_templates", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List view templates in the document.")]
        public static async Task<string> ListViewTemplates(string viewType = "", long? viewId = null, bool includeSettings = true, bool includeUsage = false, int limit = 500)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_view_templates", new { viewType, viewId, includeSettings, includeUsage, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_create_view_template_from_view", Destructive = false), System.ComponentModel.Description("Create a new view template from an existing view.")]
        public static async Task<string> CreateViewTemplateFromView(string templateName, long? sourceViewId = null, long[] controlledSettingIds = null, long[] nonControlledSettingIds = null, bool failIfNameExists = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("create_view_template_from_view", new { templateName, sourceViewId, controlledSettingIds, nonControlledSettingIds, failIfNameExists });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_apply_view_template", Destructive = false), System.ComponentModel.Description("Apply or assign a view template to one or more views.")]
        public static async Task<string> ApplyViewTemplate(long templateId, long[] viewIds = null, string mode = "assign", bool replaceExisting = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("apply_view_template", new { templateId, viewIds, mode, replaceExisting });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_duplicate_view_template", Destructive = false), System.ComponentModel.Description("Duplicate an existing view template.")]
        public static async Task<string> DuplicateViewTemplate(long templateId, string newName)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("duplicate_view_template", new { templateId, newName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_delete_view_template", Destructive = true), System.ComponentModel.Description("Delete a view template. maxUsedByViews caps dependent-view/deleted-id response previews.")]
        public static async Task<string> DeleteViewTemplate(long templateId, bool dryRun = true, bool clearFromViews = false, int maxUsedByViews = 100)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("delete_view_template", new { templateId, dryRun, clearFromViews, max_used_by_views = maxUsedByViews });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_save_selection", Destructive = false), System.ComponentModel.Description("Save a named selection. IDs are omitted from the response by default; opt in with includeElementIds and maxElementIdResults.")]
        public static async Task<string> SaveSelection(string name, long[] elementIds = null, bool replaceExisting = false, bool useActiveSelectionIfIdsOmitted = true, bool includeElementIds = false, int maxElementIdResults = 100)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("save_selection", new { name, elementIds, replaceExisting, useActiveSelectionIfIdsOmitted, include_element_ids = includeElementIds, max_element_id_results = maxElementIdResults });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_load_selection", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Load a bounded page from a saved selection filter. Use startIndex + maxResults (hard max 1000).")]
        public static async Task<string> LoadSelection(string name = "", long? selectionId = null, bool includeElementSummary = false, int startIndex = 0, int maxResults = 200)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("load_selection", new { name, selectionId, includeElementSummary, start_index = startIndex, max_results = maxResults });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_list_saved_selections", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("List all saved selection filters in the document.")]
        public static async Task<string> ListSavedSelections(string nameFilter = "", bool includeElementIds = false, bool includeElementSummary = false, int limit = 500)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("list_saved_selections", new { nameFilter, includeElementIds, includeElementSummary, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_delete_saved_selection", Destructive = true), System.ComponentModel.Description("Delete a saved selection filter by name or ID.")]
        public static async Task<string> DeleteSavedSelection(string name = "", long? selectionId = null, bool dryRun = true)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("delete_saved_selection", new { name, selectionId, dryRun });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_select_elements", Destructive = false), System.ComponentModel.Description("Update active selection in the Revit UI. Runs pure UI selection mutation (must NOT run in transaction).")]
        public static async Task<string> SelectElements(long[] elementIds = null, string savedSelectionName = "", long? savedSelectionId = null, bool zoomToSelection = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("select_elements", new { elementIds, savedSelectionName, savedSelectionId, zoomToSelection });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    [McpServerToolType, Toolset("workflows")]
    public class WorkflowsTools
    {
        [McpServerTool(Name = "revit_workflow_clash_review", Destructive = false), System.ComponentModel.Description("Run clash detection, optionally create a review view, color clash hits, and add review markers with an auditable workflow report.")]
        public static async Task<string> WorkflowClashReview(string category_a, string category_b, long? view_id = null, int max_pairs = 200, bool create_review_view = true, bool color_hits = true, bool create_markers = false, bool dry_run = true, bool continue_on_error = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("workflow_clash_review", new { category_a, category_b, view_id, max_pairs, create_review_view, color_hits, create_markers, dry_run, continue_on_error });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_workflow_model_audit", ReadOnly = true, Idempotent = true), System.ComponentModel.Description("Run a read-only composite model audit covering warnings, families, views, schedules, and MEP connectivity signals.")]
        public static async Task<string> WorkflowModelAudit(bool include_warnings = true, bool include_families = true, bool include_views = true, bool include_schedules = true, bool include_mep = true, int limit_per_section = 100)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("workflow_model_audit", new { include_warnings, include_families, include_views, include_schedules, include_mep, limit_per_section });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_workflow_room_documentation", Destructive = false), System.ComponentModel.Description("Generate room documentation records, optional callouts, room tags, finish schedule, and sheet placement.")]
        public static async Task<string> WorkflowRoomDocumentation(long[] room_ids = null, string level_name = "", bool create_callouts = true, bool create_finish_schedule = true, bool tag_rooms = true, long? sheet_id = null, bool dry_run = true, int limit = 50)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("workflow_room_documentation", new { room_ids, level_name, create_callouts, create_finish_schedule, tag_rooms, sheet_id, dry_run, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_workflow_sheet_set", Destructive = false), System.ComponentModel.Description("Create a coordinated sheet set, place views and schedules, and set sheet parameters with dry-run and rollback reporting.")]
        public static async Task<string> WorkflowSheetSet(System.Collections.Generic.List<object> sheets, string renumber_strategy = "none", bool dry_run = true, bool continue_on_error = false)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("workflow_sheet_set", new { sheets, renumber_strategy, dry_run, continue_on_error });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_workflow_data_roundtrip", Destructive = false), System.ComponentModel.Description("Export/import category parameter data with validation. output=file writes the response report as NDJSON to a local same-machine absolute path with bounded preview; remote clients receive preview only.")]
        public static async Task<string> WorkflowDataRoundtrip(string category, string export_path, string import_path = "", string mode = "export_only", bool dry_run = true, string key_field = "element_id", string[] parameter_names = null, string output = "inline")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("workflow_data_roundtrip", new { category, export_path, import_path, mode, dry_run, key_field, parameter_names, output });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_workflow_view_cleanup", Destructive = true), System.ComponentModel.Description("Analyze unused views, empty schedules, and naming outliers, with guarded optional deletion of safe candidates.")]
        public static async Task<string> WorkflowViewCleanup(bool include_unused_views = true, bool include_empty_schedules = true, bool include_naming_outliers = true, bool delete_empty_views = false, bool dry_run = true, int limit = 200)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("workflow_view_cleanup", new { include_unused_views, include_empty_schedules, include_naming_outliers, delete_empty_views, dry_run, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_workflow_naming_normalization", Destructive = false), System.ComponentModel.Description("Analyze and optionally rename views, sheets, levels, and grids using deterministic normalization or a token pattern.")]
        public static async Task<string> WorkflowNamingNormalization(string target, string profile = "", string pattern = "", long[] ids = null, bool dry_run = true, int limit = 200)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("workflow_naming_normalization", new { target, profile, pattern, ids, dry_run, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_workflow_takeoff_report", Destructive = false), System.ComponentModel.Description("Generate category, quantity, material, and optional cost takeoff reports. output=file writes relational SQLite to a local same-machine absolute path with schema and bounded preview; remote clients receive preview only.")]
        public static async Task<string> WorkflowTakeoffReport(string[] categories = null, bool include_materials = true, bool include_quantities = true, bool include_cost = false, string output_path = "", int limit_per_category = 500, string output = "inline")
        {
            try
            {
                var result = await ToolGateway.SendToRevit("workflow_takeoff_report", new { categories, include_materials, include_quantities, include_cost, output_path, limit_per_category, output });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }

    /// <summary>
    /// KEI project SQLite tools — always go through the Revit plugin process
    /// (WAL + busy_timeout). Never replace .db files while Revit is open.
    ///
    /// Why: BIM is moving from human-only 3D/model work to AI agents helping
    /// deliver projects faster and more accurately. Project data lives in a
    /// SQLite DB that Revit holds; agents must read/write via these tools so
    /// they share the same process lock instead of fighting the file from outside.
    /// </summary>
    [McpServerToolType, Toolset("kei")]
    public class KeiTools
    {
        [McpServerTool(Name = "revit_get_active_project_db", ReadOnly = true, Idempotent = true),
         System.ComponentModel.Description(
             "Resolve the KEI project SQLite path for the active Revit document " +
             "(%APPDATA%\\KEI\\Database\\Projects\\*_local.db). Reports WAL size. Use before import/query/write.")]
        public static async Task<string> GetActiveProjectDb()
        {
            try
            {
                var result = await ToolGateway.SendToRevit("get_active_project_db");
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_query_kei_database", ReadOnly = true, Idempotent = true),
         System.ComponentModel.Description(
             "Read-only SELECT/PRAGMA on the KEI project SQLite DB via Revit (WAL-safe). " +
             "preset=overview|equipment|schema|categories; or sql=SELECT...; database=auto|list|filename-substring; limit=100.")]
        public static async Task<string> QueryKeiDatabase(
            string preset = "",
            string sql = "",
            string database = "auto",
            int limit = 100)
        {
            try
            {
                var result = await ToolGateway.SendToRevit("query_kei_database", new { preset, sql, database, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_write_kei_database", Destructive = false),
         System.ComponentModel.Description(
             "Write DML (INSERT/UPDATE/DELETE/REPLACE or WITH…DML) into the KEI project SQLite DB " +
             "through the Revit process (journal_mode=WAL, busy_timeout). Broader than equipment-only import — " +
             "any project table. AI agents use this so project data work can run while Revit holds the DB; " +
             "do NOT kill Revit or replace .db/.db-wal. sql= one statement OR statements= JSON array of SQL strings. " +
             "dryRun=true validates only; database=auto|filename-substring; busyTimeoutMs default 30000. " +
             "DDL (CREATE/DROP/ALTER/PRAGMA/ATTACH) is blocked. Prefer revit_import_project_equipment for typed equipment bulk import.")]
        public static async Task<string> WriteKeiDatabase(
            string sql = "",
            string statements = "",
            bool dryRun = false,
            string database = "auto",
            int busyTimeoutMs = 30000)
        {
            try
            {
                object statementsPayload = null;
                if (!string.IsNullOrWhiteSpace(statements))
                    statementsPayload = JArray.Parse(statements);

                if (string.IsNullOrWhiteSpace(sql) && statementsPayload == null)
                    return JsonConvert.SerializeObject(new
                    {
                        error = "Provide sql (one DML statement) and/or statements (JSON array of DML strings)."
                    });

                var result = await ToolGateway.SendToRevit("write_kei_database", new
                {
                    sql = string.IsNullOrWhiteSpace(sql) ? null : sql,
                    statements = statementsPayload,
                    dryRun,
                    database,
                    busyTimeoutMs
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "revit_import_project_equipment", Destructive = false),
         System.ComponentModel.Description(
             "Bulk-import equipment types + instances + typed specs into KEI SQLite through the Revit process " +
             "(journal_mode=WAL, busy_timeout). Do NOT kill Revit or replace .db files. " +
             "items = JSON array of {projectTypeName, categoryCode, nameVN, nameEN, specsVN, specsEN, area, brand, unit, " +
             "originalTag, specText, status, quantity, instanceTags[], specs:[{parameterCode,value}]}. " +
             "dryRun=true validates only; replaceMatching=true upserts type and recreates its instances/specs; " +
             "clearAiExtracted=true deletes all Status=AIExtracted types first; busyTimeoutMs default 30000. " +
             "For arbitrary project-table DML use revit_write_kei_database.")]
        public static async Task<string> ImportProjectEquipment(
            string items,
            bool dryRun = false,
            bool replaceMatching = true,
            bool clearAiExtracted = false,
            string database = "auto",
            int busyTimeoutMs = 30000)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(items))
                    return JsonConvert.SerializeObject(new { error = "items JSON array string is required" });

                var itemsArr = JArray.Parse(items);
                var result = await ToolGateway.SendToRevit("import_project_equipment", new
                {
                    items = itemsArr,
                    dryRun,
                    replaceMatching,
                    clearAiExtracted,
                    database,
                    busyTimeoutMs
                });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }
    }
}
