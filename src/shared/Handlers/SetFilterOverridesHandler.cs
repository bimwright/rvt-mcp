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
    public class SetFilterOverridesHandler : IRevitCommand
    {
        public string Name => "set_filter_overrides";

        public string Description =>
            "Set graphic overrides (color, transparency, halftone, line weight) for a filter already applied to a view.";

        public string ParametersSchema => @"{
  ""type"":""object"",
  ""required"":[""filter_id""],
  ""properties"":{
    ""filter_id"":{""type"":""integer""},
    ""view_id"":{""type"":""integer"",""description"":""If omitted, active view.""},
    ""projection_line_color"":{""type"":""string"",""description"":""Hex RGB like '#FF0000'. Optional.""},
    ""surface_foreground_color"":{""type"":""string"",""description"":""Hex RGB. Optional. Solid fill color.""},
    ""cut_line_color"":{""type"":""string"",""description"":""Hex RGB. Optional.""},
    ""transparency"":{""type"":""integer"",""description"":""0-100 surface transparency. Optional.""},
    ""halftone"":{""type"":""boolean"",""description"":""Optional.""},
    ""projection_line_weight"":{""type"":""integer"",""description"":""1-16. Optional.""}
  }
}";

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

            var filterIdToken = request["filter_id"];
            if (filterIdToken == null || filterIdToken.Type == JTokenType.Null)
                return CommandResult.Fail("filter_id is required.");

            long filterIdRaw = request.Value<long>("filter_id");
            long? viewIdRaw = request.Value<long?>("view_id");

            // Resolve view (active view if omitted).
            View view;
            if (viewIdRaw.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(viewIdRaw.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(viewIdRaw.Value));

                view = doc.GetElement(RevitCompat.ToElementId(viewIdRaw.Value)) as View;
                if (view == null)
                    return CommandResult.Fail($"View with ID {viewIdRaw.Value} not found.");
            }
            else
            {
                view = doc.ActiveView;
                if (view == null)
                    return CommandResult.Fail("No active view.");
            }

            var resolvedViewId = RevitCompat.GetId(view.Id);

            if (!view.AreGraphicsOverridesAllowed())
            {
                return CommandResult.Ok(new
                {
                    applied = false,
                    filter_id = filterIdRaw,
                    view_id = resolvedViewId,
                    overrides_set = new string[0],
                    error = $"View '{view.Name}' ({view.ViewType}) does not allow graphics overrides."
                });
            }

            // Resolve filter element.
            if (!RevitCompat.CanRepresentElementId(filterIdRaw))
                return CommandResult.Fail(RevitCompat.ElementIdRangeError(filterIdRaw));

            var filterId = RevitCompat.ToElementId(filterIdRaw);
            var filterElement = doc.GetElement(filterId) as ParameterFilterElement;
            if (filterElement == null)
                return CommandResult.Fail($"Filter with ID {filterIdRaw} not found or is not a ParameterFilterElement.");

            // Verify the filter is applied to the view.
            ICollection<ElementId> appliedFilters;
            try
            {
                appliedFilters = view.GetFilters();
            }
            catch (Exception ex)
            {
                return CommandResult.Fail($"View '{view.Name}' does not support filter overrides: {ex.Message}");
            }

            if (appliedFilters == null || !appliedFilters.Any(id => id == filterId))
            {
                return CommandResult.Ok(new
                {
                    applied = false,
                    filter_id = filterIdRaw,
                    view_id = RevitCompat.GetId(view.Id),
                    overrides_set = new string[0],
                    error = $"Filter '{filterElement.Name}' is not applied to view '{view.Name}'. Add the filter to the view first."
                });
            }

            // Parse the supplied colors up front so an invalid hex fails before the transaction.
            Color projLineColor = null;
            Color surfaceFgColor = null;
            Color cutLineColor = null;

            var projLineColorHex = request.Value<string>("projection_line_color");
            if (!string.IsNullOrWhiteSpace(projLineColorHex))
            {
                if (!TryParseHexColor(projLineColorHex, out projLineColor))
                    return CommandResult.Fail($"Invalid projection_line_color '{projLineColorHex}'. Expected hex RGB like '#FF0000'.");
            }

            var surfaceFgColorHex = request.Value<string>("surface_foreground_color");
            if (!string.IsNullOrWhiteSpace(surfaceFgColorHex))
            {
                if (!TryParseHexColor(surfaceFgColorHex, out surfaceFgColor))
                    return CommandResult.Fail($"Invalid surface_foreground_color '{surfaceFgColorHex}'. Expected hex RGB like '#FF0000'.");
            }

            var cutLineColorHex = request.Value<string>("cut_line_color");
            if (!string.IsNullOrWhiteSpace(cutLineColorHex))
            {
                if (!TryParseHexColor(cutLineColorHex, out cutLineColor))
                    return CommandResult.Fail($"Invalid cut_line_color '{cutLineColorHex}'. Expected hex RGB like '#FF0000'.");
            }

            int? transparency = request.Value<int?>("transparency");
            if (transparency.HasValue && (transparency.Value < 0 || transparency.Value > 100))
                return CommandResult.Fail("transparency must be between 0 and 100.");

            bool? halftone = request.Value<bool?>("halftone");

            int? projLineWeight = request.Value<int?>("projection_line_weight");
            if (projLineWeight.HasValue && (projLineWeight.Value < 1 || projLineWeight.Value > 16))
                return CommandResult.Fail("projection_line_weight must be between 1 and 16.");

            var overridesSet = new List<string>();

            using (var tx = new Transaction(doc, "RvtMcp: set filter overrides"))
            {
                tx.Start();
                try
                {
                    // Start from the existing overrides so unspecified properties are preserved.
                    OverrideGraphicSettings ogs;
                    try
                    {
                        ogs = new OverrideGraphicSettings(view.GetFilterOverrides(filterId));
                    }
                    catch
                    {
                        ogs = new OverrideGraphicSettings();
                    }

                    if (projLineColor != null)
                    {
                        ogs.SetProjectionLineColor(projLineColor);
                        overridesSet.Add("projection_line_color");
                    }

                    if (surfaceFgColor != null)
                    {
                        ogs.SetSurfaceForegroundPatternColor(surfaceFgColor);
                        ogs.SetSurfaceForegroundPatternVisible(true);
                        TrySetSolidSurfaceForegroundPattern(doc, ogs);
                        overridesSet.Add("surface_foreground_color");
                    }

                    if (cutLineColor != null)
                    {
                        ogs.SetCutLineColor(cutLineColor);
                        overridesSet.Add("cut_line_color");
                    }

                    if (transparency.HasValue)
                    {
                        ogs.SetSurfaceTransparency(transparency.Value);
                        overridesSet.Add("transparency");
                    }

                    if (halftone.HasValue)
                    {
                        ogs.SetHalftone(halftone.Value);
                        overridesSet.Add("halftone");
                    }

                    if (projLineWeight.HasValue)
                    {
                        ogs.SetProjectionLineWeight(projLineWeight.Value);
                        overridesSet.Add("projection_line_weight");
                    }

                    view.SetFilterOverrides(filterId, ogs);

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to set filter overrides: {ex.Message}");
                }
            }

            return CommandResult.Ok(new
            {
                applied = true,
                filter_id = filterIdRaw,
                view_id = resolvedViewId,
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

        /// <summary>Parses a "#RRGGBB" (or "RRGGBB") hex string into a Revit Color.</summary>
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

            if (!byte.TryParse(s.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) ||
                !byte.TryParse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) ||
                !byte.TryParse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                return false;

            color = new Color(r, g, b);
            return true;
        }
    }
}
