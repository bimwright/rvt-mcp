using System;
using System.Collections.Generic;

namespace RvtMcp.Plugin.Reports.Model
{
    public enum ReportParseMode
    {
        Raw,
        Stored
    }

    public sealed class LinkTarget
    {
        public bool IsValid => Reason == null;
        public string Reason { get; }
        public IReadOnlyList<long> Ids { get; }
        public long? ViewId { get; }
        public string DocumentToken { get; }
        public int? StoredIndex { get; }

        internal LinkTarget(string reason, IReadOnlyList<long> ids = null, long? viewId = null,
            string documentToken = null, int? storedIndex = null)
        {
            Reason = reason;
            Ids = ids ?? new long[0];
            ViewId = viewId;
            DocumentToken = documentToken;
            StoredIndex = storedIndex;
        }
    }

    public static class LinkSyntax
    {
        public static LinkTarget Parse(string target, ReportParseMode mode = ReportParseMode.Raw, int storedLinkCount = 0)
        {
            if (target == null || !target.StartsWith("revit:", StringComparison.OrdinalIgnoreCase))
                return Invalid();

            if (mode == ReportParseMode.Stored)
            {
                if (target.Length < 8 || target[6] != '#') return Invalid();
                var index = 0;
                for (var i = 7; i < target.Length; i++)
                {
                    var digit = target[i] - '0';
                    if (digit < 0 || digit > 9 || index > (int.MaxValue - digit) / 10) return Invalid();
                    index = index * 10 + digit;
                }
                return index < storedLinkCount ? new LinkTarget(null, storedIndex: index) : Invalid();
            }
            if (mode != ReportParseMode.Raw) return Invalid();

            var offset = 6;
            var ids = new List<long>();
            var seen = new HashSet<long>();
            do
            {
                if (!ReadId(target, ref offset, out var id)) return Invalid();
                if (seen.Add(id)) ids.Add(id);
                if (offset == target.Length || target[offset] != ',') break;
                offset++;
            } while (true);

            long? viewId = null;
            string documentToken = null;
            if (offset < target.Length)
            {
                if (target[offset++] != '?' || offset == target.Length) return Invalid();
                do
                {
                    if (Matches(target, offset, "view="))
                    {
                        if (viewId.HasValue) return Invalid();
                        offset += 5;
                        if (!ReadId(target, ref offset, out var view)) return Invalid();
                        viewId = view;
                    }
                    else if (Matches(target, offset, "doc="))
                    {
                        if (documentToken != null) return Invalid();
                        offset += 4;
                        if (target.Length - offset < 32) return Invalid();
                        for (var i = 0; i < 32; i++)
                            if (!IsHex(target[offset + i])) return Invalid();
                        documentToken = target.Substring(offset, 32).ToLowerInvariant();
                        offset += 32;
                    }
                    else return Invalid();

                    if (offset == target.Length) break;
                    if (target[offset++] != '&' || offset == target.Length) return Invalid();
                } while (true);
            }

            return new LinkTarget(null, ids.AsReadOnly(), viewId, documentToken);
        }

        private static bool ReadId(string text, ref int offset, out long value)
        {
            value = 0;
            if (offset == text.Length || text[offset] < '1' || text[offset] > '9') return false;
            do
            {
                var digit = text[offset] - '0';
                if (value > (long.MaxValue - digit) / 10) return false;
                value = value * 10 + digit;
                offset++;
            } while (offset < text.Length && text[offset] >= '0' && text[offset] <= '9');
            return true;
        }

        private static bool Matches(string text, int offset, string value) =>
            text.Length - offset >= value.Length && string.CompareOrdinal(text, offset, value, 0, value.Length) == 0;

        private static bool IsHex(char value) =>
            value >= '0' && value <= '9' || value >= 'a' && value <= 'f' || value >= 'A' && value <= 'F';

        private static LinkTarget Invalid() => new LinkTarget("bad_syntax");
    }
}
