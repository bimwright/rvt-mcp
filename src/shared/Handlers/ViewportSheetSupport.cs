using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>One viewport's geometry on a sheet, in feet, with the reasons any rectangle is unavailable.</summary>
    internal sealed class ViewportSnapshot
    {
        public Viewport Viewport { get; set; }
        public long ViewportId { get; set; }
        public long? ViewId { get; set; }
        public string ViewName { get; set; }
        public string ViewType { get; set; }
        public double Scale { get; set; }
        public string Rotation { get; set; }
        public bool Pinned { get; set; }
        public bool CropActive { get; set; }
        public bool CropVisible { get; set; }
        public double BoxCenterX { get; set; }
        public double BoxCenterY { get; set; }
        public SheetRect? Outline { get; set; }
        public SheetRect? Crop { get; set; }
        public SheetRect? Used { get; set; }
        public string RectSource { get; set; }
        public SheetMargins? Overflow { get; set; }
        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>Shared sheet and viewport reading for the viewport layout tools.</summary>
    internal static class ViewportSheetSupport
    {
        public const double FeetToMm = 304.8;

        // Annotation that hangs past the crop is flagged above this size (0.1 mm).
        private const double OverflowWarnFeet = 0.1 / FeetToMm;
        private const double DegenerateFeet = 1e-9;

        public const string CoordinateFrame =
            "Sheet paper space in millimetres: +X right, +Y up, origin at the sheet's own origin. "
            + "These are not project coordinates; do not mix them with model coordinates or feet.";

        public const string AlignmentRectNote =
            "visible_rect_sheet_mm is the rectangle used for alignment: the crop region when the view crop is active, "
            + "otherwise the view outline. view_outline_sheet_mm also covers annotation that extends past the crop.";

        public static double Mm(double feet) => Math.Round(feet * FeetToMm, 3);

        public static object RectDto(SheetRect? rect)
        {
            if (!rect.HasValue) return null;
            var r = rect.Value;
            return new
            {
                min_x = Mm(r.MinX),
                min_y = Mm(r.MinY),
                max_x = Mm(r.MaxX),
                max_y = Mm(r.MaxY),
                width = Mm(r.Width),
                height = Mm(r.Height),
                center_x = Mm(r.CenterX),
                center_y = Mm(r.CenterY)
            };
        }

        public static bool TryResolveSheet(Document doc, JObject request, out ViewSheet sheet, out string error)
        {
            sheet = null;
            error = null;

            var sheetId = request.Value<long?>("sheet_id") ?? request.Value<long?>("sheetId");
            var sheetNumber = request.Value<string>("sheet_number") ?? request.Value<string>("sheetNumber") ?? "";

            if (sheetId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(sheetId.Value))
                {
                    error = RevitCompat.ElementIdRangeError(sheetId.Value);
                    return false;
                }
                sheet = doc.GetElement(RevitCompat.ToElementId(sheetId.Value)) as ViewSheet;
            }
            else if (!string.IsNullOrEmpty(sheetNumber))
            {
                sheet = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .FirstOrDefault(s => s.SheetNumber.Equals(sheetNumber, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                sheet = doc.ActiveView as ViewSheet;
            }

            if (sheet == null)
            {
                error = "Sheet could not be resolved. Provide a valid sheet_id or sheet_number, or make a sheet the active view.";
                return false;
            }
            if (sheet.IsPlaceholder)
            {
                sheet = null;
                error = "A placeholder sheet has no viewports.";
                return false;
            }
            return true;
        }

        public static List<Viewport> GetViewports(Document doc, ViewSheet sheet)
        {
            return sheet.GetAllViewports()
                .Select(id => doc.GetElement(id) as Viewport)
                .Where(vp => vp != null)
                .OrderBy(vp => RevitCompat.GetId(vp.Id))
                .ToList();
        }

        /// <summary>Union of the title block bounding boxes on the sheet; null when there is none.</summary>
        public static SheetRect? GetSheetFrame(Document doc, ViewSheet sheet)
        {
            var rects = new List<SheetRect>();
            foreach (var element in new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType())
            {
                var box = element.get_BoundingBox(sheet);
                if (box != null)
                    rects.Add(new SheetRect(box.Min.X, box.Min.Y, box.Max.X, box.Max.Y));
            }
            return rects.Count == 0 ? (SheetRect?)null : ViewportLayoutMath.Union(rects);
        }

        public static ViewportSnapshot Read(Document doc, Viewport vp)
        {
            var snap = new ViewportSnapshot
            {
                Viewport = vp,
                ViewportId = RevitCompat.GetId(vp.Id),
                Pinned = vp.Pinned,
                Rotation = vp.Rotation.ToString()
            };

            var view = doc.GetElement(vp.ViewId) as View;
            if (view == null)
            {
                snap.Warnings.Add("view_not_found");
                return snap;
            }

            snap.ViewId = RevitCompat.GetId(view.Id);
            snap.ViewName = view.Name;
            snap.ViewType = view.ViewType.ToString();
            snap.Scale = view.Scale;

            try
            {
                snap.CropActive = view.CropBoxActive;
                snap.CropVisible = view.CropBoxVisible;
            }
            catch (Exception)
            {
                snap.Warnings.Add("crop_flags_unavailable");
            }

            var center = vp.GetBoxCenter();
            if (center == null)
            {
                snap.Warnings.Add("viewport_center_unavailable");
                return snap;
            }
            snap.BoxCenterX = center.X;
            snap.BoxCenterY = center.Y;

            if (vp.Rotation != ViewportRotation.None)
            {
                snap.Warnings.Add("rotated_viewport_not_supported");
                return snap;
            }
            if (view is View3D view3D && view3D.IsPerspective)
            {
                snap.Warnings.Add("perspective_view_not_supported");
                return snap;
            }
            if (snap.Scale <= 0)
            {
                snap.Warnings.Add("view_scale_invalid");
                return snap;
            }

            BoundingBoxUV outline = null;
            try { outline = view.Outline; }
            catch (Exception) { }
            if (outline == null)
            {
                snap.Warnings.Add("view_outline_unavailable");
                return snap;
            }

            snap.Outline = ViewportLayoutMath.OutlineOnSheet(
                center.X, center.Y, outline.Min.U, outline.Min.V, outline.Max.U, outline.Max.V);

            if (snap.CropActive)
            {
                BoundingBoxXYZ crop = null;
                try { crop = view.CropBox; }
                catch (Exception) { }

                if (crop != null)
                {
                    var rect = ViewportLayoutMath.CropRegionOnSheet(
                        center.X, center.Y, outline.Min.U, outline.Min.V, outline.Max.U, outline.Max.V,
                        crop.Min.X, crop.Min.Y, crop.Max.X, crop.Max.Y, snap.Scale);
                    if (rect.Width > DegenerateFeet && rect.Height > DegenerateFeet)
                        snap.Crop = rect;
                    else
                        snap.Warnings.Add("crop_region_degenerate");
                }
            }

            if (snap.Crop.HasValue)
            {
                snap.Used = snap.Crop;
                snap.RectSource = "crop_region";
                snap.Overflow = ViewportLayoutMath.OverflowBeyond(snap.Outline.Value, snap.Crop.Value);
                if (snap.Overflow.Value.Max > OverflowWarnFeet)
                    snap.Warnings.Add("annotation_extends_past_crop");
            }
            else
            {
                snap.Used = snap.Outline;
                snap.RectSource = "view_outline";
                if (!snap.CropActive)
                    snap.Warnings.Add("crop_inactive_view_outline_used");
            }

            return snap;
        }

        public static object OverflowDto(SheetMargins? margins)
        {
            if (!margins.HasValue) return null;
            var m = margins.Value;
            return new { left = Mm(m.Left), right = Mm(m.Right), top = Mm(m.Top), bottom = Mm(m.Bottom) };
        }

        public static object SnapshotDto(ViewportSnapshot s)
        {
            return new
            {
                viewport_id = s.ViewportId,
                view_id = s.ViewId,
                view_name = s.ViewName,
                view_type = s.ViewType,
                scale = s.Scale,
                rotation = s.Rotation,
                pinned = s.Pinned,
                crop_active = s.CropActive,
                crop_visible = s.CropVisible,
                box_center_sheet_mm = new { x = Mm(s.BoxCenterX), y = Mm(s.BoxCenterY) },
                rect_source = s.RectSource,
                visible_rect_sheet_mm = RectDto(s.Used),
                crop_rect_sheet_mm = RectDto(s.Crop),
                view_outline_sheet_mm = RectDto(s.Outline),
                annotation_overflow_mm = OverflowDto(s.Overflow),
                warnings = s.Warnings
            };
        }
    }
}
