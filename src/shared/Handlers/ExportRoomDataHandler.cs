using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;

namespace Bimwright.Rvt.Plugin.Handlers
{
    public class ExportRoomDataHandler : IRevitCommand
    {
        public string Name => "export_room_data";
        public string Description => "Export all room data from the project";
        public string ParametersSchema => "{}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(r => r.Area > 0)
                .Select(r => new
                {
                    elementId = RevitCompat.GetId(r.Id),
                    name = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? r.Name,
                    number = r.Number,
                    level = r.Level?.Name,
                    areaMsq = System.Math.Round(r.Area * 0.09290304, 2),
                    perimeterM = System.Math.Round(r.Perimeter * 0.3048, 2),
                    department = r.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString(),
                    volume = System.Math.Round(r.Volume * 0.0283168, 4)
                })
                .OrderBy(r => r.level)
                .ThenBy(r => r.number)
                .ToArray();

            return CommandResult.Ok(new
            {
                projectName = doc.Title,
                totalRooms = rooms.Length,
                totalAreaMsq = System.Math.Round(rooms.Sum(r => r.areaMsq), 2),
                rooms
            });
        }
    }
}
