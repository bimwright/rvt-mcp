using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcp.Plugin.Handlers
{
    public class AnalyzeModelStatisticsHandler : IRevitCommand
    {
        public string Name => "analyze_model_statistics";
        public string Description => "Analyze model complexity with element counts by category";
        public string ParametersSchema => "{}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var collector = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType();

            var stats = new Dictionary<string, int>();
            int total = 0;

            foreach (var el in collector)
            {
                total++;
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
                totalElements = total,
                totalCategories = categories.Length,
                categories
            });
        }
    }
}
