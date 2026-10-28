using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RvtMcp.Plugin.Reports.Model
{
    public static class ReportBodyCanonicalizer
    {
        public static string Canonicalize(string body, IReadOnlyList<MarkdownLinkOccurrence> links,
            IReadOnlyDictionary<int, int> resolvedIndices)
        {
            if (body == null || links == null) return body;

            var result = new StringBuilder(body.Length);
            var position = 0;
            // Parser occurrences are ordered by their positions in this LF body.
            for (var i = 0; i < links.Count; i++)
            {
                var link = links[i];
                if (link == null || link.TargetStart < position || link.TargetLength < 6
                    || link.TargetStart > body.Length - link.TargetLength) continue;

                result.Append(body, position, link.TargetStart - position);
                if (link.Reason == null && resolvedIndices != null
                    && resolvedIndices.TryGetValue(i, out var index) && index >= 0)
                {
                    result.Append("revit:#").Append(index.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    // Preserve the suffix so invalid targets cannot change Markdown structure.
                    result.Append("revit-").Append(body, link.TargetStart + 6, link.TargetLength - 6);
                }
                position = link.TargetStart + link.TargetLength;
            }
            result.Append(body, position, body.Length - position);
            return result.ToString();
        }
    }
}
