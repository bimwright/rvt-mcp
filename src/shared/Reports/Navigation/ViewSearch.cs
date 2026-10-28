using System;
using System.Collections.Generic;
using System.Linq;

namespace RvtMcp.Plugin.Reports.Navigation
{
    public enum ViewCandidateKind { Plan, Default3D, SectionOrElevation }

    /// <summary>A closed view described by cheap checks only; Revit has not computed its visibility yet.</summary>
    public sealed class ViewCandidate
    {
        public ViewCandidate(long id, ViewCandidateKind kind, int elementsInside, double cropArea)
        {
            Id = id; Kind = kind; ElementsInside = elementsInside; CropArea = cropArea;
        }

        public long Id { get; }
        public ViewCandidateKind Kind { get; }

        /// <summary>Elements whose category is shown, that are not hidden, and whose centre lies in the crop and depth.</summary>
        public int ElementsInside { get; }

        /// <summary>Crop area; <see cref="double.PositiveInfinity"/> when the view is not cropped.</summary>
        public double CropArea { get; }
    }

    /// <summary>
    /// Order and budget for choosing a closed view (SPEC section 9, step 6.4). The first visibility check
    /// of a closed view costs Revit 0.3–1.8 s on a large model, so only a few candidates get one.
    /// </summary>
    public static class ViewSearch
    {
        public static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

        /// <summary>Plans, then the default 3D view, then sections and elevations; within each, more elements inside, then the smaller crop.</summary>
        public static List<ViewCandidate> Order(IEnumerable<ViewCandidate> candidates) =>
            (candidates ?? Enumerable.Empty<ViewCandidate>())
                .Where(c => c != null && c.ElementsInside > 0)
                .OrderBy(c => (int)c.Kind)
                .ThenByDescending(c => c.ElementsInside)
                .ThenBy(c => c.CropArea)
                .ToList();

        /// <summary>
        /// First candidate that <paramref name="shows"/> accepts. Candidates are checked while <paramref name="elapsed"/>
        /// is under <paramref name="budget"/>, and always at least one; the first default 3D view is checked even past the budget.
        /// </summary>
        public static ViewCandidate FindFirst(IReadOnlyList<ViewCandidate> ordered, Func<ViewCandidate, bool> shows,
            Func<TimeSpan> elapsed, TimeSpan budget)
        {
            if (ordered == null || shows == null || elapsed == null) return null;
            var checkedAny = false;
            var checked3D = false;
            foreach (var candidate in ordered)
            {
                var keep3D = candidate.Kind == ViewCandidateKind.Default3D && !checked3D;
                if (checkedAny && !keep3D && elapsed() >= budget) continue;
                checkedAny = true;
                if (candidate.Kind == ViewCandidateKind.Default3D) checked3D = true;
                if (shows(candidate)) return candidate;
            }
            return null;
        }
    }
}
