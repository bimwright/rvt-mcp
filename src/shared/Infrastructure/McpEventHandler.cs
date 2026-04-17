using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bimwright.Plugin
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
                        var unknownError = SanitizeError($"Unknown command: {request.CommandName}");
                        McpLogger.Log(request.CommandName, request.ParamsJson, false,
                                      sw.ElapsedMilliseconds, unknownError);
                        _sessionLog?.Add(new McpCallEntry
                        {
                            ToolName = request.CommandName,
                            ParamsJson = request.ParamsJson,
                            Success = false,
                            DurationMs = sw.ElapsedMilliseconds,
                            ErrorMessage = unknownError,
                            Summary = $"Unknown: {request.CommandName}"
                        });
                        var errorResponse = JsonConvert.SerializeObject(new
                        {
                            id = request.Id,
                            success = false,
                            error = unknownError
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

                    var resultError = SanitizeError(result.Error);
                    McpLogger.Log(request.CommandName, request.ParamsJson, result.Success,
                                  sw.ElapsedMilliseconds, resultError, codeSnippet, resultJson);

                    _sessionLog?.Add(new McpCallEntry
                    {
                        ToolName = request.CommandName,
                        ParamsJson = request.ParamsJson,
                        Success = result.Success,
                        DurationMs = sw.ElapsedMilliseconds,
                        ErrorMessage = resultError,
                        CodeSnippet = codeSnippet,
                        ResultJson = sessionResult,
                        ToolDescription = command.Description,
                        Summary = SummaryGenerator.Generate(request.CommandName, request.ParamsJson,
                                                             sessionResult, result.Success, resultError)
                    });

                    var response = JsonConvert.SerializeObject(new
                    {
                        id = request.Id,
                        success = result.Success,
                        data = result.Data,
                        error = resultError
                    });
                    request.Tcs.TrySetResult(response);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    var exError = SanitizeError(ex.Message);
                    McpLogger.Log(request.CommandName, request.ParamsJson, false,
                                  sw.ElapsedMilliseconds, exError);
                    _sessionLog?.Add(new McpCallEntry
                    {
                        ToolName = request.CommandName,
                        ParamsJson = request.ParamsJson,
                        Success = false,
                        DurationMs = sw.ElapsedMilliseconds,
                        ErrorMessage = exError,
                        Summary = SummaryGenerator.Generate(request.CommandName, request.ParamsJson,
                                                             null, false, exError)
                    });
                    var errorResponse = JsonConvert.SerializeObject(new
                    {
                        id = request.Id,
                        success = false,
                        error = exError
                    });
                    request.Tcs.TrySetResult(errorResponse);
                }
            }
        }

        /// <summary>
        /// S5 path-leak mask: strip absolute paths from error messages before they reach
        /// the MCP response, JSONL log, or session-log UI. Keeps filename + line number.
        /// </summary>
        private static string SanitizeError(string error)
        {
            if (string.IsNullOrEmpty(error)) return error;

            // 1. Windows absolute paths: D:\..., C:\Users\... → keep last filename only
            error = Regex.Replace(error,
                @"[A-Za-z]:\\(?:[^\\""'\s]+\\)*([^\\""'\s]+)",
                "$1");

            // 2. UNC paths: \\server\share\... → keep last filename only
            error = Regex.Replace(error,
                @"\\\\[^\\""'\s]+\\(?:[^\\""'\s]+\\)*([^\\""'\s]+)",
                "$1");

            // 3. Unix paths (safety): /home/..., /Users/... → keep last filename only
            error = Regex.Replace(error,
                @"/(?:home|Users)/[^/\s""']+/(?:[^/\s""']+/)*([^/\s""']+)",
                "$1");

            return error;
        }

        public string GetName() => "Bimwright.McpEventHandler";

        public void CancelAll()
        {
            while (_queue.TryDequeue(out var request))
            {
                request.Tcs.TrySetCanceled();
            }
        }
    }
}
