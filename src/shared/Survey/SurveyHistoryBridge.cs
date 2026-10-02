using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Survey
{
    /// <summary>Bounded before/after observations on the same UI-thread execution as the write.</summary>
    public static class SurveyHistoryBridge
    {
        private static readonly SurveySnapshotCache Cache = new SurveySnapshotCache();
        private static readonly BuiltInParameter[] Parameters = {
            BuiltInParameter.ALL_MODEL_MARK, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS,
            BuiltInParameter.ALL_MODEL_TYPE_COMMENTS, BuiltInParameter.ELEM_TYPE_PARAM };

        public static void Invalidate(object sender, EventArgs args) => Cache.Clear("document_or_view_context_changed");
        public static void DocumentChanged() => Cache.Clear("document_changed_or_undo");

        public static void Remember(UIApplication app, PendingRequest request, bool enabled, CommandResult result)
        {
            if (request.CommandName != "survey_change_impact") return;
            try
            {
                var report = result.Success ? result.Data as JObject : null;
                if (report == null) return;
                var doc = app.ActiveUIDocument?.Document;
                JObject metadata;
                if (!enabled || request.History?.IsValid != true)
                {
                    Cache.Clear("history_disabled");
                    metadata = SurveySnapshotCache.Unavailable("history_disabled");
                }
                else if (doc == null || doc.IsDetached || string.IsNullOrWhiteSpace(doc.PathName))
                {
                    Cache.Clear("document_identity_unavailable");
                    metadata = SurveySnapshotCache.Unavailable("document_identity_unavailable");
                }
                else
                {
                    var targets = new JArray();
                    var seen = new HashSet<long>();
                    foreach (var target in ((JArray)report["targets"]).OfType<JObject>())
                    {
                        if (target.Value<string>("status") != "resolved") continue;
                        var id = target.Value<long>("effectiveTargetId");
                        if (!seen.Add(id)) continue;
                        var element = doc.GetElement(RevitCompat.ToElementId(id));
                        if (element != null) targets.Add(new JObject { ["elementId"] = id, ["uniqueId"] = element.UniqueId });
                    }
                    if (targets.Count == 0)
                    {
                        Cache.Clear("no_resolved_targets");
                        metadata = SurveySnapshotCache.Unavailable("no_resolved_targets");
                    }
                    else
                    {
                        Cache.Remember(request.History.Session, doc, doc.PathName, report.Value<string>("surveyId"), targets, DateTimeOffset.UtcNow);
                        metadata = new JObject { ["status"] = "selection_in_memory", ["surveyId"] = report["surveyId"],
                            ["expiresAfterSeconds"] = 300, ["singleUse"] = true, ["elementCount"] = targets.Count,
                            ["coverage"] = "four_selected_builtin_parameters_remeasured_at_write" };
                    }
                }
                report["historySnapshot"] = metadata;
                result.Data = SurveyContract.Bound(report);
            }
            catch (Exception ex)
            {
                Cache.Clear("selection_capture_failed");
                HistoryDiagnostics.Report("survey_history_selection", ex, request.History?.Id, App.DebugLog);
            }
        }

        public sealed class Prepared
        {
            internal Document Document;
            internal string Path, ModelKey;
            internal JObject Selection;
            internal JArray Before;
            internal string CapturedAt;
        }

        public static Prepared Before(UIApplication app, PendingRequest request, bool enabled)
        {
            if (!enabled || request.History?.IsValid != true)
            {
                Cache.Clear("history_disabled");
                return null;
            }
            if (ToolReadPolicy.ReadOnlyCommands.Contains(request.CommandName)) return null;
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                var selection = Cache.Take(request.History.Session, doc, doc?.PathName, DateTimeOffset.UtcNow);
                var prepared = new Prepared { Document = doc, Path = doc?.PathName, Selection = selection };
                if (selection.Value<string>("status") != "selected") return prepared;
                prepared.ModelKey = McpChangeTracker.Scope.Identity(doc)?.Value<string>("key");
                if (prepared.ModelKey == null)
                {
                    prepared.Selection = SurveySnapshotCache.Unavailable("document_identity_unavailable");
                    return prepared;
                }
                prepared.CapturedAt = DateTimeOffset.UtcNow.ToString("o");
                prepared.Before = new JArray(((JArray)selection["targets"]).OfType<JObject>().Select(t => Read(doc, t)));
                return prepared;
            }
            catch (Exception ex)
            {
                Cache.Clear("before_capture_failed");
                HistoryDiagnostics.Report("survey_history_before", ex, request.History?.Id, App.DebugLog);
                return new Prepared { Selection = SurveySnapshotCache.Unavailable("before_capture_failed") };
            }
        }

        public static void Complete(Prepared prepared, JObject history)
        {
            if (prepared == null || history == null) return;
            foreach (var captured in ((JArray)history["documents"]).OfType<JObject>())
            {
                try
                {
                    var unavailable = prepared.Selection.Value<string>("status") != "selected" ? prepared.Selection.Value<string>("reason")
                        : history.Value<bool?>("complete") != true || captured["summary"]?.Value<string>("status") != "complete" ? "change_capture_incomplete"
                        : captured["model"]?.Value<string>("key") != prepared.ModelKey ? "different_changed_document"
                        : prepared.Document == null || !prepared.Document.IsValidObject ? "document_closed"
                        : prepared.Document.PathName != prepared.Path ? "document_context_changed" : null;
                    if (unavailable != null)
                    {
                        captured["parameterSnapshot"] = SurveySnapshotCache.Unavailable(unavailable);
                        continue;
                    }
                    var elements = new JArray();
                    foreach (var changed in ((JArray)captured["elements"]).OfType<JObject>())
                    {
                        if (changed.Value<string>("kind") != "modified") continue;
                        var before = prepared.Before.OfType<JObject>().SingleOrDefault(b =>
                            b.Value<long>("elementId") == changed.Value<long>("elementId")
                            && b.Value<string>("uniqueId") == changed.Value<string>("uniqueId"));
                        if (before == null) continue;
                        var after = Read(prepared.Document, before);
                        elements.Add(new JObject { ["elementId"] = changed["elementId"], ["uniqueId"] = changed["uniqueId"],
                            ["before"] = before, ["after"] = after });
                    }
                    captured["parameterSnapshot"] = new JObject { ["status"] = "captured", ["surveyId"] = prepared.Selection["surveyId"],
                        ["surveyedAt"] = prepared.Selection["surveyedAt"], ["beforeAt"] = prepared.CapturedAt,
                        ["afterAt"] = DateTimeOffset.UtcNow.ToString("o"), ["source"] = "plugin_execution_readback",
                        ["coverage"] = "selected_builtin_parameters_on_surveyed_modified_elements", ["elements"] = elements,
                        ["note"] = "Observed values around this call; not a full diff, write approval, Save/Sync or later Undo state." };
                }
                catch (Exception ex)
                {
                    captured["parameterSnapshot"] = SurveySnapshotCache.Unavailable("after_capture_failed");
                    HistoryDiagnostics.Report("survey_history_after", ex, log: App.DebugLog);
                }
            }
        }

        private static JObject Read(Document doc, JObject target)
        {
            var result = new JObject { ["elementId"] = target["elementId"], ["uniqueId"] = target["uniqueId"],
                ["status"] = "captured", ["parameters"] = new JArray() };
            var element = doc.GetElement(RevitCompat.ToElementId(target.Value<long>("elementId")));
            if (element == null || element.UniqueId != target.Value<string>("uniqueId"))
            {
                result["status"] = "unavailable";
                result["reason"] = "element_missing_or_replaced";
                return result;
            }
            foreach (var key in Parameters)
            {
                var value = new JObject { ["builtInId"] = (int)key, ["ownerId"] = RevitCompat.GetId(element.Id) };
                ((JArray)result["parameters"]).Add(value);
                try
                {
                    var parameter = element.get_Parameter(key);
                    if (parameter == null) { value["status"] = "unavailable"; value["reason"] = "parameter_not_present"; continue; }
                    value["name"] = parameter.Definition?.Name;
                    value["storageType"] = parameter.StorageType.ToString();
                    value["hasValue"] = parameter.HasValue;
                    var text = parameter.StorageType == StorageType.String ? parameter.AsString() : null;
                    if (text?.Length > 4096) { value["status"] = "unavailable"; value["reason"] = "value_exceeds_4096_chars"; continue; }
                    value["value"] = parameter.StorageType == StorageType.String ? (JToken)new JValue(text)
                        : parameter.StorageType == StorageType.Integer ? new JValue(parameter.AsInteger())
                        : parameter.StorageType == StorageType.Double ? new JValue(parameter.AsDouble())
                        : parameter.StorageType == StorageType.ElementId ? new JValue(RevitCompat.GetId(parameter.AsElementId())) : JValue.CreateNull();
                    value["valueUnits"] = parameter.StorageType == StorageType.Double ? "revit_internal_units" : null;
                    value["status"] = parameter.StorageType == StorageType.None ? "unavailable" : "read";
                    // Display text is supplemental; a display failure does not discard the measured raw value.
                    try
                    {
                        var display = parameter.AsValueString();
                        value["display"] = display?.Length > 160 ? display.Substring(0, 160) : display;
                        value["displayTruncated"] = display?.Length > 160;
                    }
                    catch (Exception ex) { value["displayStatus"] = "api_error:" + ex.GetType().Name; }
                }
                catch (Exception ex) { value["status"] = "unavailable"; value["reason"] = "api_error:" + ex.GetType().Name; }
            }
            return result;
        }
    }
}
