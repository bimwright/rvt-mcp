using System;
using System.Collections.ObjectModel;

namespace Bimwright.Rvt.Plugin
{
    public class McpCallEntry
    {
        public int Index { get; set; }
        public DateTime Timestamp { get; set; }
        public string ToolName { get; set; }
        public bool Success { get; set; }
        public long DurationMs { get; set; }
        public string ParamsJson { get; set; }
        public string ErrorMessage { get; set; }
        public string CodeSnippet { get; set; }
        // New fields for History redesign
        public string ResultJson { get; set; }
        public string Summary { get; set; }
        public string ToolDescription { get; set; }
        public int? RerunOfIndex { get; set; }
    }

    public class McpSessionLog
    {
        private int _nextIndex = 1;
        public ObservableCollection<McpCallEntry> Entries { get; } = new ObservableCollection<McpCallEntry>();

        public void Add(McpCallEntry entry)
        {
            entry.Index = _nextIndex++;
            if (entry.Timestamp == default)
                entry.Timestamp = DateTime.Now;
            Entries.Add(entry);
        }

        // Legacy overload — kept for backward compatibility until all callers migrate
        public void Add(string toolName, string paramsJson, bool success,
                        long durationMs, string errorMsg = null, string codeSnippet = null)
        {
            Add(new McpCallEntry
            {
                ToolName = toolName,
                ParamsJson = paramsJson,
                Success = success,
                DurationMs = durationMs,
                ErrorMessage = errorMsg,
                CodeSnippet = codeSnippet
            });
        }

        public void Clear()
        {
            Entries.Clear();
            _nextIndex = 1;
        }

        public int Count => Entries.Count;
    }
}
