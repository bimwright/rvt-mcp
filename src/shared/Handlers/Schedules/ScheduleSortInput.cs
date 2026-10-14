using System;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    internal static class ScheduleSortInput
    {
        public static bool Ascending(JObject spec)
        {
            bool? ascending = spec.Value<bool?>("ascending");
            foreach (var key in new[] { "order", "sortOrder" })
            {
                var token = spec[key];
                if (token == null || token.Type == JTokenType.Null) continue;
                var text = token.Type == JTokenType.String ? token.Value<string>().Trim() : null;
                bool direction;
                if (string.Equals(text, "ascending", StringComparison.OrdinalIgnoreCase)) direction = true;
                else if (string.Equals(text, "descending", StringComparison.OrdinalIgnoreCase)) direction = false;
                else throw new ArgumentException(key + " must be Ascending or Descending.");
                if (ascending.HasValue && ascending.Value != direction)
                    throw new ArgumentException("Conflicting ascending/order/sortOrder directions.");
                ascending = direction;
            }
            return ascending ?? true;
        }
    }
}
