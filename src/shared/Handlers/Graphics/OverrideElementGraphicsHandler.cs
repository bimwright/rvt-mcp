using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class OverrideElementGraphicsHandler : IRevitCommand
    {
        public string Name => "override_element_graphics";

        public string Description =>
            "Apply per-element view-specific graphic overrides (color, transparency, halftone, line weight) to one or more elements in a view";

        public string ParametersSchema => @"{""type"":""object"",""required"":[""element_ids""],""properties"":{""element_ids"":{""type"":""array"",""items"":{""type"":""integer""},""description"":""ElementIds to override.""},""view_id"":{""type"":""integer"",""description"":""Target view ElementId. If omitted, the active view is used.""},""projection_line_color"":{""type"":""string"",""description"":""Hex RGB '#RRGGBB'. Optional. Projection line color.""},""surface_foreground_color"":{""type"":""string"",""description"":""Hex RGB '#RRGGBB'. Optional. Solid surface foreground (fill) color.""},""cut_line_color"":{""type"":""string"",""description"":""Hex RGB '#RRGGBB'. Optional. Cut line color.""},""transparency"":{""type"":""integer"",""description"":""Surface transparency 0-100. Optional.""},""halftone"":{""type"":""boolean"",""description"":""Display the element halftone. Optional.""},""projection_line_weight"":{""type"":""integer"",""description"":""Projection line weight 1-16. Optional.""}}}";

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

            // element_ids (required).
            var idsToken = request["element_ids"];
            if (idsToken == null || idsToken.Type != JTokenType.Array)
                return CommandResult.Fail("'element_ids' is required and must be an array of integers.");

            var rawIds = new List<long>();
            foreach (var t in (JArray)idsToken)
            {
                if (t == null || t.Type == JTokenType.Null)
                    continue;
                rawIds.Add(t.Value<long>());
            }
            if (rawIds.Count == 0)
                return CommandResult.Fail("'element_ids' must contain at least one element id.");

            var viewIdRaw = request.Value<long?>("view_id");

            // Resolve the target view.
            View view;
            if (viewIdRaw.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(viewIdRaw.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(viewIdRaw.Value));

                view = doc.GetElement(RevitCompat.ToElementId(viewIdRaw.Value)) as View;
                if (view == null)
                    return Error($"View with ID {viewIdRaw.Value} not found.", null, null, 0);
            }
            else
            {
                view = doc.ActiveView;
                if (view == null)
                    return Error("No active view is available.", null, null, 0);
            }

            var resolvedViewId = RevitCompat.GetId(view.Id);

            if (!view.AreGraphicsOverridesAllowed())
                return Error(
                    $"View '{view.Name}' ({view.ViewType}) does not allow graphics overrides.",
                    resolvedViewId, view.Name, 0);

            // Build supplied-property applicators once, then merge them onto each element's current overrides.
            var applyOverrides = new List<Action<OverrideGraphicSettings>>();
            var overridesSet = new List<string>();

            var projLineColorStr = request.Value<string>("projection_line_color");
            if (!string.IsNullOrWhiteSpace(projLineColorStr))
            {
                if (!TryParseHexColor(projLineColorStr, out var color))
                    return Error($"Invalid 'projection_line_color' value '{projLineColorStr}'. Expected hex '#RRGGBB'.",
                        resolvedViewId, view.Name, rawIds.Count);
                var projectionLineColor = color;
                applyOverrides.Add(settings => settings.SetProjectionLineColor(projectionLineColor));
                overridesSet.Add("projection_line_color");
            }

            var surfaceColorStr = request.Value<string>("surface_foreground_color");
            if (!string.IsNullOrWhiteSpace(surfaceColorStr))
            {
                if (!TryParseHexColor(surfaceColorStr, out var color))
                    return Error($"Invalid 'surface_foreground_color' value '{surfaceColorStr}'. Expected hex '#RRGGBB'.",
                        resolvedViewId, view.Name, rawIds.Count);
                var surfaceForegroundColor = color;
                applyOverrides.Add(settings =>
                {
                    settings.SetSurfaceForegroundPatternColor(surfaceForegroundColor);
                    settings.SetSurfaceForegroundPatternVisible(true);
                    TrySetSolidSurfaceForegroundPattern(doc, settings);
                });
                overridesSet.Add("surface_foreground_color");
            }

            var cutLineColorStr = request.Value<string>("cut_line_color");
            if (!string.IsNullOrWhiteSpace(cutLineColorStr))
            {
                if (!TryParseHexColor(cutLineColorStr, out var color))
                    return Error($"Invalid 'cut_line_color' value '{cutLineColorStr}'. Expected hex '#RRGGBB'.",
                        resolvedViewId, view.Name, rawIds.Count);
                var cutLineColor = color;
                applyOverrides.Add(settings => settings.SetCutLineColor(cutLineColor));
                overridesSet.Add("cut_line_color");
            }

            var transparencyToken = request["transparency"];
            if (transparencyToken != null && transparencyToken.Type != JTokenType.Null)
            {
                var transparency = transparencyToken.Value<int>();
                if (transparency < 0 || transparency > 100)
                    return Error($"'transparency' must be between 0 and 100 (got {transparency}).",
                        resolvedViewId, view.Name, rawIds.Count);
                applyOverrides.Add(settings => settings.SetSurfaceTransparency(transparency));
                overridesSet.Add("transparency");
            }

            var halftoneToken = request["halftone"];
            if (halftoneToken != null && halftoneToken.Type != JTokenType.Null)
            {
                var halftone = halftoneToken.Value<bool>();
                applyOverrides.Add(settings => settings.SetHalftone(halftone));
                overridesSet.Add("halftone");
            }

            var weightToken = request["projection_line_weight"];
            if (weightToken != null && weightToken.Type != JTokenType.Null)
            {
                var weight = weightToken.Value<int>();
                if (weight < 1 || weight > 16)
                    return Error($"'projection_line_weight' must be between 1 and 16 (got {weight}).",
                        resolvedViewId, view.Name, rawIds.Count);
                applyOverrides.Add(settings => settings.SetProjectionLineWeight(weight));
                overridesSet.Add("projection_line_weight");
            }

            if (overridesSet.Count == 0)
                return Error(
                    "No override properties supplied. Provide at least one of: projection_line_color, surface_foreground_color, cut_line_color, transparency, halftone, projection_line_weight.",
                    resolvedViewId, view.Name, rawIds.Count);

            int succeeded = 0;
            var failed = new List<object>();

            using (var tx = new Transaction(doc, "RvtMcp: override element graphics"))
            {
                tx.Start();
                try
                {
                    foreach (var rawId in rawIds)
                    {
                        try
                        {
                            if (!RevitCompat.CanRepresentElementId(rawId))
                            {
                                failed.Add(new { element_id = rawId, error = RevitCompat.ElementIdRangeError(rawId) });
                                continue;
                            }

                            var elementId = RevitCompat.ToElementId(rawId);
                            var element = doc.GetElement(elementId);
                            if (element == null)
                            {
                                failed.Add(new { element_id = rawId, error = "Element not found." });
                                continue;
                            }

                            var elementOverrides = view.GetElementOverrides(elementId);
                            if (elementOverrides == null)
                                elementOverrides = new OverrideGraphicSettings();

                            foreach (var applyOverride in applyOverrides)
                                applyOverride(elementOverrides);

                            view.SetElementOverrides(elementId, elementOverrides);
                            succeeded++;
                        }
                        catch (Exception ex)
                        {
                            failed.Add(new { element_id = rawId, error = ex.Message });
                        }
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return Error($"Failed to apply element graphic overrides: {ex.Message}",
                        resolvedViewId, view.Name, rawIds.Count);
                }
            }

            return CommandResult.Ok(new
            {
                overridden = true,
                view_id = resolvedViewId,
                view_name = view.Name,
                element_count = rawIds.Count,
                succeeded,
                failed = failed.ToArray(),
                overrides_set = overridesSet.ToArray(),
                error = (string)null
            });
        }

        private static void TrySetSolidSurfaceForegroundPattern(Document doc, OverrideGraphicSettings ogs)
        {
            try
            {
                FillPatternElement solid = null;
                try
                {
                    solid = FillPatternElement.GetFillPatternElementByName(
                        doc, FillPatternTarget.Drafting, "<Solid fill>");
                }
                catch { }

                if (solid == null)
                {
                    solid = new FilteredElementCollector(doc)
                        .OfClass(typeof(FillPatternElement))
                        .Cast<FillPatternElement>()
                        .FirstOrDefault(f =>
                        {
                            try { return f.GetFillPattern().IsSolidFill; }
                            catch { return false; }
                        });
                }

                if (solid != null && solid.Id != ElementId.InvalidElementId)
                    ogs.SetSurfaceForegroundPatternId(solid.Id);
            }
            catch { }
        }

        private static CommandResult Error(string message, long? viewId, string viewName, int elementCount)
        {
            return CommandResult.Ok(new
            {
                overridden = false,
                view_id = viewId,
                view_name = viewName,
                element_count = elementCount,
                succeeded = 0,
                failed = new object[0],
                overrides_set = new string[0],
                error = message
            });
        }

        /// <summary>Parses a '#RRGGBB' (or 'RRGGBB') hex string into a Revit Color.</summary>
        private static bool TryParseHexColor(string hex, out Color color)
        {
            color = null;
            if (string.IsNullOrWhiteSpace(hex))
                return false;

            var s = hex.Trim();
            if (s.StartsWith("#", StringComparison.Ordinal))
                s = s.Substring(1);

            if (s.Length != 6)
                return false;

            if (!byte.TryParse(s.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
                !byte.TryParse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
                !byte.TryParse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                return false;

            color = new Color(r, g, b);
            return true;
        }
    }
}
