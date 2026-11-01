using System;
using System.Linq;
using System.Threading;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Reports.Presentation;
using RvtMcp.Plugin.Reports.Views;
using static H;

internal static class ReportHistoryChecks
{
    private static string Texts(System.Windows.DependencyObject root) => string.Join(" ", Visuals<TextBlock>(root).Select(t => t.Text));
    public static void History()
    {
        using (var s = new Scenario(DisplayMode.Wide).Show())
        {
            var main = Thread.CurrentThread.ManagedThreadId;
            var calls = 0; var worker = 0; var offset = -1;
            s.Runtime.HistoryReader = (id, callOffset, elementOffset) =>
            {
                Interlocked.Increment(ref calls); worker = Thread.CurrentThread.ManagedThreadId; offset = elementOffset;
                return new JObject { ["status"] = "references", ["calls"] = new JArray(new JObject {
                    ["call_id"] = new string('a', 32), ["tool"] = "synthetic", ["status"] = "available",
                    ["record_id"] = "record-1", ["reason_status"] = "recorded", ["reason"] = new JObject { ["reason"] = "Synthetic request" },
                    ["elements"] = new JArray(new JObject { ["id"] = 1, ["kind"] = "modified", ["before"] = "old", ["after"] = "new" }),
                    ["next_element_offset"] = elementOffset == 0 ? (JToken)10 : JValue.CreateNull() }) };
            };
            Expect(calls == 0, "collapsed History performs no I/O");
            var history = Visuals<ReportHistoryView>(s.Wide).Single(); history.IsExpanded = true;
            Expect(WaitFor(() => Texts(history).Contains("Synthetic request")), "expanded History shows the exact reason");
            Expect(worker != main && calls == 1, "History reads once on a worker thread");
            Expect(Texts(history).Contains("record-1") && Texts(history).Contains("old") && Texts(history).Contains("new"), "record ID and before/after are visible");
            var more = Visuals<Button>(history).Single(b => (b.Content as string) == "More elements"); Click(more);
            Expect(WaitFor(() => offset == 10 && calls == 2), "History pages elements independently");
            Click(s.Window.CompactButton);
            var compact = Visuals<ReportHistoryView>(s.Compact).Single();
            Expect(compact.IsExpanded && WaitFor(() => Texts(compact).Contains("Synthetic request")), "History expansion and page survive a mode switch");
        }
    }
}
