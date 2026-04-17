using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcp.Plugin.Handlers
{
    /// <summary>
    /// Returns a markdown summary of the current Revit project for MCP Prompt injection.
    /// Includes project info, active view, category counts, and MEP system names.
    /// </summary>
    public class GetModelOverviewHandler : IRevitCommand
    {
        public string Name => "get_model_overview";
        public string Description => "Get Revit project overview as markdown for prompt context.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No active document in Revit.");

            var sb = new StringBuilder();

            // ── Project Info ──
            sb.AppendLine("## Project");
            sb.AppendLine($"Name: {doc.Title}");
            sb.AppendLine($"Path: {doc.PathName ?? "(not saved)"}");

            var view = doc.ActiveView;
            if (view != null)
                sb.AppendLine($"Active View: {view.Name} ({view.ViewType})");

            sb.AppendLine();

            // ── Category Counts ──
            sb.AppendLine("## Element Categories");

            var stats = new Dictionary<string, int>();
            int total = 0;

            var collector = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType();

            foreach (var el in collector)
            {
                total++;
                var catName = el.Category?.Name ?? "Uncategorized";
                if (stats.ContainsKey(catName))
                    stats[catName]++;
                else
                    stats[catName] = 1;
            }

            sb.AppendLine($"Total: {total:N0} elements in {stats.Count} categories");
            sb.AppendLine();

            foreach (var kv in stats.OrderByDescending(x => x.Value).Take(30))
            {
                sb.AppendLine($"- {kv.Key}: {kv.Value:N0}");
            }

            if (stats.Count > 30)
                sb.AppendLine($"- ... and {stats.Count - 30} more categories");

            sb.AppendLine();

            // ── MEP Systems ──
            sb.AppendLine("## MEP Systems");

            try
            {
                var systemNames = new List<string>();

                // Piping Systems
                var pipingSystems = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_PipingSystem)
                    .WhereElementIsNotElementType()
                    .Cast<Element>()
                    .Select(e => e.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct()
                    .OrderBy(n => n);
                systemNames.AddRange(pipingSystems);

                // Duct Systems
                var ductSystems = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_DuctSystem)
                    .WhereElementIsNotElementType()
                    .Cast<Element>()
                    .Select(e => e.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct()
                    .OrderBy(n => n);
                systemNames.AddRange(ductSystems);

                if (systemNames.Count == 0)
                {
                    sb.AppendLine("No MEP systems found.");
                }
                else
                {
                    sb.AppendLine($"Total: {systemNames.Count} systems");
                    sb.AppendLine();
                    foreach (var name in systemNames.Take(30))
                    {
                        sb.AppendLine($"- {name}");
                    }
                    if (systemNames.Count > 30)
                        sb.AppendLine($"- ... and {systemNames.Count - 30} more systems");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"MEP systems: unavailable ({ex.Message})");
            }

            return CommandResult.Ok(new { markdown = sb.ToString() });
        }
    }
}
