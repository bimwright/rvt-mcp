using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RvtMcp.Plugin.Lint;

namespace RvtMcp.Plugin.Handlers
{
    public class DetectFirmProfileHandler : IRevitCommand
    {
        public string Name => "detect_firm_profile";
        public string Description => "Fingerprint project naming patterns and match against firm-profile library. Always returns project_pattern; library_match is null when library empty.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            // 1. View-name dominant
            var viewNames = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate && v.CanBePrinted)
                .Select(v => v.Name)
                .ToArray();
            var analysis = ViewNamingAnalyzer.Analyze(viewNames);

            // 2. Sheet prefix (most common leading alpha block across SheetNumber)
            var sheetNumbers = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Select(s => s.SheetNumber)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToArray();
            var sheetPrefix = InferPrefix(sheetNumbers);

            // 3. Level pattern
            var levelNames = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .Select(l => l.Name)
                .ToArray();
            var levelAnalysis = ViewNamingAnalyzer.Analyze(levelNames);

            var evidence = new FirmMatchEvidence
            {
                SheetPrefix = sheetPrefix,
                ViewDominant = analysis.Dominant,
                LevelPattern = levelAnalysis.Dominant
            };

            var library = FirmProfileLibrary.LoadFrom(GetLibraryFolders());
            var match = library.Match(evidence);

            return CommandResult.Ok(new
            {
                project_pattern = new
                {
                    view_dominant = analysis.Dominant,
                    sheet_prefix = sheetPrefix,
                    level_pattern = levelAnalysis.Dominant
                },
                library_match = match == null ? null : (object)new
                {
                    profile_id = match.ProfileId,
                    confidence = match.Confidence,
                    matched_hints = match.MatchedHints.ToArray()
                }
            });
        }

        private static string InferPrefix(IEnumerable<string> sheetNumbers)
        {
            var prefixes = sheetNumbers
                .Select(s =>
                {
                    // Leading alpha block (ABC-001 → "ABC")
                    int i = 0;
                    while (i < s.Length && char.IsLetter(s[i])) i++;
                    return i > 0 ? s.Substring(0, i) : null;
                })
                .Where(p => !string.IsNullOrEmpty(p))
                .GroupBy(p => p)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();
            return prefixes?.Key;
        }

        private static IEnumerable<string> GetLibraryFolders()
        {
            var pluginDir = Path.GetDirectoryName(typeof(DetectFirmProfileHandler).Assembly.Location);
            if (!string.IsNullOrEmpty(pluginDir))
                yield return Path.Combine(pluginDir, "firm-profiles");

            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localApp))
                yield return Path.Combine(localApp, "Bimwright", "rvt-mcp", "firm-profiles");
        }
    }
}
