using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitMcp.Plugin.Handlers
{
    public class CreateLevelHandler : IRevitCommand
    {
        public string Name => "create_level";
        public string Description => "Create a level at a specified elevation";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""elevation"":{""type"":""number"",""description"":""Elevation in mm""},""name"":{""type"":""string""}},""required"":[""elevation""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var elevation = request.Value<double>("elevation"); // mm
            var name = request.Value<string>("name");

            using (var tx = new Transaction(doc, "MCP: Create Level"))
            {
                tx.Start();
                try
                {
                    var level = Level.Create(doc, elevation / 304.8);
                    if (!string.IsNullOrEmpty(name))
                        level.Name = name;

                    tx.Commit();
                    return CommandResult.Ok(new
                    {
                        elementId = RevitCompat.GetId(level.Id),
                        name = level.Name,
                        elevationMm = Math.Round(level.Elevation * 304.8, 1)
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to create level: {ex.Message}");
                }
            }
        }
    }
}
