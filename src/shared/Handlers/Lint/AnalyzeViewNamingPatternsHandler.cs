using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Lint;

namespace RvtMcp.Plugin.Handlers
{
    public class AnalyzeViewNamingPatternsHandler : IRevitCommand
    {
        public string Name => "analyze_view_naming_patterns";
        public string Description => "Infer dominant view-naming pattern from project with bounded pattern and outlier detail.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""max_patterns"":{""type"":""integer"",""default"":50,""minimum"":1,""maximum"":500},""start_outlier"":{""type"":""integer"",""default"":0,""minimum"":0},""max_outliers"":{""type"":""integer"",""default"":20,""minimum"":1,""maximum"":100}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");
            var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            var maxPatterns = request.Value<int?>("max_patterns") ?? 50;
            if (maxPatterns < 1 || maxPatterns > 500)
                return CommandResult.Fail("max_patterns must be between 1 and the hard maximum of 500.");
            if (!ResponsePaging.TryParse(request, "start_outlier", "max_outliers", 20, 100, out var outlierPaging, out var pagingError))
                return CommandResult.Fail(pagingError);

            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate && v.CanBePrinted)
                .ToArray();

            // GroupBy to safely handle duplicate view names across different view types
            var nameToView = views
                .GroupBy(v => v.Name)
                .ToDictionary(g => g.Key, g => g.First(), System.StringComparer.Ordinal);

            var analysis = ViewNamingAnalyzer.Analyze(nameToView.Keys, int.MaxValue);
            var outlierPage = ResponsePaging.Slice(analysis.Outliers, outlierPaging.StartIndex, outlierPaging.MaxResults);

            // Fill outlier IDs from the Revit view lookup
            var enrichedOutliers = outlierPage.Items.Select(o => new
            {
                id = nameToView.TryGetValue(o.Name, out var v) ? RevitCompat.GetId(v.Id) : 0L,
                name = o.Name,
                closest_pattern = o.ClosestPattern
            }).ToArray();

            return CommandResult.Ok(new
            {
                total_views = analysis.TotalViews,
                total_pattern_count = analysis.Patterns.Count,
                returned_pattern_count = System.Math.Min(analysis.Patterns.Count, maxPatterns),
                patterns_truncated = analysis.Patterns.Count > maxPatterns,
                patterns = analysis.Patterns.Take(maxPatterns).Select(p => new
                {
                    pattern = p.Pattern,
                    examples = p.Examples,
                    count = p.Count,
                    coverage = p.Coverage
                }).ToArray(),
                dominant = analysis.Dominant,
                total_outlier_count = outlierPage.TotalCount,
                start_outlier = outlierPage.StartIndex,
                returned_outlier_count = outlierPage.ReturnedCount,
                outliers_truncated = outlierPage.Truncated,
                next_outlier = outlierPage.NextIndex,
                outliers = enrichedOutliers
            });
        }
    }
}
