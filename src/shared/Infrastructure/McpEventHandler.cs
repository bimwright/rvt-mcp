using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using RvtMcp.Plugin.Views.Toast;

namespace RvtMcp.Plugin
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
        private readonly McpToastNotifier _toastNotifier;

        public McpEventHandler(CommandDispatcher dispatcher, McpSessionLog sessionLog, McpToastNotifier toastNotifier = null)
        {
            _dispatcher = dispatcher;
            _sessionLog = sessionLog;
            _toastNotifier = toastNotifier;

            try
            {
                var config = RvtMcpConfig.Load(args: null);
                SendCodeJournal.RunMaintenance(config);
            }
            catch { }
        }

        public void Enqueue(PendingRequest request)
        {
            _queue.Enqueue(request);
        }

        public void Execute(UIApplication app)
        {
            App.Instance?.CaptureMainWindowHandle(app.MainWindowHandle);

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
                        var unknownError = McpResponsePrivacy.RedactErrorForResponse($"Unknown command: {request.CommandName}");
                        McpLogger.Log(request.CommandName, request.ParamsJson, false,
                                      sw.ElapsedMilliseconds, unknownError);
                        _sessionLog?.Add(new McpCallEntry
                        {
                            ToolName = request.CommandName,
                            ParamsJson = request.ParamsJson,
                            Success = false,
                            DurationMs = sw.ElapsedMilliseconds,
                            ErrorMessage = unknownError,
                            Summary = Localization.L.T("history.summary.unknown", ("tool", request.CommandName)),
                            PreserveSummary = true
                        });
                        var errorResponse = JsonConvert.SerializeObject(new
                        {
                            id = request.Id,
                            success = false,
                            error = unknownError
                        });
                        request.Tcs.TrySetResult(errorResponse);
                        try
                        {
                            _toastNotifier?.OnCompleted(
                                request.CommandName,
                                request.ParamsJson,
                                null,
                                false,
                                unknownError,
                                sw.ElapsedMilliseconds,
                                null);
                        }
                        catch (Exception toastEx)
                        {
                            App.DebugLog("McpToastNotifier.OnCompleted failed (unknown command): " + toastEx.Message);
                        }
                        break;
                    }

                    // S6 strict schema validation — fail fast with error-as-teacher envelope
                    var validation = SchemaValidator.Validate(command.ParametersSchema, request.ParamsJson);
                    if (!validation.IsValid)
                    {
                        sw.Stop();
                        var validationError = McpResponsePrivacy.RedactErrorForResponse(validation.Error);
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
                            Summary = Localization.L.T("history.summary.validationFailed", ("error", validationError)),
                            PreserveSummary = true
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
                        try
                        {
                            _toastNotifier?.OnCompleted(
                                request.CommandName,
                                request.ParamsJson,
                                null,
                                false,
                                validationError,
                                sw.ElapsedMilliseconds,
                                command.Description);
                        }
                        catch (Exception toastEx)
                        {
                            App.DebugLog("McpToastNotifier.OnCompleted failed (validation): " + toastEx.Message);
                        }
                        break;
                    }

                    var result = command.Execute(app, request.ParamsJson);
                    sw.Stop();

                    // Explicit output=file and oversized arbitrary-code responses spill before
                    // logging and guarding, so neither the wire nor logs retain the bulk body.
                    var preSpillResponse = JsonConvert.SerializeObject(new
                    {
                        id = request.Id,
                        success = result.Success,
                        data = result.Data,
                        error = result.Error
                    });
                    var spillOutcome = new ResponseSpillProcessor(new ResponseSpillWriter()).Process(
                        request.CommandName,
                        request.ParamsJson,
                        result.Success,
                        result.Data,
                        preSpillResponse);
                    result.Data = spillOutcome.Data;

                    // responseData is the redacted view used ONLY for session log + summary.
                    // The wire response (below) uses result.Data raw so the agent sees real values.
                    var responseData = McpResponsePrivacy.RedactDataForResponse(request.CommandName, result.Data);

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

                    string responseResultJson = null;
                    try { responseResultJson = responseData != null ? JsonConvert.SerializeObject(responseData) : null; }
                    catch { }

                    // Truncate for session log (max 10KB)
                    string sessionResult = responseResultJson != null && responseResultJson.Length > 10240
                        ? responseResultJson.Substring(0, 10240)
                        : responseResultJson;

                    var resultError = McpResponsePrivacy.RedactErrorForResponse(result.Error);
                    McpLogger.Log(request.CommandName, request.ParamsJson, result.Success,
                                  sw.ElapsedMilliseconds, resultError, codeSnippet, resultJson);

                    SendCodeJournalGate.OnSendCodeLogged(request.CommandName, request.ParamsJson, codeSnippet, result.Success, sw.ElapsedMilliseconds, resultError, resultJson);

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

                    var size = ResponseSizeGuard.Evaluate(
                        commandName: request.CommandName,
                        serializedPayload: response,
                        topLevelKeyCount: (result.Data as Newtonsoft.Json.Linq.JObject)?.Count ?? 0,
                        narrowingHint: ResponseSizePolicyCatalog.GetNarrowingHint(request.CommandName));
                    if (size.Warning != null)
                        Console.Error.WriteLine(size.Warning);

                    if (size.Reject)
                    {
                        var mutationCompleted = result.Success
                            && ResponseSizePolicyCatalog.ShouldPreserveSuccessfulMutation(
                                request.CommandName,
                                request.ParamsJson,
                                ToolActivityClassifier.Classify(request.CommandName) == ToolActivityKind.Write);
                        if (mutationCompleted)
                        {
                            var compacted = MutationResponseCompactor.Compact(result.Data, size.ByteCount);
                            if (ResponseSizePolicyCatalog.IsMutationOutcomeIndeterminate(request.CommandName))
                                compacted["mutation_applied"] = JValue.CreateNull();
                            response = JsonConvert.SerializeObject(new
                            {
                                id = request.Id,
                                success = true,
                                data = compacted,
                                error = (string)null,
                                warning = compacted.Value<string>("warning")
                            });

                            // Defensive fallback: compaction itself must never breach the hard cap.
                            var compactSize = ResponseSizeGuard.Evaluate(
                                request.CommandName,
                                response,
                                compacted.Count);
                            if (compactSize.Reject)
                            {
                                response = JsonConvert.SerializeObject(new
                                {
                                    id = request.Id,
                                    success = true,
                                    data = new
                                    {
                                        success = true,
                                        mutation_applied = compacted["mutation_applied"],
                                        response_compacted = true,
                                        original_byte_count = size.ByteCount
                                    },
                                    error = (string)null,
                                    warning = "Command completed successfully; oversized response detail was omitted. Inspect the summary before another call."
                                });
                            }
                        }
                        else
                        {
                            response = JsonConvert.SerializeObject(new
                            {
                                id = request.Id,
                                success = false,
                                error = size.RejectError
                            });
                        }
                    }
                    else if (size.AgentWarning != null)
                    {
                        var warnedResponse = JObject.Parse(response);
                        warnedResponse["warning"] = size.AgentWarning;
                        var candidate = warnedResponse.ToString(Formatting.None);
                        if (System.Text.Encoding.UTF8.GetByteCount(candidate) <= ResponseSizeGuard.EnforcementBudgetBytes)
                            response = candidate;
                    }

                    request.Tcs.TrySetResult(response);
                    try
                    {
                        // Use redacted payload (same policy as session log) — never raw result.Data.
                        _toastNotifier?.OnCompleted(
                            request.CommandName,
                            request.ParamsJson,
                            responseResultJson,
                            result.Success,
                            resultError,
                            sw.ElapsedMilliseconds,
                            command.Description);
                    }
                    catch (Exception toastEx)
                    {
                        App.DebugLog("McpToastNotifier.OnCompleted failed (success path): " + toastEx.Message);
                    }
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    var exError = McpResponsePrivacy.RedactErrorForResponse(ex.Message);
                    McpLogger.Log(request.CommandName, request.ParamsJson, false,
                                  sw.ElapsedMilliseconds, exError);

                    string codeSnippetEx = null;
                    if (request.CommandName == "send_code_to_revit")
                    {
                        try { codeSnippetEx = JObject.Parse(request.ParamsJson)?.Value<string>("code"); }
                        catch { }
                    }
                    SendCodeJournalGate.OnSendCodeLogged(request.CommandName, request.ParamsJson, codeSnippetEx, false, sw.ElapsedMilliseconds, exError, null);

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
                    try
                    {
                        _toastNotifier?.OnCompleted(
                            request.CommandName,
                            request.ParamsJson,
                            null,
                            false,
                            exError,
                            sw.ElapsedMilliseconds,
                            null);
                    }
                    catch (Exception toastEx)
                    {
                        App.DebugLog("McpToastNotifier.OnCompleted failed (exception path): " + toastEx.Message);
                    }
                }

                break;
            }

            if (!_queue.IsEmpty)
            {
                try { App.Instance?.ExternalEvent?.Raise(); }
                catch { }
            }
        }

        public string GetName() => "RvtMcp.McpEventHandler";

        public void CancelAll()
        {
            while (_queue.TryDequeue(out var request))
            {
                request.Tcs.TrySetCanceled();
            }
        }
    }
}
