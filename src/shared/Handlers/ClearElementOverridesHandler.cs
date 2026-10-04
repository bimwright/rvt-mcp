using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ClearElementOverridesHandler : IRevitCommand
    {
        public string Name => "clear_element_overrides";

        public string Description =>
            "Reset per-element view-specific graphic overrides back to default for one or more elements (or all overridden elements) in a view.";

        public string ParametersSchema => @"{""type"":""object"",""properties"":{""element_ids"":{""type"":""array"",""items"":{""type"":""integer""},""description"":""Elements to clear. If omitted, clears overrides on ALL elements visible in the view that currently have overrides.""},""view_id"":{""type"":""integer"",""description"":""If omitted, active view.""}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            JObject request;
            try
            {
                request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            }
            catch (Newtonsoft.Json.JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            var viewIdRaw = request.Value<long?>("view_id");

            // Resolve the target view.
            View view;
            if (viewIdRaw.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(viewIdRaw.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(viewIdRaw.Value));

                view = doc.GetElement(RevitCompat.ToElementId(viewIdRaw.Value)) as View;
                if (view == null)
                    return Error($"View with ID {viewIdRaw.Value} not found.", null, null, 0, 0);
            }
            else
            {
                view = doc.ActiveView;
                if (view == null)
                    return Error("No active view is available.", null, null, 0, 0);
            }

            var resolvedViewId = RevitCompat.GetId(view.Id);

            if (!view.AreGraphicsOverridesAllowed())
                return Error(
                    $"View '{view.Name}' ({view.ViewType}) does not allow graphics overrides.",
                    resolvedViewId, view.Name, 0, 0);

            // Collect explicit element ids if supplied.
            var explicitIds = new List<long>();
            var elementIdsToken = request["element_ids"];
            if (elementIdsToken != null && elementIdsToken.Type == JTokenType.Array)
            {
                foreach (var token in (JArray)elementIdsToken)
                {
                    if (token == null || token.Type == JTokenType.Null) continue;
                    explicitIds.Add(token.Value<long>());
                }
            }

            // Build the working set of ElementIds to clear.
            var targets = new List<ElementId>();
            var failed = new List<object>();

            if (explicitIds.Count > 0)
            {
                foreach (var raw in explicitIds)
                {
                    if (!RevitCompat.CanRepresentElementId(raw))
                    {
                        failed.Add(new { element_id = raw, error = RevitCompat.ElementIdRangeError(raw) });
                        continue;
                    }

                    var elemId = RevitCompat.ToElementId(raw);
                    var elem = doc.GetElement(elemId);
                    if (elem == null)
                    {
                        failed.Add(new { element_id = raw, error = $"Element with ID {raw} not found." });
                        continue;
                    }

                    targets.Add(elemId);
                }
            }
            else
            {
                // No ids supplied: scan the view for elements that currently have overrides.
                IList<Element> visibleElements;
                try
                {
                    visibleElements = new FilteredElementCollector(doc, view.Id)
                        .WhereElementIsNotElementType()
                        .ToElements();
                }
                catch (Exception ex)
                {
                    return Error($"Failed to collect elements in view: {ex.Message}",
                        resolvedViewId, view.Name, 0, 0);
                }

                foreach (var elem in visibleElements)
                {
                    if (elem == null || elem.Id == null) continue;
                    try
                    {
                        if (HasOverrides(view.GetElementOverrides(elem.Id)))
                            targets.Add(elem.Id);
                    }
                    catch
                    {
                        // Element does not support per-element overrides; skip silently.
                    }
                }
            }

            int succeeded = 0;
            int elementCount = targets.Count;

            using (var tx = new Transaction(doc, "RvtMcp: clear element overrides"))
            {
                tx.Start();
                try
                {
                    foreach (var elemId in targets)
                    {
                        try
                        {
                            // Applying a fresh empty OverrideGraphicSettings resets to default.
                            view.SetElementOverrides(elemId, new OverrideGraphicSettings());
                            succeeded++;
                        }
                        catch (Exception ex)
                        {
                            failed.Add(new { element_id = RevitCompat.GetId(elemId), error = ex.Message });
                        }
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return Error($"Failed to clear element overrides: {ex.Message}",
                        resolvedViewId, view.Name, elementCount, 0);
                }
            }

            return CommandResult.Ok(new
            {
                cleared = true,
                view_id = resolvedViewId,
                view_name = view.Name,
                element_count = elementCount,
                succeeded,
                failed = failed.ToArray(),
                error = (string)null
            });
        }

        /// <summary>
        /// Detects non-default element overrides while tolerating API/property differences.
        /// </summary>
        private static bool HasOverrides(OverrideGraphicSettings ogs)
        {
            if (ogs == null) return false;

            bool boolValue;
            int intValue;
            ViewDetailLevel detailLevel;
            ElementId patternId;
            Color color;

            if (TryRead(() => ogs.Halftone, out boolValue) && boolValue) return true;
            if (TryRead(() => ogs.Transparency, out intValue) && intValue > 0) return true;

            if (TryRead(() => ogs.DetailLevel, out detailLevel) &&
                detailLevel != ViewDetailLevel.Undefined) return true;

            if (TryRead(() => ogs.ProjectionLineColor, out color) && IsValid(color)) return true;
            if (TryRead(() => ogs.CutLineColor, out color) && IsValid(color)) return true;
            if (TryRead(() => ogs.SurfaceForegroundPatternColor, out color) && IsValid(color)) return true;
            if (TryRead(() => ogs.SurfaceBackgroundPatternColor, out color) && IsValid(color)) return true;
            if (TryRead(() => ogs.CutForegroundPatternColor, out color) && IsValid(color)) return true;
            if (TryRead(() => ogs.CutBackgroundPatternColor, out color) && IsValid(color)) return true;

            if (TryRead(() => ogs.ProjectionLineWeight, out intValue) && intValue > 0) return true;
            if (TryRead(() => ogs.CutLineWeight, out intValue) && intValue > 0) return true;

            if (TryRead(() => ogs.ProjectionLinePatternId, out patternId) && IsValid(patternId)) return true;
            if (TryRead(() => ogs.CutLinePatternId, out patternId) && IsValid(patternId)) return true;
            if (TryRead(() => ogs.SurfaceForegroundPatternId, out patternId) && IsValid(patternId)) return true;
            if (TryRead(() => ogs.SurfaceBackgroundPatternId, out patternId) && IsValid(patternId)) return true;
            if (TryRead(() => ogs.CutForegroundPatternId, out patternId) && IsValid(patternId)) return true;
            if (TryRead(() => ogs.CutBackgroundPatternId, out patternId) && IsValid(patternId)) return true;

            if (TryRead(() => ogs.IsSurfaceForegroundPatternVisible, out boolValue) && !boolValue) return true;
            if (TryRead(() => ogs.IsSurfaceBackgroundPatternVisible, out boolValue) && !boolValue) return true;
            if (TryRead(() => ogs.IsCutForegroundPatternVisible, out boolValue) && !boolValue) return true;
            if (TryRead(() => ogs.IsCutBackgroundPatternVisible, out boolValue) && !boolValue) return true;

            return false;
        }

        private static bool TryRead<T>(Func<T> read, out T value)
        {
            try
            {
                value = read();
                return true;
            }
            catch
            {
                value = default(T);
                return false;
            }
        }

        private static bool IsValid(ElementId id)
        {
            return id != null && id != ElementId.InvalidElementId;
        }

        private static bool IsValid(Color color)
        {
            return color != null && color.IsValid;
        }

        private static CommandResult Error(string message, long? viewId, string viewName,
            int elementCount, int succeeded)
        {
            return CommandResult.Ok(new
            {
                cleared = false,
                view_id = viewId,
                view_name = viewName,
                element_count = elementCount,
                succeeded,
                failed = new object[0],
                error = message
            });
        }
    }
}
