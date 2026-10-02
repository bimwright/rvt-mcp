using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class AlignViewportsHandler : IRevitCommand
    {
        public string Name => "align_viewports";
        public string Description => "Align viewports to a reference viewport, or centre them on the sheet, using sheet-space millimetres of the visible crop rectangle.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""mode""],
  ""properties"": {
    ""sheet_id"": { ""type"": ""integer"" },
    ""sheet_number"": { ""type"": ""string"" },
    ""mode"": { ""type"": ""string"", ""enum"": [""left"", ""right"", ""top"", ""bottom"", ""center_x"", ""center_y"", ""center"", ""center_on_sheet""] },
    ""reference_viewport_id"": { ""type"": ""integer"" },
    ""viewport_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }
  }
}";

        private const string ModeList = "left, right, top, bottom, center_x, center_y, center, center_on_sheet";

        // Moves smaller than 0.0005 mm are treated as already aligned.
        private const double MoveToleranceFeet = 0.0005 / ViewportSheetSupport.FeetToMm;

        // After applying, every rectangle must land within 0.01 mm of the plan.
        private const double VerifyToleranceMm = 0.01;

        private sealed class Move
        {
            public ViewportSnapshot Before;
            public double Dx;
            public double Dy;
            public bool Changed;
        }

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
            catch (JsonException ex)
            {
                return CommandResult.Fail($"Parameters must be a JSON object: {ex.Message}");
            }

            var modeText = request.Value<string>("mode");
            if (!ViewportLayoutMath.TryParseMode(modeText, out var mode))
                return CommandResult.Fail("mode is required and must be one of: " + ModeList + ".");

            var dryRun = request.Value<bool?>("dry_run") ?? request.Value<bool?>("dryRun") ?? true;

            if (!ViewportSheetSupport.TryResolveSheet(doc, request, out var sheet, out var sheetError))
                return CommandResult.Fail(sheetError);

            var viewports = ViewportSheetSupport.GetViewports(doc, sheet);
            var byId = viewports.ToDictionary(vp => RevitCompat.GetId(vp.Id));

            // Reference viewport
            ViewportSnapshot reference = null;
            long? referenceId = request.Value<long?>("reference_viewport_id") ?? request.Value<long?>("referenceViewportId");
            if (ViewportLayoutMath.NeedsReference(mode))
            {
                if (!referenceId.HasValue)
                    return CommandResult.Fail("reference_viewport_id is required for mode '" + modeText + "'.");
                if (!byId.TryGetValue(referenceId.Value, out var referenceViewport))
                    return CommandResult.Fail("reference_viewport_id " + referenceId.Value + " is not a viewport on sheet " + sheet.SheetNumber + ".");

                reference = ViewportSheetSupport.Read(doc, referenceViewport);
                if (!reference.Used.HasValue)
                    return CommandResult.Fail("The reference viewport has no usable rectangle: " + string.Join(", ", reference.Warnings) + ".");
            }
            else if (referenceId.HasValue)
            {
                return CommandResult.Fail("reference_viewport_id is not used by mode 'center_on_sheet'.");
            }

            // Targets
            List<Viewport> targets;
            var idToken = request["viewport_ids"];
            if (idToken != null && idToken.Type != JTokenType.Null)
            {
                if (!(idToken is JArray idArray) || idArray.Count == 0)
                    return CommandResult.Fail("viewport_ids must be a non-empty array of viewport ids when supplied.");

                var ids = new List<long>();
                foreach (var token in idArray)
                {
                    if (token.Type != JTokenType.Integer)
                        return CommandResult.Fail("viewport_ids must contain integers.");
                    ids.Add(token.Value<long>());
                }

                var unknown = ids.Where(id => !byId.ContainsKey(id)).Distinct().OrderBy(id => id).ToArray();
                if (unknown.Length > 0)
                    return CommandResult.Fail("Viewport id(s) not on sheet " + sheet.SheetNumber + ": " + string.Join(", ", unknown));

                targets = ids.Distinct().Where(id => id != referenceId).Select(id => byId[id]).ToList();
            }
            else
            {
                targets = viewports.Where(vp => RevitCompat.GetId(vp.Id) != referenceId).ToList();
            }

            if (targets.Count == 0)
                return CommandResult.Fail("No viewports to move. Select at least one viewport other than the reference.");

            var snapshots = targets.Select(vp => ViewportSheetSupport.Read(doc, vp)).ToList();

            var blocked = new List<string>();
            foreach (var s in snapshots)
            {
                if (s.Pinned)
                    blocked.Add(s.ViewportId + " (pinned)");
                else if (!s.Used.HasValue)
                    blocked.Add(s.ViewportId + " (" + (s.Warnings.Count > 0 ? string.Join(", ", s.Warnings) : "no usable rectangle") + ")");
            }
            if (blocked.Count > 0)
                return CommandResult.Fail("Cannot move viewport(s): " + string.Join("; ", blocked)
                    + ". Unpin them or leave them out of viewport_ids. Nothing was changed.");

            // Plan
            SheetRect? frame = null;
            var moves = new List<Move>();
            if (mode == ViewportAlignMode.CenterOnSheet)
            {
                frame = ViewportSheetSupport.GetSheetFrame(doc, sheet);
                if (!frame.HasValue)
                    return CommandResult.Fail("Sheet " + sheet.SheetNumber + " has no title block, so the sheet frame is unknown and the viewports cannot be centred on it.");

                var group = ViewportLayoutMath.Union(snapshots.Select(s => s.Used.Value));
                var delta = ViewportLayoutMath.CenterGroupOnFrame(frame.Value, group);
                foreach (var s in snapshots)
                    moves.Add(NewMove(s, delta.Dx, delta.Dy));
            }
            else
            {
                foreach (var s in snapshots)
                {
                    var delta = ViewportLayoutMath.AlignDelta(reference.Used.Value, s.Used.Value, mode);
                    moves.Add(NewMove(s, delta.Dx, delta.Dy));
                }
            }

            var changed = moves.Where(m => m.Changed).ToList();

            if (dryRun || changed.Count == 0)
            {
                return CommandResult.Ok(BuildResult(sheet, modeText, reference, frame, moves, null,
                    applied: false, dryRun: dryRun,
                    note: dryRun
                        ? "Nothing was changed. Call again with dry_run=false to apply these moves."
                        : "Nothing to move: the viewports already satisfy this alignment."));
            }

            double maxErrorMm;
            using (var tx = new Transaction(doc, "RvtMcp: align viewports"))
            {
                tx.Start();
                try
                {
                    foreach (var m in changed)
                    {
                        var center = m.Before.Viewport.GetBoxCenter();
                        m.Before.Viewport.SetBoxCenter(new XYZ(center.X + m.Dx, center.Y + m.Dy, center.Z));
                    }
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    return CommandResult.Fail("Could not move the viewports; nothing was changed: " + ex.Message);
                }
            }

            // Read back what Revit actually holds.
            var after = new Dictionary<long, ViewportSnapshot>();
            maxErrorMm = 0.0;
            foreach (var m in moves)
            {
                var read = ViewportSheetSupport.Read(doc, m.Before.Viewport);
                after[m.Before.ViewportId] = read;
                if (m.Changed && read.Used.HasValue)
                {
                    var expected = m.Before.Used.Value.Translated(m.Dx, m.Dy);
                    var error = new[]
                    {
                        Math.Abs(read.Used.Value.MinX - expected.MinX), Math.Abs(read.Used.Value.MaxX - expected.MaxX),
                        Math.Abs(read.Used.Value.MinY - expected.MinY), Math.Abs(read.Used.Value.MaxY - expected.MaxY)
                    }.Max() * ViewportSheetSupport.FeetToMm;
                    if (error > maxErrorMm) maxErrorMm = error;
                }
            }

            return CommandResult.Ok(BuildResult(sheet, modeText, reference, frame, moves, after,
                applied: true, dryRun: false,
                note: "Applied as one Revit Undo step. Read back max_position_error_mm = " + Math.Round(maxErrorMm, 4)
                    + (maxErrorMm <= VerifyToleranceMm ? " (verified)." : " (exceeds " + VerifyToleranceMm + " mm; check the sheet).")));
        }

        private static Move NewMove(ViewportSnapshot before, double dx, double dy)
        {
            return new Move
            {
                Before = before,
                Dx = dx,
                Dy = dy,
                Changed = Math.Abs(dx) > MoveToleranceFeet || Math.Abs(dy) > MoveToleranceFeet
            };
        }

        private static object BuildResult(
            ViewSheet sheet, string mode, ViewportSnapshot reference, SheetRect? frame,
            List<Move> moves, Dictionary<long, ViewportSnapshot> after,
            bool applied, bool dryRun, string note)
        {
            var items = moves.Select(m =>
            {
                var plannedAfter = m.Before.Used.Value.Translated(m.Dx, m.Dy);
                SheetRect? rectAfter = plannedAfter;
                if (after != null && after.TryGetValue(m.Before.ViewportId, out var read) && read.Used.HasValue)
                    rectAfter = read.Used;

                return new
                {
                    viewport_id = m.Before.ViewportId,
                    view_name = m.Before.ViewName,
                    changed = m.Changed,
                    delta_sheet_mm = new { x = ViewportSheetSupport.Mm(m.Dx), y = ViewportSheetSupport.Mm(m.Dy) },
                    rect_before_sheet_mm = ViewportSheetSupport.RectDto(m.Before.Used),
                    rect_after_sheet_mm = ViewportSheetSupport.RectDto(rectAfter),
                    rect_source = m.Before.RectSource,
                    warnings = m.Before.Warnings
                };
            }).ToList();

            return new
            {
                dry_run = dryRun,
                applied,
                mode,
                sheet_id = RevitCompat.GetId(sheet.Id),
                sheet_number = sheet.SheetNumber,
                units = "mm",
                coordinate_frame = ViewportSheetSupport.CoordinateFrame,
                alignment_rect_note = ViewportSheetSupport.AlignmentRectNote,
                reference_viewport_id = reference?.ViewportId,
                reference_rect_sheet_mm = ViewportSheetSupport.RectDto(reference?.Used),
                frame_sheet_mm = ViewportSheetSupport.RectDto(frame),
                moved_count = applied ? moves.Count(m => m.Changed) : 0,
                planned_move_count = moves.Count(m => m.Changed),
                unchanged_count = moves.Count(m => !m.Changed),
                moves = items,
                note
            };
        }
    }
}
