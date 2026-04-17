using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace Bimwright.Rvt.Plugin.Handlers
{
    public class CreateRoomHandler : IRevitCommand
    {
        public string Name => "create_room";
        public string Description => "Create and place a room at a specified location";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""x"":{""type"":""number""},""y"":{""type"":""number""},""level"":{""type"":""string""},""name"":{""type"":""string""},""number"":{""type"":""string""}},""required"":[""x"",""y""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var request = JObject.Parse(paramsJson);
            var x = request.Value<double>("x");
            var y = request.Value<double>("y");
            var levelName = request.Value<string>("level");
            var roomName = request.Value<string>("name");
            var roomNumber = request.Value<string>("number");

            var point = new UV(x / 304.8, y / 304.8);

            // Find level
            var level = new FilteredElementCollector(doc).OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault(lv => !string.IsNullOrEmpty(levelName)
                    ? lv.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase)
                    : true);

            if (level == null)
                return CommandResult.Fail("No level found.");

            // Find a floor plan view for this level
            var planView = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .FirstOrDefault(v => v.GenLevel?.Id == level.Id && !v.IsTemplate);

            if (planView == null)
                return CommandResult.Fail($"No floor plan view found for level '{level.Name}'.");

            using (var tx = new Transaction(doc, "MCP: Create Room"))
            {
                tx.Start();
                try
                {
#if REVIT2022
                    var room = doc.Create.NewRoom(level, point);
#else
                    // R23+: NewRoom(Level, UV) was removed. Use NewRoom(Phase) + manual placement.
                    var phase = doc.Phases.get_Item(doc.Phases.Size - 1);
                    var room = doc.Create.NewRoom(phase);
                    room.Location.Move(new Autodesk.Revit.DB.XYZ(point.U, point.V, level.Elevation));
#endif
                    if (!string.IsNullOrEmpty(roomName))
                        room.Name = roomName;
                    if (!string.IsNullOrEmpty(roomNumber))
                        room.Number = roomNumber;

                    tx.Commit();
                    return CommandResult.Ok(new
                    {
                        elementId = RevitCompat.GetId(room.Id),
                        name = room.Name,
                        number = room.Number,
                        level = level.Name
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to create room: {ex.Message}");
                }
            }
        }
    }
}
