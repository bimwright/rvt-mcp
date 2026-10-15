using System;
using System.Collections.Generic;

namespace RvtMcp.Failures
{
    // Names of transactions that changed the model during one MCP operation, oldest first.
    // Revit reports a rolled-back transaction group with no element changes, only the
    // names it reverted (newest first); an assimilated inner group appears under its group name.
    public sealed class CommittedTransactionLog
    {
        private readonly List<string> _names = new List<string>();

        public int Count => _names.Count;

        public void Committed(IEnumerable<string> names)
        {
            if (names == null) return;
            foreach (var name in names) _names.Add(name ?? "");
        }

        // Removes the reverted names only when they are exactly the newest entries. Otherwise the
        // log is left unchanged and the caller must treat the outcome as unknown, so a real change
        // is never reported as reverted.
        public bool TryRevert(IList<string> revertedNewestFirst)
        {
            if (revertedNewestFirst == null || revertedNewestFirst.Count > _names.Count) return false;
            for (var i = 0; i < revertedNewestFirst.Count; i++)
                if (!string.Equals(_names[_names.Count - 1 - i], revertedNewestFirst[i] ?? "", StringComparison.Ordinal))
                    return false;
            _names.RemoveRange(_names.Count - revertedNewestFirst.Count, revertedNewestFirst.Count);
            return true;
        }
    }
}
