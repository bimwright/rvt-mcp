using System;
using RvtMcp.Plugin.Reports.Storage;

namespace RvtMcp.Plugin.Reports.Revit
{
    internal static class ReportServices
    {
        // One store per Revit process: replacing a report by task_id relies on its in-memory map.
        private static readonly Lazy<ReportStore> SharedStore = new Lazy<ReportStore>(() => new ReportStore(log: App.DebugLog));

        internal static ReportStore Store => SharedStore.Value;
    }
}
