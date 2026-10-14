using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class AnalyzeModelStatisticsHandler : IRevitCommand
    {
        private const int DefaultCap = 100_000;

        public string Name => "analyze_model_statistics";
        public string Description => "Analyze model complexity with element counts by category. Iteration is capped at maxElements (default 100000) to keep the Revit UI responsive on large models.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""maxElements"":{""type"":""integer"",""description"":""Max elements to iterate. Default 100000. Set higher only if you need full accuracy on a very large model and accept a longer UI freeze.""}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            int maxElements = DefaultCap;
            if (!string.IsNullOrWhiteSpace(paramsJson))
            {
                var request = JObject.Parse(paramsJson);
                var requested = request.Value<int?>("maxElements");
                if (requested.HasValue && requested.Value > 0)
                    maxElements = requested.Value;
            }

            var collector = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType();

            var stats = new Dictionary<string, int>();
            int processed = 0;
            bool truncated = false;

            foreach (var el in collector)
            {
                if (processed >= maxElements)
                {
                    truncated = true;
                    break;
                }
                processed++;
                var catName = el.Category?.Name ?? "Uncategorized";
                if (stats.ContainsKey(catName))
                    stats[catName]++;
                else
                    stats[catName] = 1;
            }

            var categories = stats
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new { category = kv.Key, count = kv.Value })
                .ToArray();

            return CommandResult.Ok(new
            {
                projectName = doc.Title,
                elementsCounted = processed,
                totalCategories = categories.Length,
                truncated,
                cap = maxElements,
                note = truncated
                    ? $"Model exceeds {maxElements} elements; stats reflect the first {processed} enumerated. Raise maxElements for full accuracy."
                    : null,
                categories
            });
        }
    }
}
