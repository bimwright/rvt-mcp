using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>Shared by SetViewStyleHandler, SetViewOrientationHandler and CreateCameraViewHandler.</summary>
    internal static class ViewPresentationSupport
    {
        private const double MmPerFoot = 304.8;

        public static View ResolveView(UIDocument uidoc, JObject request, bool threeDOnly, out string error)
        {
            error = null;
            var doc = uidoc.Document;
            var id = request.Value<long?>("view_id");
            var name = request.Value<string>("view_name");
            View view;
            if (id.HasValue)
            {
                view = doc.GetElement(RevitCompat.ToElementId(id.Value)) as View;
                if (view == null) error = "No view with id " + id.Value + ".";
            }
            else if (!string.IsNullOrWhiteSpace(name))
            {
                var candidates = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => !v.IsTemplate && (!threeDOnly || v is View3D))
                    .ToList();
                view = candidates.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.Ordinal))
                    ?? candidates.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
                if (view == null)
                    error = "No " + (threeDOnly ? "3D view" : "view") + " named '" + name + "'. Available: "
                        + string.Join(", ", candidates.Select(v => v.Name).Take(30));
            }
            else
            {
                view = uidoc.ActiveView;
                if (view == null) error = "No active view; pass view_id or view_name.";
            }

            if (view != null && view.IsTemplate)
            {
                error = "View '" + view.Name + "' is a template.";
                view = null;
            }
            return view;
        }

        public static XYZ ReadPointMm(JObject request, string key, out string error)
        {
            var v = ViewOrientationMath.ReadVector(request, key, true, " in mm", out error);
            return v == null ? null : new XYZ(v[0] / MmPerFoot, v[1] / MmPerFoot, v[2] / MmPerFoot);
        }

        public static double[] ReadUp(JObject request, out string error)
            => ViewOrientationMath.ReadVector(request, "up", false, "", out error);

        public static ViewOrientation3D MakeOrientation(XYZ eye, XYZ target, double[] up, out string error)
        {
            error = null;
            var forward = target - eye;
            var trueUp = ViewOrientationMath.PerpendicularUp(new[] { forward.X, forward.Y, forward.Z }, up);
            if (trueUp == null)
            {
                error = "eye and target must be different points.";
                return null;
            }
            return new ViewOrientation3D(eye, new XYZ(trueUp[0], trueUp[1], trueUp[2]), forward.Normalize());
        }

        // SetOrientation moves Revit's temporary camera, which a transaction rollback leaves where it was set.
        public static void RestoreCamera(View3D view, ViewOrientation3D before)
        {
            // A restore failure must not replace the error that caused the rollback.
            try { view.SetOrientation(before); }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex) { App.DebugLog("Camera restore after rollback failed: " + ex.Message); }
        }

        public static object Describe(View3D view)
        {
            var o = view.GetOrientation();
            return new
            {
                view_id = RevitCompat.GetId(view.Id),
                view_name = view.Name,
                perspective = view.IsPerspective,
                eye = PointMm(o.EyePosition),
                forward = Direction(o.ForwardDirection),
                up = Direction(o.UpDirection)
            };
        }

        public static bool TryZoomToFit(UIDocument uidoc, View view)
        {
            // ZoomToFit only works on a view that is open in the UI, and runs after the commit, so a
            // failure here must not turn a saved orientation into an error.
            try
            {
                var uiView = uidoc.GetOpenUIViews().FirstOrDefault(u => u.ViewId == view.Id);
                if (uiView == null) return false;
                uiView.ZoomToFit();
                return true;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return false;
            }
        }

        private static double[] PointMm(XYZ p)
            => new[] { Math.Round(p.X * MmPerFoot, 1), Math.Round(p.Y * MmPerFoot, 1), Math.Round(p.Z * MmPerFoot, 1) };

        private static double[] Direction(XYZ p)
            => new[] { Math.Round(p.X, 4), Math.Round(p.Y, 4), Math.Round(p.Z, 4) };
    }
}
