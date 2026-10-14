#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    /// <summary>Shared validation and metadata for offset-based response paging.</summary>
    public static class ResponsePaging
    {
        public sealed class Options
        {
            public int StartIndex { get; set; }
            public int MaxResults { get; set; }
        }

        public sealed class Page<T>
        {
            public T[] Items { get; set; } = Array.Empty<T>();
            public int TotalCount { get; set; }
            public int StartIndex { get; set; }
            public int ReturnedCount { get; set; }
            public bool Truncated { get; set; }
            public int? NextIndex { get; set; }
        }

        public static bool TryParse(
            JObject? request,
            string startParameter,
            string maxParameter,
            int defaultPageSize,
            int hardMaximum,
            out Options options,
            out string? error)
        {
            options = new Options();
            error = null;

            if (defaultPageSize < 1 || hardMaximum < 1 || defaultPageSize > hardMaximum)
                throw new ArgumentOutOfRangeException(nameof(defaultPageSize));

            var startIndex = request?.Value<int?>(startParameter) ?? 0;
            var maxResults = request?.Value<int?>(maxParameter) ?? defaultPageSize;

            if (startIndex < 0)
            {
                error = $"{startParameter} must be at least 0.";
                return false;
            }

            if (maxResults < 1)
            {
                error = $"{maxParameter} must be at least 1.";
                return false;
            }

            if (maxResults > hardMaximum)
            {
                error = $"Limit exceeded: {maxParameter} exceeds the hard maximum of {hardMaximum}.";
                return false;
            }

            options.StartIndex = startIndex;
            options.MaxResults = maxResults;
            return true;
        }

        public static Page<T> Slice<T>(IReadOnlyList<T>? source, int startIndex, int maxResults)
        {
            if (startIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(startIndex));
            if (maxResults < 1)
                throw new ArgumentOutOfRangeException(nameof(maxResults));

            var items = source ?? Array.Empty<T>();
            var pageItems = items.Skip(startIndex).Take(maxResults).ToArray();
            var nextIndex = startIndex + pageItems.Length;
            var truncated = nextIndex < items.Count;

            return new Page<T>
            {
                Items = pageItems,
                TotalCount = items.Count,
                StartIndex = startIndex,
                ReturnedCount = pageItems.Length,
                Truncated = truncated,
                NextIndex = truncated ? nextIndex : (int?)null
            };
        }
    }
}
