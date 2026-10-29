using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    /// <summary>
    /// Reminds the agent to send a report: every response that changed the model carries <c>_report</c> until a report
    /// is sent, the first in full and the rest short; a check tool's first answer in a job carries a conditional one.
    /// Only the tool description says when to report otherwise, and a client that loads tool descriptions on demand may
    /// never read it.
    /// </summary>
    internal sealed class AgentReportReminder
    {
        internal const string Text = "This job changed the model. Before your final answer, call revit_report_task_result once: "
            + "outcome completed, failed or cancelled, and [name](revit:<element id>) links to the elements. Not after each tool.";
        internal const string ShortText = "Report still due: call revit_report_task_result once before your final answer.";
        internal const string CheckText = "If this check found elements the user should look at, call revit_report_task_result once "
            + "before your final answer, with [name](revit:<element id>) links to them. Skip it when nothing was found.";

        // Read-only checks whose answer is a list of elements for the user to review.
        private static readonly HashSet<string> Checks = new HashSet<string>(StringComparer.Ordinal)
        {
            "find_untagged_elements", "find_undimensioned_elements", "find_overlapping_elements", "find_mep_disconnects",
            "clash_detection", "get_model_warnings_summary", "audit_families", "workflow_model_audit", "workflow_clash_review"
        };

        private readonly Func<bool> _supported;
        private int _changeReminded;
        private int _checkReminded;

        /// <param name="supported">Whether the connected add-in accepts reports; a reminder it would reject is not given.</param>
        internal AgentReportReminder(Func<bool> supported) { _supported = supported; }

        /// <summary>The reminder this response should carry, or null.</summary>
        internal string For(string command, JObject changes, bool failed)
        {
            if (ChangedModel(changes))
                return !_supported() ? null : Volatile.Read(ref _changeReminded) == 0 ? Text : ShortText;
            if (failed || !Checks.Contains(command ?? "")) return null;
            if (Volatile.Read(ref _changeReminded) != 0 || Volatile.Read(ref _checkReminded) != 0) return null;
            return _supported() ? CheckText : null;
        }

        internal void Reminded(string text)
        {
            if (text == CheckText) Volatile.Write(ref _checkReminded, 1);
            else Volatile.Write(ref _changeReminded, 1);
        }

        internal void Reported()
        {
            Volatile.Write(ref _changeReminded, 0);
            Volatile.Write(ref _checkReminded, 0);
        }

        /// <summary>The agent summary of a command's changes shows a change, or cannot rule one out.</summary>
        internal static bool ChangedModel(JObject changes)
        {
            if (changes == null) return false;
            if (changes.Value<bool?>("complete") == false) return true;
            return (changes["documents"] as JArray ?? new JArray()).OfType<JObject>().Any(doc =>
                new[] { "added", "modified", "deleted" }.Any(kind => (doc[kind] as JObject)?.Value<int?>("count") > 0));
        }
    }
}
