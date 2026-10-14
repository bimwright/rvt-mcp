using System;
using System.Linq;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ListBakedToolsHandler : IRevitCommand
    {
        public string Name => "list_baked_tools";
        public string Description => "List a filtered, bounded page of baked (user-compiled) tools with usage stats";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""name_filter"":{""type"":""string""},""start_index"":{""type"":""integer"",""default"":0,""minimum"":0},""limit"":{""type"":""integer"",""default"":100,""minimum"":1,""maximum"":500}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            if (!ResponsePaging.TryParse(request, "start_index", "limit", 100, 500, out var paging, out var pagingError))
                return CommandResult.Fail(pagingError);
            var nameFilter = request.Value<string>("name_filter");

            var registry = App.Instance?.BakedToolRegistry;
            if (registry == null)
                return CommandResult.Ok(new { count = 0, tools = new object[0], truncated = false });

            var metas = registry.GetAllSortedForList()
                .Where(m => string.IsNullOrWhiteSpace(nameFilter)
                    || (m.Name ?? string.Empty).IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToArray();
            var page = ResponsePaging.Slice(metas, paging.StartIndex, paging.MaxResults);
            var tools = page.Items.Select(m => new
            {
                name = m.Name,
                description = m.Description,
                source = m.Source,
                params_schema = ParseObject(m.ParametersSchema),
                usage_count = m.UsageCount,
                usage_score_30d = m.UsageScore30d,
                last_used = m.LastUsedAt,
                compat_map = ParseObject(m.CompatMap),
                failure_rate = m.FailureRate,
                lifecycle_state = m.LifecycleState,
                created_utc = m.CreatedUtc
            }).ToArray();

            return CommandResult.Ok(new
            {
                count = page.TotalCount,
                start_index = page.StartIndex,
                returned_count = page.ReturnedCount,
                truncated = page.Truncated,
                next_index = page.NextIndex,
                tools
            });
        }

        private static JObject ParseObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new JObject();
            try
            {
                return JObject.Parse(json);
            }
            catch (JsonException)
            {
                return new JObject();
            }
        }
    }
}
