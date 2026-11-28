using System;
using System.Collections.Generic;

namespace RvtMcp.Plugin.Handlers
{
    public sealed class PurgeUnusedPlan<T>
    {
        public int TotalCandidates { get; set; }
        public int SafeToPurge { get; set; }
        public int SkippedDueToReferences { get; set; }
        public int Returned { get; set; }
        public bool Truncated { get; set; }
        public List<T> Items { get; set; } = new List<T>();
    }

    /// <summary>
    /// Host-free selection and accounting for <c>purge_unused</c> so <c>safe_to_purge</c> and
    /// <c>skipped_due_to_references</c> reflect the full candidate set rather than the <c>limit</c>-truncated page.
    /// </summary>
    public static class PurgeUnusedPlanning
    {
        public static PurgeUnusedPlan<T> Select<T>(
            IReadOnlyList<T> candidates,
            Func<T, long> getId,
            ISet<long> referencedIds,
            int limit)
        {
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));
            if (getId == null) throw new ArgumentNullException(nameof(getId));
            if (referencedIds == null) throw new ArgumentNullException(nameof(referencedIds));
            if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit), "limit must be at least 1.");

            var safeCount = 0;
            var skippedCount = 0;
            var page = new List<T>(Math.Min(limit, candidates.Count));

            foreach (var candidate in candidates)
            {
                if (referencedIds.Contains(getId(candidate)))
                {
                    skippedCount++;
                    continue;
                }

                safeCount++;
                if (page.Count < limit)
                    page.Add(candidate);
            }

            return new PurgeUnusedPlan<T>
            {
                TotalCandidates = candidates.Count,
                SafeToPurge = safeCount,
                SkippedDueToReferences = skippedCount,
                Returned = page.Count,
                Truncated = safeCount > page.Count,
                Items = page
            };
        }
    }
}
