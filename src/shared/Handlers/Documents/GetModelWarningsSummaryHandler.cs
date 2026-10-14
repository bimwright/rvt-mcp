using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class GetModelWarningsSummaryHandler : IRevitCommand
    {
        public string Name => "get_model_warnings_summary";
        public string Description => "Return a grouped summary of doc.GetWarnings(): per warning type, count + optional example failing element ids.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""include_examples"":{""type"":""boolean"",""default"":true},""max_examples_per_type"":{""type"":""integer"",""default"":5,""minimum"":0,""maximum"":100},""max_warning_types"":{""type"":""integer"",""default"":200,""minimum"":1,""maximum"":1000}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var includeExamples = req.Value<bool?>("include_examples") ?? true;
            var maxExamples = req.Value<int?>("max_examples_per_type") ?? 5;
            var maxWarningTypes = req.Value<int?>("max_warning_types") ?? 200;
            if (maxExamples < 0 || maxExamples > 100)
                return CommandResult.Fail("max_examples_per_type must be between 0 and the hard maximum of 100.");
            if (maxWarningTypes < 1 || maxWarningTypes > 1000)
                return CommandResult.Fail("max_warning_types must be between 1 and the hard maximum of 1000.");

            var warnings = doc.GetWarnings();
            var grouped = warnings
                .GroupBy(w => w.GetDescriptionText())
                .OrderByDescending(g => g.Count())
                .Select(g => new
                {
                    description = g.Key,
                    count = g.Count(),
                    severity = g.First().GetSeverity().ToString(),
                    examples = includeExamples
                        ? g.Take(maxExamples).Select(w => new
                        {
                            failing_element_ids = w.GetFailingElements().Select(RevitCompat.GetId).ToList()
                        }).ToList<object>()
                        : null
                })
                .Take(maxWarningTypes)
                .ToList();
            var uniqueDescriptionCount = warnings.Select(w => w.GetDescriptionText()).Distinct().Count();

            return CommandResult.Ok(new
            {
                total_warnings = warnings.Count,
                unique_descriptions = uniqueDescriptionCount,
                returned_warning_types = grouped.Count,
                truncated = grouped.Count < uniqueDescriptionCount,
                warnings = grouped
            });
        }
    }
}
