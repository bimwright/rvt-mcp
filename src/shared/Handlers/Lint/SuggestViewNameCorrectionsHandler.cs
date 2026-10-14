using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RvtMcp.Plugin.Lint;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class SuggestViewNameCorrectionsHandler : IRevitCommand
    {
        private const int SuggestionCap = 50;

        public string Name => "suggest_view_name_corrections";
        public string Description => "Propose corrected names for view outliers. Optional profile arg uses firm-profile library; omitted = use project-inferred pattern.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""profile"":{""type"":""string"",""description"":""Optional firm-profile id from library. Omit to use project-inferred pattern.""}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            string profileId = null;
            if (!string.IsNullOrWhiteSpace(paramsJson))
            {
                var req = JObject.Parse(paramsJson);
                profileId = req.Value<string>("profile");
            }

            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate && v.CanBePrinted)
                .ToArray();

            var nameToView = views.GroupBy(v => v.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var analysis = ViewNamingAnalyzer.Analyze(nameToView.Keys);

            string source;
            string targetPattern;

            if (!string.IsNullOrEmpty(profileId))
            {
                var library = FirmProfileLibrary.LoadFrom(GetLibraryFolders());
                var profile = library.Profiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));
                if (profile == null)
                {
                    return CommandResult.Ok(new
                    {
                        error = $"Profile '{profileId}' not in library.",
                        available = library.Profiles.Select(p => p.Id).ToArray(),
                        hint = "Call without 'profile' param to use project-inferred pattern."
                    });
                }
                targetPattern = profile.Rules?.ViewName;
                source = $"profile:{profile.Id}";
            }
            else
            {
                targetPattern = analysis.Dominant;
                source = "inferred";
            }

            var suggestions = new List<object>();
            if (!string.IsNullOrEmpty(targetPattern))
            {
                foreach (var outlier in analysis.Outliers.Take(SuggestionCap))
                {
                    if (!nameToView.TryGetValue(outlier.Name, out var view)) continue;
                    var suggested = BuildSuggestion(outlier.Name, targetPattern);
                    suggestions.Add(new
                    {
                        id = RevitCompat.GetId(view.Id),
                        current = outlier.Name,
                        suggested,
                        reason = source == "inferred"
                            ? $"matches dominant {targetPattern}"
                            : $"matches profile rule {targetPattern}"
                    });
                }
            }

            return CommandResult.Ok(new
            {
                source,
                suggestions = suggestions.ToArray()
            });
        }

        private static string BuildSuggestion(string currentName, string pattern)
        {
            // Minimal v0.2.1: return the pattern as-is with the current name slotted in {Name}.
            // A smarter substitution can come later once we have real user feedback.
            if (pattern.Contains("{Name}"))
                return pattern.Replace("{Name}", currentName);
            return pattern;
        }

        private static IEnumerable<string> GetLibraryFolders()
        {
            // Shipped folder: alongside the plugin DLL
            var pluginDir = Path.GetDirectoryName(typeof(SuggestViewNameCorrectionsHandler).Assembly.Location);
            if (!string.IsNullOrEmpty(pluginDir))
                yield return Path.Combine(pluginDir, "firm-profiles");

            // User folder: %LOCALAPPDATA%\Bimwright\rvt-mcp\firm-profiles
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localApp))
                yield return Path.Combine(localApp, "Bimwright", "rvt-mcp", "firm-profiles");
        }
    }
}
