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
        public string LogId { get; } = Guid.NewGuid().ToString("N");
        public string CommandName { get; set; }
        public string ParamsJson { get; set; }
        public JObject ScriptMetadata { get; set; }
        internal ScriptExecutionState ScriptState { get; set; }
        public RvtMcpConfig RuntimeOptions { get; set; }
        public ChangeHistoryRequest History { get; set; }
        public ReportCallContext ReportContext { get; set; }
        public bool FromMcp { get; set; }
        public string ExpectedDocumentId { get; set; }
        public bool DocumentProtocol { get; set; }
        public TaskCompletionSource<string> Tcs { get; set; }
    }

    public class McpEventHandler : IExternalEventHandler
    {
        private readonly ConcurrentQueue<PendingRequest> _queue = new ConcurrentQueue<PendingRequest>();
        private readonly CommandDispatcher _dispatcher;
        private readonly McpSessionLog _sessionLog;
        private readonly McpToastNotifier _toastNotifier;
        private readonly ResponseSpillWriter _spillWriter;

        public McpEventHandler(CommandDispatcher dispatcher, McpSessionLog sessionLog, McpToastNotifier toastNotifier = null)
            : this(dispatcher, sessionLog, toastNotifier, null) { }

        public McpEventHandler(CommandDispatcher dispatcher, McpSessionLog sessionLog, McpToastNotifier toastNotifier,
            ResponseSpillWriter spillWriter)
        {
            _dispatcher = dispatcher;
            _sessionLog = sessionLog;
            _toastNotifier = toastNotifier;
            _spillWriter = spillWriter;

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
                if (!PendingRequestLifecycle.For(request.Tcs).TryStart())
                    continue;

                var sw = Stopwatch.StartNew();
                var runtimeConfig = new RvtMcpConfig();
                JObject changes = null;
                JObject history = null;
                JObject historyMarker = null;
                JObject reportCapture = null;
                Failures.FailureControl.Scope failureScope = null;
                JObject actualTarget = null;
                try
                {
                    if (request.ReportContext != null && (!request.ReportContext.IsValid
                        || request.ReportContext.CallId != PendingRequestLifecycle.For(request.Tcs).OperationId
                        || request.History?.IsValid == true && (request.ReportContext.CallId != request.History.Id || request.ReportContext.Session != request.History.Session)))
                        throw new InvalidOperationException("REPORT_CONTEXT_INVALID: Capture and history identifiers must agree.");
                    if (request.CommandName == "send_code_to_revit" && request.ScriptMetadata != null)
                        request.ScriptState = new ScriptExecutionState(request.ScriptMetadata, request.ParamsJson);
                    actualTarget = DocumentTargetGuard.Capture(app);
                    if (request.FromMcp)
                    {
                        var documentRejection = ToolReadPolicy.DocumentRejection(request.CommandName,
                            request.ExpectedDocumentId, actualTarget.Value<string>("document_id"), request.DocumentProtocol);
                        if (documentRejection != null) throw new InvalidOperationException(documentRejection);
                    }
                    runtimeConfig = RvtMcpConfig.LoadReadOnly().WithRuntimeOptions(request.RuntimeOptions);
                    var rejection = ToolReadPolicy.Rejection(request.CommandName, runtimeConfig.ReadOnlyOrDefault, runtimeConfig.EnableSendCodeOrDefault);
                    if (rejection != null) throw new InvalidOperationException(rejection);
                    var command = _dispatcher.GetCommand(request.CommandName);
                    if (command == null)
                    {
                        sw.Stop();
                        var unknownError = McpResponsePrivacy.RedactErrorForResponse($"Unknown command: {request.CommandName}");
                        McpLogger.Log(request.CommandName, request.ParamsJson, false,
                                      sw.ElapsedMilliseconds, unknownError, enabled: runtimeConfig.EnableCallLogOrDefault, callId: request.LogId);
                        _sessionLog?.Add(new McpCallEntry
                        {
                            ToolName = request.CommandName,
                            CallId = request.LogId,
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
                        if (request.ScriptState != null) throw new InvalidOperationException(validation.Error);
                        sw.Stop();
                        var validationError = McpResponsePrivacy.RedactErrorForResponse(validation.Error);
                        McpLogger.Log(request.CommandName, request.ParamsJson, false,
                                      sw.ElapsedMilliseconds, validationError, enabled: runtimeConfig.EnableCallLogOrDefault, callId: request.LogId);
                        _sessionLog?.Add(new McpCallEntry
                        {
                            ToolName = request.CommandName,
                            CallId = request.LogId,
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

                    CommandResult result;
                    failureScope = Failures.FailureControl.Begin(app, request, runtimeConfig.WarningPolicyOrDefault);
                    Failures.FailureControl.ThrowIfStopped();
                    var surveySnapshot = Survey.SurveyHistoryBridge.Before(app, request, runtimeConfig.EnableChangeHistoryOrDefault);
                    using (ReportExecutionContext.Enter(request.ReportContext))
                    using (var capture = McpChangeTracker.Begin(request.History?.IsValid == true && runtimeConfig.EnableChangeHistoryOrDefault))
                    {
                        bool rolledBack = false;
                        try
                        {
                            failureScope.ExecutionStarted = true;
                            result = command is Handlers.SendCodeToRevitHandler scriptHandler && request.ScriptMetadata != null
                                ? scriptHandler.ExecuteProtocol(app, request.ParamsJson, request.ScriptMetadata, request.ScriptState)
                                : command.Execute(app, request.ParamsJson);
                            rolledBack = request.CommandName == "batch_execute"
                                && result.Data != null && JObject.FromObject(result.Data).Value<bool>("rolledBack");
                        }
                        finally
                        {
                            changes = capture.Snapshot(rolledBack);
                            try
                            {
                                if (request.CommandName != "report_task_result")
                                {
                                    var context = request.ReportContext == null ? null : new ReportCallContext { CallId = request.ReportContext.CallId,
                                        Session = request.ReportContext.Session, HistoryRequested = request.History?.IsValid == true && runtimeConfig.EnableChangeHistoryOrDefault };
                                    var retained = Reports.Changes.ReportChangeBuffer.Shared.Push(capture.ReportSnapshot(rolledBack), context, request.CommandName);
                                    if (context != null) reportCapture = new JObject { ["call_id"] = context.CallId, ["client_session"] = context.Session,
                                        ["status"] = retained ? "captured" : "expired" };
                                }
                            }
                            catch (Exception ex)
                            {
                                Reports.Changes.ReportChangeBuffer.Shared.MarkCaptureUnavailable(request.ReportContext?.Session);
                                if (request.ReportContext != null && request.CommandName != "report_task_result")
                                    reportCapture = new JObject { ["call_id"] = request.ReportContext.CallId,
                                        ["client_session"] = request.ReportContext.Session, ["status"] = "failed" };
                                try { App.DebugLog("Report change capture failed: " + ex.Message); } catch { }
                            }
                            try { history = capture.HistorySnapshot(app.ActiveUIDocument?.Document, rolledBack); }
                            catch (Exception ex)
                            {
                                HistoryDiagnostics.Report("history_snapshot", ex, request.History?.Id, App.DebugLog);
                                historyMarker = new JObject { ["status"] = "capture_failed" };
                            }
                            Survey.SurveyHistoryBridge.Complete(surveySnapshot, history);
                        }
                    }
                    failureScope.Complete(result);
                    if (request.CommandName == "open_model" && result.Success)
                        actualTarget = DocumentTargetGuard.Capture(app);
                    Survey.SurveyHistoryBridge.Remember(app, request, runtimeConfig.EnableChangeHistoryOrDefault, result);
                    sw.Stop();
                    // Spill may replace result.Data; the outcome is judged on the handler's own data.
                    var handlerData = result.Data;

                    // Explicit output=file and oversized arbitrary-code responses spill before
                    // logging and guarding, so neither the wire nor logs retain the bulk body.
                    var preSpillResponse = JsonConvert.SerializeObject(new
                    {
                        id = request.Id,
                        success = result.Success,
                        data = result.Data,
                        error = McpResponsePrivacy.RedactErrorForResponse(NoDocumentGuidance.ForAgent(result.Error))
                    });
                    var envelope = JObject.Parse(preSpillResponse);
                    envelope["_target"] = actualTarget;
                    failureScope.Enrich(envelope);
                    request.ScriptState?.Apply(envelope);
                    if (request.CommandName == "send_code_to_revit" && request.ScriptState == null)
                    {
                        var returned = handlerData == null ? null : JObject.FromObject(handlerData)["result"];
                        ScriptExecutionState.AttachLegacyHint(envelope, returned);
                    }
                    if (changes != null) envelope["changes"] = changes;
                    if (reportCapture != null) envelope["_report_capture"] = reportCapture;
                    // Reserve the small receipt marker in the response budget before writing the capture.
                    if (history != null || historyMarker != null)
                        envelope["history_transfer"] = historyMarker ?? ChangeHistoryTransfer.PublicationMarker(request.History.Id, history);
                    var preGuard = ResponseEnvelopeGuard.Apply(request.CommandName, request.ParamsJson,
                        envelope, runtimeConfig, _spillWriter);
                    if (request.ScriptState != null && envelope.Value<bool>("success") && !preGuard.Value<bool>("success"))
                    {
                        request.ScriptState.Failed();
                        if (preGuard["_script"] is JObject failedOutput) failedOutput["script_stage"] = "output_failed";
                    }
                    result.Data = preGuard["data"];

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
                    string rejectError = result.Success && !preGuard.Value<bool>("success")
                        ? preGuard.Value<string>("error") : null;

                    // Toast, History and mcp-calls.jsonl report one outcome, judged after the
                    // size guard so a rejected response is not recorded as a success.
                    var outcome = CommandOutcome.Normalize(
                        request.CommandName, preGuard.Value<bool>("success"), handlerData,
                        preGuard.Value<string>("error") ?? resultError, rejectError);
                    historyMarker = historyMarker ?? PublishHistory(request, history, outcome.Success, handlerData, app.Application.VersionNumber);
                    if (historyMarker != null) preGuard["history_transfer"] = historyMarker;
                    else preGuard.Remove("history_transfer");
                    failureScope.PersistFinal(preGuard);
                    if (!preGuard.Value<bool>("success") && outcome.Success)
                        outcome = CommandOutcome.Normalize(request.CommandName, false, handlerData, preGuard.Value<string>("error"), null);
                    var response = preGuard.ToString(Formatting.None);
                    // Audit identity is independent of the smaller wire receipt after compaction.
                    var audit = new JObject(); SafetyResponseMetadata.Attach(audit, envelope);
                    if (request.ScriptState != null) audit["_script"] = RvtMcp.Scripting.ScriptTelemetry.BoundScript(request.ScriptState.Script);

                    var logged = McpLogger.TryLog(request.CommandName, request.ParamsJson, outcome.Success,
                                  sw.ElapsedMilliseconds, outcome.Error, codeSnippet, resultJson, runtimeConfig.EnableCallLogOrDefault, request.LogId, audit);
                    var journaled = SendCodeJournalGate.OnSendCodeLogged(request.CommandName, request.ParamsJson, codeSnippet,
                        outcome.Success, sw.ElapsedMilliseconds, outcome.Error, resultJson, runtimeConfig, audit);
                    if ((!logged || !journaled) && preGuard["_script"] is JObject scriptReceipt)
                    {
                        scriptReceipt["telemetry_status"] = "write_failed";
                        failureScope.PersistFinal(preGuard);
                        response = preGuard.ToString(Formatting.None);
                        if (!preGuard.Value<bool>("success") && outcome.Success)
                            outcome = CommandOutcome.Normalize(request.CommandName, false, handlerData, preGuard.Value<string>("error"), null);
                    }

                    _sessionLog?.Add(new McpCallEntry
                    {
                        ToolName = request.CommandName,
                        CallId = request.LogId,
                        ParamsJson = request.ParamsJson,
                        Success = outcome.Success,
                        DurationMs = sw.ElapsedMilliseconds,
                        ErrorMessage = outcome.Error,
                        CodeSnippet = codeSnippet,
                        ResultJson = sessionResult,
                        ToolDescription = command.Description,
                        Script = audit["_script"] as JObject,
                        Task = audit["_task"] as JObject,
                        Operation = preGuard["_operation"] as JObject,
                        Summary = SummaryGenerator.Generate(request.CommandName, request.ParamsJson,
                                                             sessionResult, outcome.Success, outcome.Error)
                    });

                    request.Tcs.TrySetResult(response);
                    try
                    {
                        // Use redacted payload (same policy as session log) — never raw result.Data.
                        _toastNotifier?.OnCompleted(
                            request.CommandName,
                            request.ParamsJson,
                            UiResult(responseResultJson, preGuard),
                            outcome.Success,
                            outcome.Error,
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
                    failureScope?.Complete(new CommandResult { Success = false, Error = exError });
                    if (failureScope?.Stopped == true) exError = failureScope.ErrorCode ?? exError;
                    string codeSnippetEx = null;
                    if (request.CommandName == "send_code_to_revit")
                    {
                        try { codeSnippetEx = JObject.Parse(request.ParamsJson)?.Value<string>("code"); }
                        catch { }
                    }
                    var errorEnvelope = JObject.FromObject(new
                    {
                        id = request.Id,
                        success = false,
                        error = exError
                    });
                    failureScope?.Enrich(errorEnvelope);
                    if (actualTarget != null) errorEnvelope["_target"] = actualTarget;
                    if (exError.StartsWith("DOCUMENT_UNCONFIRMED:", StringComparison.Ordinal)
                        || exError.StartsWith("DOCUMENT_CHANGED:", StringComparison.Ordinal))
                        errorEnvelope["_operation"] = new JObject
                        {
                            ["operationId"] = PendingRequestLifecycle.For(request.Tcs).OperationId,
                            ["execution"] = "completed", ["outcome"] = "not_started", ["mutationApplied"] = false, ["complete"] = true
                        };
                    if (changes != null) errorEnvelope["changes"] = changes;
                    if (reportCapture != null) errorEnvelope["_report_capture"] = reportCapture;
                    if (request.ScriptState != null && errorEnvelope["_operation"] == null && failureScope == null)
                        errorEnvelope["_operation"] = new JObject {
                            ["operationId"] = PendingRequestLifecycle.For(request.Tcs).OperationId,
                            ["execution"] = "completed", ["outcome"] = "not_started", ["mutationApplied"] = false, ["complete"] = true };
                    request.ScriptState?.Failed();
                    request.ScriptState?.Apply(errorEnvelope);
                    historyMarker = historyMarker ?? PublishHistory(request, history, false, null, app.Application.VersionNumber);
                    if (historyMarker != null) errorEnvelope["history_transfer"] = historyMarker;
                    if (changes != null || failureScope != null)
                    {
                        try { errorEnvelope = ResponseEnvelopeGuard.Apply(request.CommandName, request.ParamsJson, errorEnvelope, runtimeConfig); }
                        catch { errorEnvelope["changes"] = ChangeSummary.Omitted(); }
                    }
                    failureScope?.PersistFinal(errorEnvelope);
                    if (!McpLogger.TryLog(request.CommandName, request.ParamsJson, false, sw.ElapsedMilliseconds, exError,
                        enabled: runtimeConfig.EnableCallLogOrDefault, callId: request.LogId, receipt: errorEnvelope)
                        && errorEnvelope["_script"] is JObject failedScript)
                    {
                        failedScript["telemetry_status"] = "write_failed";
                        failureScope?.PersistFinal(errorEnvelope);
                    }
                    if (!SendCodeJournalGate.OnSendCodeLogged(request.CommandName, request.ParamsJson, codeSnippetEx, false,
                        sw.ElapsedMilliseconds, exError, null, runtimeConfig, errorEnvelope)
                        && errorEnvelope["_script"] is JObject failedJournal)
                    {
                        failedJournal["telemetry_status"] = "write_failed";
                        failureScope?.PersistFinal(errorEnvelope);
                    }
                    _sessionLog?.Add(new McpCallEntry {
                        ToolName = request.CommandName, CallId = request.LogId, ParamsJson = request.ParamsJson,
                        Success = false, DurationMs = sw.ElapsedMilliseconds, ErrorMessage = exError,
                        Script = errorEnvelope["_script"] as JObject, Task = errorEnvelope["_task"] as JObject,
                        Operation = errorEnvelope["_operation"] as JObject,
                        Summary = SummaryGenerator.Generate(request.CommandName, request.ParamsJson, null, false, exError) });
                    request.Tcs.TrySetResult(errorEnvelope.ToString(Formatting.None));
                    try
                    {
                        _toastNotifier?.OnCompleted(
                            request.CommandName,
                            request.ParamsJson,
                            UiResult(null, errorEnvelope),
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
                finally
                {
                    failureScope?.Dispose();
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

        private static string UiResult(string result, JObject envelope)
        {
            if (envelope["_script"] == null) return result;
            JObject value;
            try { value = JObject.Parse(result ?? "{}"); } catch { value = new JObject(); }
            SafetyResponseMetadata.Attach(value, envelope);
            return value.ToString(Formatting.None);
        }

        private static JObject PublishHistory(PendingRequest request, JObject history, bool success, object data, string revitYear)
        {
            if (history == null) return null;
            try
            {
                history["callId"] = request.History.Id;
                history["session"] = request.History.Session;
                history["tool"] = request.CommandName;
                history["at"] = DateTime.UtcNow.ToString("o");
                history["success"] = success;
                history["revitYear"] = revitYear;
                history["user"] = Environment.UserName;
                history["machine"] = Environment.MachineName;
                // The handler returns updated rows only after commit, including partial-success calls.
                if (request.CommandName == "set_element_parameter_values" && data != null)
                {
                    var values = JObject.FromObject(data);
                    history["parameterValues"] = new JObject { ["parameter"] = values["parameterName"], ["updated"] = values["updated"] };
                }
                return new ChangeHistoryTransfer().Publish(request.History.Id, history);
            }
            catch (Exception ex)
            {
                // History failure must never change or replay an already completed Revit operation.
                HistoryDiagnostics.Report("history_publish", ex, request.History?.Id, App.DebugLog);
                return new JObject { ["status"] = "capture_failed" };
            }
        }

        public void CancelAll()
        {
            while (_queue.TryDequeue(out var request))
            {
                request.Tcs.TrySetCanceled();
            }
        }
    }
}
