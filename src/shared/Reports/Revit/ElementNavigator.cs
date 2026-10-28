using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RvtMcp.Plugin.Reports.Model;
using RvtMcp.Plugin.Reports.Navigation;
using RvtMcp.Plugin.Reports.Presentation;

namespace RvtMcp.Plugin.Reports.Revit
{
    /// <summary>
    /// SPEC section 9, steps 1–9, on the Revit UI thread. Opens no transaction and edits no view. ShowElements only
    /// gets elements already visible in the active view: given anything else, Revit picks its own view or asks the user.
    /// </summary>
    internal static class ElementNavigator
    {
        // Cheap ranking looks at this many elements; the real visibility check uses all of them.
        private const int RankedElements = 50;

        internal static ReportNavigationResult Navigate(UIApplication app, ReportNavigationRequest request)
        {
            var uidoc = app?.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null || request == null || !ReportDocumentMatch.Matches(request.Document, DocumentMatcher.Identify(doc)))
                return Result(ReportNavigationOutcome.NotMatched);

            var found = new List<Element>();
            var seen = new HashSet<ElementId>();
            var missing = 0;
            foreach (var target in request.Elements ?? (IReadOnlyList<ReportElement>)new ReportElement[0])
            {
                // unique_id only: the same element id in another copy of the model can be a different element.
                var element = string.IsNullOrEmpty(target?.UniqueId) ? null : doc.GetElement(target.UniqueId);
                if (element == null) missing++;
                else if (seen.Add(element.Id)) found.Add(element);
            }
            if (found.Count == 0) return Result(ReportNavigationOutcome.ElementsGone, missing: missing);

            var linkedView = found.OfType<View>().FirstOrDefault(CanActivate);
            if (linkedView != null)
            {
                if (uidoc.ActiveView?.Id != linkedView.Id) uidoc.ActiveView = linkedView;
                return Result(ReportNavigationOutcome.ViewActivated, view: linkedView);
            }

            var graphic = found.Where(IsGraphic).ToList();
            if (graphic.Count == 0) return Result(ReportNavigationOutcome.NoGraphics);
            var ids = graphic.Select(e => e.Id).ToList();

            var active = uidoc.ActiveView;
            var choice = Choose(doc, uidoc, active, request.ViewHint, graphic, ids);
            if (choice == null) return Result(ReportNavigationOutcome.NoViewFound);

            if (active == null || choice.View.Id != active.Id) uidoc.ActiveView = choice.View;
            uidoc.Selection.SetElementIds(ids);
            // Zoom only when the chosen view really is active, so Revit never searches views on its own.
            if (uidoc.ActiveView?.Id == choice.View.Id) uidoc.ShowElements(choice.Visible);
            return Result(ReportNavigationOutcome.Selected, ids.Count, missing, choice.View);
        }

        /// <summary>Step 6: view hint, active view, the open view showing most, owner views of view-specific elements, then closed views.</summary>
        private static Choice Choose(Document doc, UIDocument uidoc, View active, ReportView hint, List<Element> graphic, List<ElementId> ids)
        {
            var hintView = string.IsNullOrEmpty(hint?.UniqueId) ? null : doc.GetElement(hint.UniqueId) as View;
            foreach (var view in new[] { CanActivate(hintView) ? hintView : null, active })
            {
                var visible = Visible(doc, view, ids);
                if (visible.Count > 0) return new Choice(view, visible);
            }

            var open = uidoc.GetOpenUIViews().Select(v => v.ViewId).ToList();
            Choice best = null;
            foreach (var id in open)
            {
                if (active != null && id == active.Id) continue;
                var view = doc.GetElement(id) as View;
                var visible = Visible(doc, view, ids);
                if (visible.Count > (best?.Visible.Count ?? 0)) best = new Choice(view, visible);
            }
            if (best != null) return best;

            // Tags, text and dimensions show only in the view that owns them.
            foreach (var ownerId in graphic.Where(e => e.ViewSpecific).Select(e => e.OwnerViewId).Distinct())
            {
                var view = doc.GetElement(ownerId) as View;
                if (!CanActivate(view)) continue;
                var visible = Visible(doc, view, ids);
                if (visible.Count > 0) return new Choice(view, visible);
            }

            return ChooseClosed(doc, new HashSet<ElementId>(open), graphic, ids);
        }

        /// <summary>Step 6.4: rank closed views with cheap checks, then spend the time budget on real visibility checks.</summary>
        private static Choice ChooseClosed(Document doc, HashSet<ElementId> open, List<Element> graphic, List<ElementId> ids)
        {
            var probes = graphic.Take(RankedElements).Select(Probe.Of).Where(p => p != null).ToList();
            if (probes.Count == 0) return null;
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();
            var views = new Dictionary<long, View>();
            var candidates = new List<ViewCandidate>();
            foreach (var view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
            {
                if (view.IsTemplate || open.Contains(view.Id)) continue;
                var kind = KindOf(view);
                if (kind == null) continue;
                int inside;
                try
                {
                    var range = kind == ViewCandidateKind.Plan ? PlanRange((ViewPlan)view, levels) : null;
                    inside = probes.Count(p => Inside(view, kind.Value, p, range));
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { continue; }
                if (inside == 0) continue;
                var id = RevitCompat.GetId(view.Id);
                views[id] = view;
                candidates.Add(new ViewCandidate(id, kind.Value, inside, CropArea(view)));
            }

            ICollection<ElementId> visible = null;
            var clock = Stopwatch.StartNew();
            var hit = ViewSearch.FindFirst(ViewSearch.Order(candidates), c =>
            {
                visible = Visible(doc, views[c.Id], ids);
                return visible.Count > 0;
            }, () => clock.Elapsed, ViewSearch.Budget);
            return hit == null ? null : new Choice(views[hit.Id], visible);
        }

        private static ViewCandidateKind? KindOf(View view)
        {
            switch (view.ViewType)
            {
                case ViewType.FloorPlan:
                case ViewType.CeilingPlan:
                case ViewType.EngineeringPlan:
                    return view is ViewPlan ? ViewCandidateKind.Plan : (ViewCandidateKind?)null;
                case ViewType.ThreeD:
                    return view is View3D view3D && !view3D.IsPerspective && view.Name.StartsWith("{3D", StringComparison.Ordinal)
                        ? ViewCandidateKind.Default3D : (ViewCandidateKind?)null;
                case ViewType.Section:
                case ViewType.Elevation:
                    return ViewCandidateKind.SectionOrElevation;
                default:
                    return null;
            }
        }

        private static bool Inside(View view, ViewCandidateKind kind, Probe probe, ZRange range)
        {
            if (!InCrop(view, kind, probe.Center)) return false;
            if (range != null && (probe.MaxZ < range.Low || probe.MinZ > range.High)) return false;
            if (CategoryHidden(view, probe.CategoryId)) return false;
            return !probe.Element.IsHidden(view);
        }

        /// <summary>Plans and sections test the crop in the view plane; the default 3D view tests its section box.</summary>
        private static bool InCrop(View view, ViewCandidateKind kind, XYZ point)
        {
            if (kind == ViewCandidateKind.Default3D)
            {
                var view3D = (View3D)view;
                if (!view3D.IsSectionBoxActive) return true;
                var box3D = view3D.GetSectionBox();
                var p = box3D.Transform.Inverse.OfPoint(point);
                return Between(p.X, box3D.Min.X, box3D.Max.X) && Between(p.Y, box3D.Min.Y, box3D.Max.Y) && Between(p.Z, box3D.Min.Z, box3D.Max.Z);
            }
            if (!view.CropBoxActive) return true;
            var box = view.CropBox;
            var local = box.Transform.Inverse.OfPoint(point);
            return Between(local.X, box.Min.X, box.Max.X) && Between(local.Y, box.Min.Y, box.Max.Y);
        }

        private static bool Between(double value, double min, double max) => value >= min && value <= max;

        private static bool CategoryHidden(View view, ElementId categoryId)
        {
            try { return view.GetCategoryHidden(categoryId); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return false; }
        }

        /// <summary>
        /// Heights a plan can show, from its view range. Ceiling plans look up, so the range spans every plane;
        /// an unlimited plane leaves its side open. Levels are compared by elevation, not id: an MEP element has
        /// no level id and often shows in the plan of a level other than its reference level.
        /// </summary>
        private static ZRange PlanRange(ViewPlan plan, List<Level> levels)
        {
            PlanViewRange range;
            try { range = plan.GetViewRange(); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
            var top = Plane(plan, range, PlanViewPlane.TopClipPlane, levels);
            var cut = Plane(plan, range, PlanViewPlane.CutPlane, levels);
            var bottom = Plane(plan, range, PlanViewPlane.BottomClipPlane, levels);
            var depth = Plane(plan, range, PlanViewPlane.ViewDepthPlane, levels);
            var finite = new[] { top, cut, bottom, depth }.Where(z => z.HasValue).Select(z => z.Value).ToList();
            if (finite.Count == 0) return null;
            return new ZRange
            {
                Low = depth == null || bottom == null ? double.NegativeInfinity : finite.Min(),
                High = top == null ? double.PositiveInfinity : finite.Max()
            };
        }

        private static double? Plane(ViewPlan plan, PlanViewRange range, PlanViewPlane which, List<Level> levels)
        {
            var id = range.GetLevelId(which);
            if (id == null || id == PlanViewRange.Unlimited) return null;
            var current = plan.GenLevel;
            Level level;
            if (id == PlanViewRange.Current) level = current;
            else if (id == PlanViewRange.LevelAbove)
                level = current == null ? null : levels.FirstOrDefault(l => l.ProjectElevation > current.ProjectElevation + 1e-6);
            else if (id == PlanViewRange.LevelBelow)
                level = current == null ? null : levels.LastOrDefault(l => l.ProjectElevation < current.ProjectElevation - 1e-6);
            else level = plan.Document.GetElement(id) as Level;
            return level == null ? (double?)null : level.ProjectElevation + range.GetOffset(which);
        }

        private static double CropArea(View view)
        {
            if (view is View3D || !view.CropBoxActive) return double.PositiveInfinity;
            var box = view.CropBox;
            return (box.Max.X - box.Min.X) * (box.Max.Y - box.Min.Y);
        }

        private static ICollection<ElementId> Visible(Document doc, View view, ICollection<ElementId> ids)
        {
            if (view == null || view.IsTemplate || ids.Count == 0) return new List<ElementId>();
            try { return new FilteredElementCollector(doc, view.Id).WherePasses(new ElementIdSetFilter(ids)).ToElementIds(); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return new List<ElementId>(); } // e.g. schedules have no element collector
        }

        private static bool IsGraphic(Element element)
        {
            if (element is ElementType || element is View) return false;
            Category category;
            try { category = element.Category; }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return false; }
            return category != null && (category.CategoryType == CategoryType.Model || category.CategoryType == CategoryType.Annotation);
        }

        private static bool CanActivate(View view) =>
            view != null && !view.IsTemplate && view.ViewType != ViewType.Undefined && view.ViewType != ViewType.Internal
            && view.ViewType != ViewType.ProjectBrowser && view.ViewType != ViewType.SystemBrowser;

        private static ReportNavigationResult Result(ReportNavigationOutcome outcome, int selected = 0, int missing = 0, View view = null) =>
            new ReportNavigationResult
            {
                Outcome = outcome, SelectedCount = selected, MissingCount = missing,
                ViewName = view == null ? null : view is ViewSheet sheet ? sheet.SheetNumber + " - " + sheet.Name : view.Name
            };

        private sealed class Choice
        {
            public Choice(View view, ICollection<ElementId> visible) { View = view; Visible = visible; }
            public View View { get; }
            public ICollection<ElementId> Visible { get; }
        }

        private sealed class ZRange
        {
            public double Low { get; set; }
            public double High { get; set; }
        }

        private sealed class Probe
        {
            public Element Element { get; private set; }
            public ElementId CategoryId { get; private set; }
            public XYZ Center { get; private set; }
            public double MinZ { get; private set; }
            public double MaxZ { get; private set; }

            /// <summary>Null for an element without a model bounding box, such as a view-specific annotation.</summary>
            public static Probe Of(Element element)
            {
                BoundingBoxXYZ box;
                try { box = element.get_BoundingBox(null); }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
                if (box == null || element.Category == null) return null;
                return new Probe
                {
                    Element = element, CategoryId = element.Category.Id,
                    Center = (box.Min + box.Max) / 2, MinZ = box.Min.Z, MaxZ = box.Max.Z
                };
            }
        }
    }
}
