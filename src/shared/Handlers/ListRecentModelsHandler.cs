using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.UI;

namespace RvtMcp.Plugin.Handlers
{
    public class ListRecentModelsHandler : IRevitCommand
    {
        public string Name => "list_recent_models";
        public string Description => "List this Revit's recent models without opening one.";
        public string ParametersSchema => "{}";

        public const string AskUser =
            "Ask the user which path to open. Then call revit_open_model. Do not open a file before the user chooses.";

        public const string AskForPath =
            "No recent files were found. Ask the user for an absolute path on this machine, then call revit_open_model. Do not guess a path.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var year = AuthToken.RevitVersion;
            if (string.IsNullOrWhiteSpace(year))
                return CommandResult.Fail("Revit year is not set on this plugin.");

            string iniPath;
            try
            {
                iniPath = RecentFileList.IniPath(year);
            }
            catch (ArgumentException)
            {
                return CommandResult.Fail("Revit year on this plugin is not a 4-digit calendar year.");
            }

            if (!File.Exists(iniPath))
            {
                return CommandResult.Ok(new
                {
                    revit_year = year,
                    ini_found = false,
                    count = 0,
                    files = new object[0],
                    instruction = AskForPath
                });
            }

            string iniText;
            try
            {
                using (var stream = new FileStream(iniPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream))
                    iniText = reader.ReadToEnd();
            }
            catch (Exception)
            {
                return CommandResult.Fail("Could not read the Revit recent-file list for this Revit year.");
            }

            var files = RecentFileList.Parse(iniText)
                .Select(entry => new
                {
                    index = entry.Index,
                    path = entry.Path,
                    name = Path.GetFileName(entry.Path),
                    exists = File.Exists(entry.Path)
                })
                .ToArray();

            return CommandResult.Ok(new
            {
                revit_year = year,
                ini_found = true,
                count = files.Length,
                files,
                instruction = files.Length == 0 ? AskForPath : AskUser
            });
        }
    }
}
