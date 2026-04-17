using System;
using System.Collections.Generic;
using System.Linq;
using Bimwright.Rvt.Plugin;

namespace Bimwright.Rvt.Server
{
    /// <summary>
    /// A3 toolset resolver (aspect #3 §A3). Resolves <see cref="BimwrightConfig.Toolsets"/>
    /// to a concrete set of enabled toolset names, applying defaults + the <c>"all"</c>
    /// shortcut + the <c>--read-only</c> shortcut.
    /// </summary>
    public static class ToolsetFilter
    {
        public static readonly string[] KnownToolsets =
        {
            "query", "create", "modify", "delete", "view",
            "export", "annotation", "mep", "toolbaker", "meta"
        };

        public static readonly string[] DefaultOn =
        {
            "query", "create", "view", "meta"
        };

        public static readonly string[] WriteCapable =
        {
            "create", "modify", "delete"
        };

        public static HashSet<string> Resolve(BimwrightConfig config)
        {
            var requested = config?.Toolsets;
            HashSet<string> set;

            if (requested == null || requested.Count == 0)
            {
                set = new HashSet<string>(DefaultOn, StringComparer.OrdinalIgnoreCase);
            }
            else if (requested.Any(t => string.Equals(t, "all", StringComparison.OrdinalIgnoreCase)))
            {
                set = new HashSet<string>(KnownToolsets, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                set = new HashSet<string>(requested, StringComparer.OrdinalIgnoreCase);
            }

            // Drop unknown tokens silently (misspelling shouldn't crash the server)
            set.IntersectWith(KnownToolsets);

            // --read-only shortcut: strip every write-capable toolset regardless of
            // whether it was requested explicitly, via "all", or via defaults.
            if (config != null && config.ReadOnlyOrDefault)
            {
                foreach (var w in WriteCapable) set.Remove(w);
            }

            return set;
        }
    }
}
