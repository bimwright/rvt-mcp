using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace RvtMcp.Plugin.Presentation.History
{
    public interface IHistoryRuntime : IDisposable
    {
        ObservableCollection<McpCallEntry> Entries { get; }
        event EventHandler LanguageChanged;
        int LanguageVersion { get; }
        void Activate();
        void Deactivate();
        int LoadPastSessions();
        void ClearSession();
        void RefreshSummaries();
        string Description(string toolName);
        string FindCode(string hash);
        void OpenLogs();
        Task<HistoryRerunOutput> Rerun(HistoryRerunRequest request);
    }

    public interface IHistoryDialogs
    {
        bool ConfirmNewSession();
        bool ConfirmRerun(bool redacted);
    }

    /// <summary>Frozen before confirmation/await; later row selection cannot change this request.</summary>
    public sealed class HistoryRerunRequest
    {
        public string ToolName { get; set; }
        public string ParamsJson { get; set; }
        public int OriginalIndex { get; set; }
        public string OriginalResultJson { get; set; }
    }

    public sealed class HistoryRerunOutput
    {
        public McpCallEntry Entry { get; set; }
        public string OriginalResultJson { get; set; }
        public int OriginalIndex { get; set; }
        public DateTime CompletedAt { get; set; }
    }
}
