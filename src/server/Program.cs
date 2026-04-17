// Usage:
//   stdio (default):  Bimwright.Server.exe              — spawned by Claude/GPT/Cursor
//   HTTP SSE:          Bimwright.Server.exe --http 8200  — for Ollama/LM Studio/custom
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bimwright.Server
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // Parse --target R22|R23|R24|R25|R26|R27 for multi-Revit support
            int targetIndex = Array.IndexOf(args, "--target");
            if (targetIndex >= 0 && targetIndex + 1 < args.Length)
            {
                var target = args[targetIndex + 1].ToUpperInvariant();
                if (Array.IndexOf(AuthToken.AllVersions, target) < 0)
                {
                    Console.Error.WriteLine("[Bimwright] Invalid --target argument. Expected: --target R22|R23|R24|R25|R26|R27");
                    Environment.Exit(1);
                    return;
                }
                AuthToken.Target = target;
            }

            // Initialize memory system
            var session = new Memory.SessionContext();
            RevitTools.Session = session;
            RevitResources.Session = session;

            int httpIndex = Array.IndexOf(args, "--http");
            if (httpIndex >= 0)
            {
                if (httpIndex + 1 >= args.Length || !int.TryParse(args[httpIndex + 1], out var port)
                    || port < 1 || port > 65535)
                {
                    Console.Error.WriteLine("[Bimwright] Invalid --http argument. Expected: --http <port> (1-65535)");
                    Environment.Exit(1);
                    return;
                }
                await RunHttpSse(port);
            }
            else
            {
                await RunStdio();
            }
        }

        private static async Task RunStdio()
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services
                .AddMcpServer()
                .WithStdioServerTransport()
                .WithTools<RevitTools>()
                .WithPrompts<RevitPrompts>()
                .WithResources<RevitResources>();
            var app = builder.Build();
            await app.RunAsync();
        }

        private static async Task RunHttpSse(int port)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Services
                .AddMcpServer()
                .WithHttpTransport()
                .WithTools<RevitTools>()
                .WithPrompts<RevitPrompts>()
                .WithResources<RevitResources>();

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

            Console.Error.WriteLine($"[Bimwright] SSE server listening on http://127.0.0.1:{port}");
            await app.RunAsync();
        }
    }

    [McpServerToolType]
    public class RevitTools
    {
        internal static Memory.SessionContext Session { get; set; }

        private static TcpClient _client;
        private static NamedPipeClientStream _pipeStream;
        private static StreamReader _reader;
        private static StreamWriter _writer;
        private static readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new ConcurrentDictionary<string, TaskCompletionSource<string>>();
        private static readonly object _connectLock = new object();
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

                var target = AuthToken.Target; // null = auto, "R22"-"R27" = specific version

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
                        _pipeStream = pipe;
                        stream = pipe;
                        Console.Error.WriteLine($"[Bimwright] Connected to Revit {pipeVer} via Named Pipe: {pipeName}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[Bimwright] Pipe connect failed ({pipeVer}: {ex.Message}) — falling back to TCP");
                        try { _pipeStream?.Close(); } catch { }
                        _pipeStream = null;
                    }
                }

                // Fall back to TCP (R22-R24) if pipe did not connect.
                if (stream == null && AuthToken.TryReadTcp(out var port, out var tcpToken, out var tcpVer))
                {
                    _token = tcpToken;
                    _client = new TcpClient();
                    _client.Connect("127.0.0.1", port);
                    stream = _client.GetStream();
                    Console.Error.WriteLine($"[Bimwright] Connected to Revit {tcpVer} via TCP on port {port}");
                }

                if (stream == null)
                {
                    var which = target != null ? $"(target={target})" : "(auto-detect R22-R27)";
                    throw new InvalidOperationException(
                        $"Revit MCP plugin not running {which}. Check discovery files in %LOCALAPPDATA%\\Bimwright\\");
                }

                _reader = new StreamReader(stream, Encoding.UTF8);
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                _connected = true;

                // Start reading responses on background thread
                var readThread = new Thread(ReadLoop) { IsBackground = true, Name = "Bimwright.ResponseReader" };
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

        /// <summary>Public accessor for SendToRevit — used by RevitPrompts.</summary>
        public static Task<JObject> SendToRevitPublic(string command, object parameters = null)
            => SendToRevit(command, parameters);

        private static async Task<JObject> SendToRevit(string command, object parameters = null)
        {
            EnsureConnected();

            var id = $"req-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid().ToString("N").Substring(0, 6)}";
            var request = JsonConvert.SerializeObject(new { id, command, @params = parameters ?? new { }, token = _token });

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
                var paramsStr = parameters != null ? JsonConvert.SerializeObject(parameters) : null;
                Session?.RecordCall(command, paramsStr, false, sw.ElapsedMilliseconds, "Timeout (60s)");
                throw new TimeoutException("Request timed out (60s). Revit may be in a modal dialog.");
            }

            sw.Stop();
            var responseLine = await tcs.Task;
            var response = JObject.Parse(responseLine);
            var paramsJson = parameters != null ? JsonConvert.SerializeObject(parameters) : null;

            if (response.Value<bool>("success"))
            {
                var data = response["data"] as JObject ?? new JObject();
                Session?.RecordCall(command, paramsJson, true, sw.ElapsedMilliseconds,
                    resultJson: data.ToString(Formatting.None));
                return data;
            }
            else
            {
                var error = response.Value<string>("error") ?? "Unknown error from Revit";
                Session?.RecordCall(command, paramsJson, false, sw.ElapsedMilliseconds, error);
                throw new InvalidOperationException(error);
            }
        }

        [McpServerTool(Name = "show_message"), System.ComponentModel.Description("Display a TaskDialog inside Revit with an optional custom message. Use for connection testing, user notifications, or feedback during automation flows. Both 'message' and 'title' are optional — omit for default greeting.")]
        public static async Task<string> ShowMessage(string message = null, string title = null)
        {
            try
            {
                object parameters = null;
                if (!string.IsNullOrWhiteSpace(message) || !string.IsNullOrWhiteSpace(title))
                {
                    parameters = new { message, title };
                }
                var result = await SendToRevit("show_message", parameters);
                return JsonConvert.SerializeObject(result);
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        [McpServerTool(Name = "get_current_view_info"), System.ComponentModel.Description("Get current active view info. Returns: viewName, viewType (FloorPlan/Section/3D/Sheet), level, scale, detailLevel, displayStyle. Use before creating elements to know which level/view is active.")]
        public static async Task<string> GetCurrentViewInfo()
        {
            try
            {
                var result = await SendToRevit("get_current_view_info");
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        [McpServerTool(Name = "get_selected_elements"), System.ComponentModel.Description("Get currently selected elements in Revit. Returns array of {id, name, category, typeName}. Use to inspect what user has selected before operating on elements (color, delete, move).")]
        public static async Task<string> GetSelectedElements()
        {
            try
            {
                var result = await SendToRevit("get_selected_elements");
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        [McpServerTool(Name = "get_available_family_types"), System.ComponentModel.Description("Get available family types in current project. Returns {familyName, typeName, typeId} grouped by category. Optional: filter by category name (e.g. 'Walls', 'Doors', 'Pipes'). Use typeId from results when calling create_point_based_element.")]
        public static async Task<string> GetAvailableFamilyTypes(string category = "")
        {
            try
            {
                var result = await SendToRevit("get_available_family_types", new { category });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        [McpServerTool(Name = "ai_element_filter"), System.ComponentModel.Description("Filter elements by category and parameter. Numeric values in mm (auto-converted). Operators: equals/contains/startswith/greaterthan/lessthan. Set select=true to auto-select results in Revit. Example: category='Pipes', parameterName='Diameter', parameterValue='200', operator='greaterthan', select=true")]
        public static async Task<string> AiElementFilter(string category, string parameterName = "", string parameterValue = "", string @operator = "equals", int limit = 100, bool select = false)
        {
            try
            {
                var result = await SendToRevit("ai_element_filter", new { category, parameterName, parameterValue, @operator, limit, select });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        [McpServerTool(Name = "analyze_model_statistics"), System.ComponentModel.Description("Analyze model: element counts grouped by category (Walls, Doors, Pipes, etc.). Use to understand project scope before detailed queries.")]
        public static async Task<string> AnalyzeModelStatistics()
        {
            try
            {
                var result = await SendToRevit("analyze_model_statistics");
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        [McpServerTool(Name = "get_material_quantities"), System.ComponentModel.Description("Calculate material quantities (area m², volume m³) from elements by category. Required: category (e.g. 'Walls', 'Floors').")]
        public static async Task<string> GetMaterialQuantities(string category)
        {
            try
            {
                var result = await SendToRevit("get_material_quantities", new { category });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        // --- Phase 3: Create Elements ---

        [McpServerTool(Name = "create_line_based_element"), System.ComponentModel.Description("Create line-based elements (wall). Params: elementType, startX/Y, endX/Y (mm), level (name), typeId (optional), height (mm, default 3000).")]
        public static async Task<string> CreateLineBasedElement(string elementType, double startX, double startY, double endX, double endY, string level = "", long? typeId = null, double height = 3000)
        {
            try
            {
                var result = await SendToRevit("create_line_based_element", new { elementType, startX, startY, endX, endY, level, typeId, height });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "create_point_based_element"), System.ComponentModel.Description("Create point-based elements (door, window, furniture). Params: typeId (from get_available_family_types), x/y/z (mm), level (name).")]
        public static async Task<string> CreatePointBasedElement(long typeId, double x, double y, double z = 0, string level = "")
        {
            try
            {
                var result = await SendToRevit("create_point_based_element", new { typeId, x, y, z, level });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "create_surface_based_element"), System.ComponentModel.Description("Create surface-based elements (floor, ceiling). Params: elementType, points (array of {x,y} in mm, min 3), level (name), typeId (optional).")]
        public static async Task<string> CreateSurfaceBasedElement(string elementType, string points, string level = "", long? typeId = null)
        {
            try
            {
                var parsedPoints = JArray.Parse(points);
                var result = await SendToRevit("create_surface_based_element", new { elementType, points = parsedPoints, level, typeId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "create_level"), System.ComponentModel.Description("Create a level at specified elevation. Params: elevation (mm), name (optional).")]
        public static async Task<string> CreateLevel(double elevation, string name = "")
        {
            try
            {
                var result = await SendToRevit("create_level", new { elevation, name });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "create_grid"), System.ComponentModel.Description("Create a grid line. Params: startX/Y, endX/Y (mm), name (optional).")]
        public static async Task<string> CreateGrid(double startX, double startY, double endX, double endY, string name = "")
        {
            try
            {
                var result = await SendToRevit("create_grid", new { startX, startY, endX, endY, name });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "create_room"), System.ComponentModel.Description("Create and place a room. Params: x/y (mm), level (name), name (optional), number (optional).")]
        public static async Task<string> CreateRoom(double x, double y, string level = "", string name = "", string number = "")
        {
            try
            {
                var result = await SendToRevit("create_room", new { x, y, level, name, number });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        // --- Phase 4: Modify & Delete ---

        [McpServerTool(Name = "operate_element"), System.ComponentModel.Description("Operate on elements in current view. operation: select (highlight in UI), hide, unhide, isolate (hide everything else), setcolor (override graphics with RGB). elementIds: JSON int array e.g. '[12345, 67890]'. For setcolor: r/g/b 0-255 (default red 255,0,0).")]
        public static async Task<string> OperateElement(string operation, string elementIds, byte r = 255, byte g = 0, byte b = 0)
        {
            try
            {
                var parsedIds = JArray.Parse(elementIds).ToObject<long[]>();
                var result = await SendToRevit("operate_element", new { operation, elementIds = parsedIds, r, g, b });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "color_elements"), System.ComponentModel.Description("Color-code elements by parameter value in current view. Auto-assigns distinct colors per unique value. Example: category='Pipes', parameterName='System Type' → each system type gets a different color. category='Walls', parameterName='Type' → each wall type colored differently.")]
        public static async Task<string> ColorElements(string category, string parameterName)
        {
            try
            {
                var result = await SendToRevit("color_elements", new { category, parameterName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "delete_element"), System.ComponentModel.Description("Delete elements by ID. DESTRUCTIVE — cannot be undone via MCP. elementIds: JSON int array e.g. '[12345]'. Get IDs from get_selected_elements or ai_element_filter first.")]
        public static async Task<string> DeleteElement(string elementIds)
        {
            try
            {
                var parsedIds = JArray.Parse(elementIds).ToObject<long[]>();
                var result = await SendToRevit("delete_element", new { elementIds = parsedIds });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        // --- Phase 5: Export & Tags ---

        [McpServerTool(Name = "export_room_data"), System.ComponentModel.Description("Export all room data from project. Returns array of {name, number, area (m²), perimeter, level, department, volume (m³)}. Use for space analysis and reporting.")]
        public static async Task<string> ExportRoomData()
        {
            try
            {
                var result = await SendToRevit("export_room_data");
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "tag_all_walls"), System.ComponentModel.Description("Tag all walls in current view with wall type tags at midpoint. Skips already-tagged walls. Returns count of new tags placed.")]
        public static async Task<string> TagAllWalls()
        {
            try
            {
                var result = await SendToRevit("tag_all_walls");
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "tag_all_rooms"), System.ComponentModel.Description("Tag all rooms in current view with room tags at location point. Skips already-tagged rooms. Returns count of new tags placed.")]
        public static async Task<string> TagAllRooms()
        {
            try
            {
                var result = await SendToRevit("tag_all_rooms");
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        // --- Phase 6: Dynamic Code Execution ---

        [McpServerTool(Name = "send_code_to_revit"), System.ComponentModel.Description("LAST RESORT — use query_kei_database or other MCP tools first. Send C# code to Revit for dynamic compilation and execution. Risk: can crash Revit or corrupt data. Variables available: doc (Document), uidoc (UIDocument), app (UIApplication). Write code body only — auto-wrapped in static Run(UIApplication). Must end with 'return ...;'. Available namespaces: System, System.Linq, System.Collections.Generic, Autodesk.Revit.DB, Autodesk.Revit.UI. Common patterns: FilteredElementCollector for querying elements, Transaction for model modifications, UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.Millimeters) for unit conversion, uidoc.Selection.SetElementIds() to select elements, OverrideGraphicSettings for visual overrides.")]
        public static async Task<string> SendCodeToRevit(string code)
        {
            try
            {
                var result = await SendToRevit("send_code_to_revit", new { code });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        // --- ToolBaker: Self-evolution engine ---

        [McpServerTool(Name = "bake_tool"), System.ComponentModel.Description(
            "Bake a new permanent tool from C# code. The code has access to: app (UIApplication), doc (Document), " +
            "uidoc (UIDocument), request (JObject parsed from paramsJson). Code must return CommandResult.Ok(data) or CommandResult.Fail(error). " +
            "Parameters: name (string, alphanumeric+underscore), description (string), code (string, C# method body), " +
            "parametersSchema (string, optional JSON schema). Debug builds only.")]
        public static async Task<string> BakeTool(string name, string description, string code, string parametersSchema = "{}")
        {
            try
            {
                var result = await SendToRevit("bake_tool", new { name, description, code, parametersSchema });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "list_baked_tools"), System.ComponentModel.Description(
            "List all baked (user-compiled) tools with name, description, usage count, and creation date. " +
            "Use this to discover available baked tools before calling run_baked_tool.")]
        public static async Task<string> ListBakedTools()
        {
            try
            {
                var result = await SendToRevit("list_baked_tools");
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "run_baked_tool"), System.ComponentModel.Description(
            "Execute a baked tool by name. Use list_baked_tools first to see available tools. " +
            "Parameters: name (string, the baked tool name), params (object, tool-specific parameters).")]
        public static async Task<string> RunBakedTool(string name, string @params = "{}")
        {
            try
            {
                var result = await SendToRevit("run_baked_tool", new { name, @params });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        // --- Phase 7: Views & Sheets ---

        [McpServerTool(Name = "create_view"), System.ComponentModel.Description("Create a view. Params: viewType (floorplan, 3d), level (name, for floorplan), name (optional).")]
        public static async Task<string> CreateView(string viewType, string level = "", string name = "")
        {
            try
            {
                var result = await SendToRevit("create_view", new { viewType, level, name });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        [McpServerTool(Name = "place_view_on_sheet"), System.ComponentModel.Description("Place a view on a sheet. Creates new sheet if sheetId not provided. Params: viewId (required), sheetId (optional), sheetNumber (optional), sheetName (optional).")]
        public static async Task<string> PlaceViewOnSheet(long viewId, long? sheetId = null, string sheetNumber = "", string sheetName = "MCP Generated Sheet")
        {
            try
            {
                var result = await SendToRevit("place_view_on_sheet", new { viewId, sheetId, sheetNumber, sheetName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        // --- Phase 10: New tools ---

        [McpServerTool(Name = "detect_system_elements"), System.ComponentModel.Description("Detect all elements in a MEP system from a seed element ID. Traverses connectors to find all pipes, fittings, accessories, and equipment in the same system. Returns element IDs grouped by category and bounding box in mm. Use get_selected_elements first to get an element ID.")]
        public static async Task<string> DetectSystemElements(long elementId)
        {
            try
            {
                var result = await SendToRevit("detect_system_elements", new { elementId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        [McpServerTool(Name = "analyze_sheet_layout"), System.ComponentModel.Description("Analyze a sheet's title block dimensions and viewport positions/scales in mm. Provide sheetNumber (e.g. 'ISO-005') or sheetId. If neither, uses active view when it is a sheet. Returns title block size, viewport centers, widths, heights, and scales.")]
        public static async Task<string> AnalyzeSheetLayout(string sheetNumber = "", long? sheetId = null)
        {
            try
            {
                var result = await SendToRevit("analyze_sheet_layout", new { sheetNumber, sheetId });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }
        // --- KEI BOQ / Flow Analysis ---

        [McpServerTool(Name = "flow_sort_system"), System.ComponentModel.Description(
            "Sort supply items in a piping system by flow order using graph traversal on KEI database. " +
            "Returns items grouped into chains (main trunk + branches) with full supply data " +
            "(TenVatTu, KichThuoc, VatLieu, TieuChuan, DonVi, SoLuong). " +
            "Input: systemName (exact match from Systems table, e.g. '1.02_WW_PUMP_TK01 to FS02-01'). " +
            "Use query_kei_database preset='systems' first to list available system names.")]
        public static async Task<string> FlowSortSystem(string systemName)
        {
            try
            {
                var result = await SendToRevit("flow_sort_system", new { systemName });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        [McpServerTool(Name = "save_port_declaration"), System.ComponentModel.Description(
            "Save, delete, or list EquipmentPortDeclarations for GraphService v2 flow direction. " +
            "Each declaration maps an equipment's OUT port to the element connected on its output side, per system. " +
            "Actions: 'save' (upsert), 'delete' (remove), 'list' (view declarations, optionally filtered by systemId). " +
            "For 'save': requires projectEquipmentId, systemId, outPortConnectedToElementId. " +
            "For 'delete': requires projectEquipmentId, systemId. " +
            "For 'list': optional systemId filter. " +
            "Validates FK references before saving. Returns declaration with context (TagNumber, SystemName, element info).")]
        public static async Task<string> SavePortDeclaration(
            string action = "save",
            int projectEquipmentId = 0,
            int systemId = 0,
            long outPortConnectedToElementId = 0,
            string database = "auto")
        {
            try
            {
                var result = await SendToRevit("save_port_declaration", new { action, projectEquipmentId, systemId, outPortConnectedToElementId, database });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        // --- KEI Diagnostics (server-side, no Revit needed) ---

        private static string GetKeiLogsFolder()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "KEI", "Logs");
        }

        [McpServerTool(Name = "read_kei_logs"), System.ComponentModel.Description(
            "Read KEI add-in log files from %APPDATA%/KEI/Logs/. Does NOT require Revit connection. " +
            "Use to diagnose KEI errors, crashes, or unexpected behavior. " +
            "Params: date (yyyy-MM-dd, default today), lines (last N lines, default 100), " +
            "level (filter: Error/Warn/Info/Debug), search (keyword filter). " +
            "When called with no params, returns today's last 100 log lines. " +
            "Call with level='Error' to see only errors. Call with date='list' to see available log files.")]
        public static Task<string> ReadKeiLogs(string date = "", int lines = 100, string level = "", string search = "")
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var paramsJson = JsonConvert.SerializeObject(new { date, lines, level, search });
            try
            {
                var logsFolder = GetKeiLogsFolder();

                if (!Directory.Exists(logsFolder))
                    return Task.FromResult(JsonConvert.SerializeObject(new
                    {
                        error = "KEI logs folder not found",
                        expectedPath = logsFolder
                    }));

                // List available log files
                if (date?.Equals("list", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var files = Directory.GetFiles(logsFolder, "KEI_*.log")
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.LastWriteTime)
                        .Select(f => new
                        {
                            name = f.Name,
                            date = f.Name.Replace("KEI_", "").Replace(".log", ""),
                            sizeKB = Math.Round(f.Length / 1024.0, 1),
                            lastModified = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                        })
                        .ToList();

                    // Also check for IUpdater error log
                    var updaterLog = Path.Combine(logsFolder, "IUpdater_ErrorLog.txt");
                    var hasUpdaterLog = File.Exists(updaterLog);

                    return Task.FromResult(JsonConvert.SerializeObject(new
                    {
                        logsFolder,
                        logFiles = files,
                        updaterErrorLog = hasUpdaterLog ? "IUpdater_ErrorLog.txt exists" : null
                    }, Formatting.Indented));
                }

                // Determine which file to read
                var targetDate = string.IsNullOrEmpty(date)
                    ? DateTime.Now.ToString("yyyy-MM-dd")
                    : date;
                var logFile = Path.Combine(logsFolder, $"KEI_{targetDate}.log");

                if (!File.Exists(logFile))
                {
                    // Try IUpdater_ErrorLog.txt as fallback if searching for errors
                    var updaterLogPath = Path.Combine(logsFolder, "IUpdater_ErrorLog.txt");
                    var availableFiles = Directory.GetFiles(logsFolder, "KEI_*.log")
                        .Select(Path.GetFileName)
                        .OrderByDescending(f => f)
                        .Take(5)
                        .ToList();

                    return Task.FromResult(JsonConvert.SerializeObject(new
                    {
                        error = $"Log file not found: KEI_{targetDate}.log",
                        recentFiles = availableFiles,
                        hasUpdaterErrorLog = File.Exists(updaterLogPath),
                        hint = "Use date='list' to see all available log files"
                    }, Formatting.Indented));
                }

                // Read file (shared read, KEI may be writing)
                string[] allLines;
                using (var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    allLines = sr.ReadToEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                }

                IEnumerable<string> filtered = allLines.Where(l => !string.IsNullOrWhiteSpace(l));

                // Filter by level
                if (!string.IsNullOrEmpty(level))
                    filtered = filtered.Where(l => l.Contains($"[{level}]", StringComparison.OrdinalIgnoreCase));

                // Filter by search keyword
                if (!string.IsNullOrEmpty(search))
                    filtered = filtered.Where(l => l.Contains(search, StringComparison.OrdinalIgnoreCase));

                var result = filtered.ToList();
                var totalMatches = result.Count;

                // Take last N lines
                var output = result.Skip(Math.Max(0, result.Count - lines)).ToList();

                var resultJson = JsonConvert.SerializeObject(new
                {
                    file = $"KEI_{targetDate}.log",
                    totalLines = allLines.Length,
                    matchingLines = totalMatches,
                    returnedLines = output.Count,
                    filters = new
                    {
                        level = string.IsNullOrEmpty(level) ? "all" : level,
                        search = string.IsNullOrEmpty(search) ? "none" : search,
                        lastN = lines
                    },
                    lines = output
                }, Formatting.Indented);
                sw.Stop();
                Session?.RecordCall("read_kei_logs", paramsJson, true, sw.ElapsedMilliseconds, resultJson: resultJson);
                return Task.FromResult(resultJson);
            }
            catch (Exception ex)
            {
                sw.Stop();
                Session?.RecordCall("read_kei_logs", paramsJson, false, sw.ElapsedMilliseconds, ex.Message);
                return Task.FromResult($"Error: {ex.Message}");
            }
        }
        // --- KEI Database (routed to Plugin for logging + correct project detection) ---

        [McpServerTool(Name = "query_kei_database"), System.ComponentModel.Description(
            "Query KEI project SQLite database. Read-only. Requires Revit connection. " +
            "Auto-detects the active project database from the current Revit document. " +
            "Use 'preset' for common queries: overview (table counts), supply_sample (20 recent items), " +
            "systems (system types), change_queue (recent 50), unprocessed (pending changes), " +
            "schema (all tables+columns), categories (element categories), recent_changes (last 30 with details). " +
            "Or pass custom 'sql' (SELECT only). 'database' defaults to auto-detect, or pass 'list' to see all DBs. " +
            "Use 'limit' to cap rows (default 100).")]
        public static async Task<string> QueryKeiDatabase(string preset = "", string sql = "", string database = "auto", int limit = 100)
        {
            try
            {
                var result = await SendToRevit("query_kei_database", new { preset, sql, database, limit });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { error = ex.Message, type = ex.GetType().Name });
            }
        }

        // ── KEI Equipment Management (Phase 4) ──────────────────────────────

        [McpServerTool(Name = "manage_equipment_categories"), System.ComponentModel.Description(
            "Discover equipment category templates and their typed parameters. " +
            "Use list_categories to see all 13 MEP categories. " +
            "Use get_parameters to see required/optional params for a category (with units and conversion factors). " +
            "Use suggest_category to match an equipment name to the best category.")]
        public static async Task<string> ManageEquipmentCategories(
            string action = "list_categories",
            string categoryCode = "",
            string equipmentName = "",
            string database = "auto")
        {
            try
            {
                var result = await SendToRevit("manage_equipment_categories", new { action, categoryCode, equipmentName, database });
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { error = ex.Message, type = ex.GetType().Name });
            }
        }

        [McpServerTool(Name = "manage_equipment_type"), System.ComponentModel.Description(
            "CRUD operations for project equipment types. " +
            "Use list to see all types (filter by categoryCode or status). " +
            "Use get to see a type with its specs and instances. " +
            "Use upsert to create/update a type (upsert key: projectTypeName). " +
            "Use upsert_batch to create/update multiple types atomically. " +
            "Use delete to remove a type and its specs/instances (CASCADE).")]
        public static async Task<string> ManageEquipmentType(
            string action = "list",
            long projectTypeId = 0,
            string categoryCode = "",
            string status = "",
            string type = "",
            string types = "",
            string database = "auto")
        {
            try
            {
                // Parse JSON strings for complex params
                var typeObj = string.IsNullOrEmpty(type) ? null : JObject.Parse(type);
                var typesArr = string.IsNullOrEmpty(types) ? null : JArray.Parse(types);
                var parameters = new JObject
                {
                    ["action"] = action,
                    ["database"] = database
                };
                if (projectTypeId > 0) parameters["projectTypeId"] = projectTypeId;
                if (!string.IsNullOrEmpty(categoryCode)) parameters["categoryCode"] = categoryCode;
                if (!string.IsNullOrEmpty(status)) parameters["status"] = status;
                if (typeObj != null) parameters["type"] = typeObj;
                if (typesArr != null) parameters["types"] = typesArr;

                var result = await SendToRevit("manage_equipment_type", parameters);
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { error = ex.Message, type = ex.GetType().Name });
            }
        }

        [McpServerTool(Name = "manage_typed_specs"), System.ComponentModel.Description(
            "Read/write typed equipment specifications. Values are stored in display units AND SI units. " +
            "Server auto-converts display to SI using ConversionFactor from CategoryParameters. " +
            "Use get to read all specs for a type. " +
            "Use set to write a single spec (provide parameterCode + value). " +
            "Use set_batch to write multiple specs atomically (provide specs JSON array). " +
            "Use delete to remove a spec.")]
        public static async Task<string> ManageTypedSpecs(
            string action = "get",
            long projectTypeId = 0,
            string parameterCode = "",
            string value = "",
            double rangeMin = 0,
            double rangeMax = 0,
            string specs = "",
            string database = "auto")
        {
            try
            {
                var parameters = new JObject
                {
                    ["action"] = action,
                    ["projectTypeId"] = projectTypeId,
                    ["database"] = database
                };
                if (!string.IsNullOrEmpty(parameterCode)) parameters["parameterCode"] = parameterCode;
                if (!string.IsNullOrEmpty(value))
                {
                    // Try parse as number, fallback to string
                    if (double.TryParse(value, out var numVal))
                        parameters["value"] = numVal;
                    else
                        parameters["value"] = value;
                }
                if (rangeMin > 0) parameters["rangeMin"] = rangeMin;
                if (rangeMax > 0) parameters["rangeMax"] = rangeMax;
                if (!string.IsNullOrEmpty(specs)) parameters["specs"] = JArray.Parse(specs);

                var result = await SendToRevit("manage_typed_specs", parameters);
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { error = ex.Message, type = ex.GetType().Name });
            }
        }

        [McpServerTool(Name = "manage_equipment_instance"), System.ComponentModel.Description(
            "CRUD operations for equipment instances (physical units in the project). " +
            "Use list to see all instances for a type. " +
            "Use upsert to create/update a single instance (upsert key: tagNumber within type). " +
            "Use batch_create with tags (JSON array of strings) for DSTB with tag numbers. " +
            "Use batch_create with count for DSTB without tags (server generates placeholders, Status=Draft). " +
            "Use delete to remove an instance.")]
        public static async Task<string> ManageEquipmentInstance(
            string action = "list",
            long projectTypeId = 0,
            long projectEquipmentId = 0,
            string tagNumber = "",
            long revitElementId = 0,
            string tags = "",
            int count = 0,
            string database = "auto")
        {
            try
            {
                var parameters = new JObject
                {
                    ["action"] = action,
                    ["database"] = database
                };
                if (projectTypeId > 0) parameters["projectTypeId"] = projectTypeId;
                if (projectEquipmentId > 0) parameters["projectEquipmentId"] = projectEquipmentId;
                if (!string.IsNullOrEmpty(tagNumber)) parameters["tagNumber"] = tagNumber;
                if (revitElementId > 0) parameters["revitElementId"] = revitElementId;
                if (!string.IsNullOrEmpty(tags)) parameters["tags"] = JArray.Parse(tags);
                if (count > 0) parameters["count"] = count;

                var result = await SendToRevit("manage_equipment_instance", parameters);
                return JsonConvert.SerializeObject(result, Formatting.Indented);
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { error = ex.Message, type = ex.GetType().Name });
            }
        }

        [McpServerTool(Name = "analyze_usage_patterns"), System.ComponentModel.Description(
            "Analyze MCP tool usage patterns. Returns: session stats (call counts, success rates, top tools, flags), " +
            "and optionally historical data from journal files. Parameters: " +
            "days (int, default 1) — how many days of history to include. " +
            "Use this to understand which tools are used most, which fail often, and detect repeated patterns.")]
        public static string AnalyzeUsagePatterns(int days = 1)
        {
            try
            {
                var session = Session;
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
}
