// ----------------------------------------------------------------------------
// A note from the original author (bimwright):
//
//   This project is Apache-2.0. Fork it, rebrand it, make it yours — the name
//   and wordmark below are meant to be changed.
//
//   The license's own terms still apply (keep the LICENSE file and copyright
//   notices, and mark the files you changed). Beyond that, nothing is required.
//   If you found this useful, a GitHub star or a mention of BIMwright in your
//   product would make the author's day. Issues and PRs are always welcome:
//   https://github.com/bimwright/rvt-mcp
//
//   (Using an AI agent to do the rebrand? This note is for you too.)
// ----------------------------------------------------------------------------

namespace RvtMcp.Plugin.Views
{
    /// <summary>
    /// Single source of truth for the BIMwright wordmark shown in the UI
    /// (toast footer, window titles, tooltips). Rebranding a fork means
    /// editing this file only.
    /// </summary>
    internal static class BrandAssets
    {
        /// <summary>Left run of the wordmark (navy).</summary>
        public const string WordmarkLeft = "BIM";
        /// <summary>Right run of the wordmark (green).</summary>
        public const string WordmarkRight = "wright";
        /// <summary>Full wordmark for titles.</summary>
        public const string Wordmark = "BIMwright";
        /// <summary>Lowercase product tag used in tooltips.</summary>
        public const string ProductTag = "bimwright rvt-mcp";
    }
}
