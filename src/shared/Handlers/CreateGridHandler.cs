using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace Bimwright.Plugin.Handlers
{
    public class CreateGridHandler : IRevitCommand
    {
        public string Name => "create_grid";
        public string Description => "Create a grid line from start/end points";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""startX"":{""type"":""number""},""startY"":{""type"":""number""},""endX"":{""type"":""number""},""endY"":{""type"":""number""},""name"":{""type"":""string""}},""required"":[""startX"",""startY"",""endX"",""endY""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var startX = request.Value<double>("startX");
            var startY = request.Value<double>("startY");
            var endX = request.Value<double>("endX");
            var endY = request.Value<double>("endY");
            var name = request.Value<string>("name");

            var line = Line.CreateBound(
                new XYZ(startX / 304.8, startY / 304.8, 0),
                new XYZ(endX / 304.8, endY / 304.8, 0));

            using (var tx = new Transaction(doc, "MCP: Create Grid"))
            {
                tx.Start();
                try
                {
                    var grid = Grid.Create(doc, line);
                    if (!string.IsNullOrEmpty(name))
                        grid.Name = name;

                    tx.Commit();
                    return CommandResult.Ok(new
                    {
                        elementId = RevitCompat.GetId(grid.Id),
                        name = grid.Name
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to create grid: {ex.Message}");
                }
            }
        }
    }
}
