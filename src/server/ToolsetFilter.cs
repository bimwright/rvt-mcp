using System;
using System.Collections.Generic;
using System.Linq;
using RvtMcp.Plugin;

namespace RvtMcp.Server
{
    /// <summary>
    /// A3 toolset resolver (aspect #3 §A3). Resolves <see cref="RvtMcpConfig.Toolsets"/>
    /// to a concrete set of enabled toolset names, applying defaults + the <c>"all"</c>
    /// shortcut + the <c>--read-only</c> shortcut.
    /// </summary>
    public static class ToolsetFilter
    {
        public static readonly string[] KnownToolsets =
        {
            "query", "create", "modify", "delete", "view",
            "export", "annotation", "mep", "schedule", "families", "graphics", "toolbaker", "meta", "lint",
            "sheets", "materials", "geometry", "rooms", "links", "parameters", "organization", "workflows",
            "structural"
        };

        public static readonly string[] DefaultOn =
        {
            "query", "create", "view", "meta"
        };

        public static readonly string[] WriteCapable =
        {
            "create", "modify", "delete", "schedule", "families", "mep", "graphics", "export", "toolbaker",
            "sheets", "materials", "annotation", "rooms", "links", "parameters", "organization", "workflows",
            "structural"
        };

        public static HashSet<string> Resolve(RvtMcpConfig config)
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

            // Read-only is applied to individual annotated methods during registration.

            if (config != null && !config.EnableToolbakerOrDefault)
                set.Remove("toolbaker");

            return set;
        }
    }
}
