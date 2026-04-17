using System;
using System.Collections.Concurrent;
using System.Diagnostics;
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
                        var unknownError = ErrorSanitizer.Sanitize($"Unknown command: {request.CommandName}");
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

                    // S6 strict schema validation — fail fast with error-as-teacher envelope
                    var validation = SchemaValidator.Validate(command.ParametersSchema, request.ParamsJson);
                    if (!validation.IsValid)
                    {
                        sw.Stop();
                        var validationError = ErrorSanitizer.Sanitize(validation.Error);
                        McpLogger.Log(request.CommandName, request.ParamsJson, false,
                                      sw.ElapsedMilliseconds, validationError);
                        _sessionLog?.Add(new McpCallEntry
                        {
                            ToolName = request.CommandName,
                            ParamsJson = request.ParamsJson,
                            Success = false,
                            DurationMs = sw.ElapsedMilliseconds,
                            ErrorMessage = validationError,
                            ToolDescription = command.Description,
                            Summary = "Validation failed: " + validationError
                        });
                        var validationResponse = JsonConvert.SerializeObject(new
                        {
                            id = request.Id,
                            success = false,
                            error = validationError,
                            suggestion = validation.Suggestion,
                            hint = validation.Hint
                        });
                        request.Tcs.TrySetResult(validationResponse);
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

                    var resultError = ErrorSanitizer.Sanitize(result.Error);
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
                    var exError = ErrorSanitizer.Sanitize(ex.Message);
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
