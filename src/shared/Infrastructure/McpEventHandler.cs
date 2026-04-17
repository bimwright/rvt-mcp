using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMcp.Plugin
{
    public class PendingRequest
    {
        public string Id { get; set; }
        public string CommandName { get; set; }
        public string ParamsJson { get; set; }
        public TaskCompletionSource<string> Tcs { get; set; }
    }

    public class McpEventHandler : IExternalEventHandler
    {
        private readonly ConcurrentQueue<PendingRequest> _queue = new ConcurrentQueue<PendingRequest>();
        private readonly CommandDispatcher _dispatcher;
        private readonly McpSessionLog _sessionLog;

        public McpEventHandler(CommandDispatcher dispatcher, McpSessionLog sessionLog)
        {
            _dispatcher = dispatcher;
            _sessionLog = sessionLog;
        }

        public void Enqueue(PendingRequest request)
        {
            _queue.Enqueue(request);
        }

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var request))
            {
                // Stale command guard: skip if TCS already completed (timeout/cancel)
                if (request.Tcs.Task.IsCompleted)
                    continue;

                var sw = Stopwatch.StartNew();
                try
                {
                    var command = _dispatcher.GetCommand(request.CommandName);
                    if (command == null)
                    {
                        sw.Stop();
                        McpLogger.Log(request.CommandName, request.ParamsJson, false,
                                      sw.ElapsedMilliseconds, $"Unknown command: {request.CommandName}");
                        _sessionLog?.Add(new McpCallEntry
                        {
                            ToolName = request.CommandName,
                            ParamsJson = request.ParamsJson,
                            Success = false,
                            DurationMs = sw.ElapsedMilliseconds,
                            ErrorMessage = $"Unknown command: {request.CommandName}",
                            Summary = $"Unknown: {request.CommandName}"
                        });
                        var errorResponse = JsonConvert.SerializeObject(new
                        {
                            id = request.Id,
                            success = false,
                            error = $"Unknown command: {request.CommandName}"
                        });
                        request.Tcs.TrySetResult(errorResponse);
                        continue;
                    }

                    var result = command.Execute(app, request.ParamsJson);
                    sw.Stop();

                    string codeSnippet = null;
                    if (request.CommandName == "send_code_to_revit")
                    {
                        try { codeSnippet = JObject.Parse(request.ParamsJson)?.Value<string>("code"); }
                        catch { }
                    }

                    // Serialize result for logging
                    string resultJson = null;
                    try { resultJson = result.Data != null ? JsonConvert.SerializeObject(result.Data) : null; }
                    catch { }

                    // Truncate for session log (max 10KB)
                    string sessionResult = resultJson != null && resultJson.Length > 10240
                        ? resultJson.Substring(0, 10240)
                        : resultJson;

                    McpLogger.Log(request.CommandName, request.ParamsJson, result.Success,
                                  sw.ElapsedMilliseconds, result.Error, codeSnippet, resultJson);

                    _sessionLog?.Add(new McpCallEntry
                    {
                        ToolName = request.CommandName,
                        ParamsJson = request.ParamsJson,
                        Success = result.Success,
                        DurationMs = sw.ElapsedMilliseconds,
                        ErrorMessage = result.Error,
                        CodeSnippet = codeSnippet,
                        ResultJson = sessionResult,
                        ToolDescription = command.Description,
                        Summary = SummaryGenerator.Generate(request.CommandName, request.ParamsJson,
                                                             sessionResult, result.Success, result.Error)
                    });

                    var response = JsonConvert.SerializeObject(new
                    {
                        id = request.Id,
                        success = result.Success,
                        data = result.Data,
                        error = result.Error
                    });
                    request.Tcs.TrySetResult(response);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    McpLogger.Log(request.CommandName, request.ParamsJson, false,
                                  sw.ElapsedMilliseconds, ex.Message);
                    _sessionLog?.Add(new McpCallEntry
                    {
                        ToolName = request.CommandName,
                        ParamsJson = request.ParamsJson,
                        Success = false,
                        DurationMs = sw.ElapsedMilliseconds,
                        ErrorMessage = ex.Message,
                        Summary = SummaryGenerator.Generate(request.CommandName, request.ParamsJson,
                                                             null, false, ex.Message)
                    });
                    var errorResponse = JsonConvert.SerializeObject(new
                    {
                        id = request.Id,
                        success = false,
                        error = ex.Message
                    });
                    request.Tcs.TrySetResult(errorResponse);
                }
            }
        }

        public string GetName() => "RevitMcp.McpEventHandler";

        public void CancelAll()
        {
            while (_queue.TryDequeue(out var request))
            {
                request.Tcs.TrySetCanceled();
            }
        }
    }
}
