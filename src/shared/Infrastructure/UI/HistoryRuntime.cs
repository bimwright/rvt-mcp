using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Presentation.History;

namespace RvtMcp.Plugin.Infrastructure.UI
{
    public sealed class HistoryRuntime : IHistoryRuntime
    {
        private readonly McpSessionLog _log;
        private readonly CommandDispatcher _commands;
        private readonly McpEventHandler _handler;
        private readonly Autodesk.Revit.UI.ExternalEvent _externalEvent;
        private readonly Dispatcher _dispatcher;
        private bool _active, _disposed;
        private int _generation;

        public HistoryRuntime(McpSessionLog log, CommandDispatcher commands, McpEventHandler handler,
            Autodesk.Revit.UI.ExternalEvent externalEvent)
        {
            _log = log; _commands = commands; _handler = handler; _externalEvent = externalEvent;
            _dispatcher = Dispatcher.CurrentDispatcher;
        }
        public ObservableCollection<McpCallEntry> Entries => _log.Entries;
        public event EventHandler LanguageChanged;
        public int LanguageVersion => L.Version;
        public void Activate() { if (!_active && !_disposed) { _active = true; _generation++; L.Changed += OnLanguageChanged; } }
        public void Deactivate() { if (_active) { _active = false; _generation++; L.Changed -= OnLanguageChanged; } }
        private void OnLanguageChanged(object sender, EventArgs e)
        {
            var generation = _generation;
            Action publish = () => { if (_active && !_disposed && generation == _generation) LanguageChanged?.Invoke(this, EventArgs.Empty); };
            try { if (_dispatcher.CheckAccess()) publish(); else _dispatcher.BeginInvoke(publish); } catch { }
        }
        public void RefreshSummaries() { foreach (var entry in Entries) McpSessionLog.RefreshSummary(entry); }
        public string Description(string toolName) => _commands.GetCommand(toolName)?.Description;
        public string FindCode(string hash) => SendCodeJournal.TryFindCodeByHash(hash);
        public void ClearSession() => _log.Clear();
        public int LoadPastSessions()
        {
            var folder = !string.IsNullOrEmpty(McpLogger.CurrentLogPath) ? Path.GetDirectoryName(McpLogger.CurrentLogPath)
                : Path.Combine(McpLogger.LocalAppDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bimwright", "rvt-mcp");
            var entries = SessionLogHistoryLoader.LoadPastSessions(folder, McpLogger.CurrentSessionId, Entries.Count(e => !e.IsHistorical));
            for (var i = 0; i < entries.Count; i++)
            {
                entries[i].ToolDescription = Description(entries[i].ToolName);
                Entries.Insert(i, entries[i]);
            }
            return entries.Count;
        }
        public void OpenLogs()
        {
            var folder = Path.Combine(McpLogger.LocalAppDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bimwright", "rvt-mcp");
            try { Directory.CreateDirectory(folder); Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); } catch { }
        }

        public async Task<HistoryRerunOutput> Rerun(HistoryRerunRequest request)
        {
            var sw = Stopwatch.StartNew();
            var result = await Task.Run(async () =>
            {
                var tcs = new TaskCompletionSource<string>();
                _handler.Enqueue(new PendingRequest { Id = Guid.NewGuid().ToString(), CommandName = request.ToolName,
                    ParamsJson = request.ParamsJson, Tcs = tcs });
                _externalEvent.Raise();
                var response = JObject.Parse(await tcs.Task);
                return new CommandResult { Success = response.Value<bool>("success"), Error = response.Value<string>("error"), Data = response["data"]?.ToObject<object>() };
            });
            sw.Stop();
            string json = null;
            try { json = result.Data != null ? Newtonsoft.Json.JsonConvert.SerializeObject(result.Data) : null; } catch { }
            var bounded = json != null && json.Length > 10240 ? json.Substring(0, 10240) : json;
            _log.Add(new McpCallEntry { ToolName = request.ToolName, ParamsJson = request.ParamsJson,
                Success = result.Success, DurationMs = sw.ElapsedMilliseconds, ErrorMessage = result.Error,
                ResultJson = bounded, ToolDescription = Description(request.ToolName),
                Summary = SummaryGenerator.Generate(request.ToolName, request.ParamsJson, bounded, result.Success, result.Error),
                RerunOfIndex = request.OriginalIndex });
            // Logging still completes if its originating UI is hidden/disposed; it never retries the request.
            return new HistoryRerunOutput { Entry = new McpCallEntry { ToolName = request.ToolName,
                Success = result.Success, ResultJson = bounded, ErrorMessage = result.Error, DurationMs = sw.ElapsedMilliseconds },
                OriginalIndex = request.OriginalIndex, OriginalResultJson = request.OriginalResultJson, CompletedAt = DateTime.Now };
        }
        public void Dispose() { if (_disposed) return; Deactivate(); _disposed = true; }
    }
}
