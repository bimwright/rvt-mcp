using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;

namespace Bimwright.Rvt.Plugin.Handlers
{
    public class TagAllRoomsHandler : IRevitCommand
    {
        public string Name => "tag_all_rooms";
        public string Description => "Tag all rooms in the current view";
        public string ParametersSchema => "{}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var view = doc.ActiveView;
            if (view == null)
                return CommandResult.Fail("No active view.");

            var rooms = new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(r => r.Area > 0)
                .ToList();

            if (rooms.Count == 0)
                return CommandResult.Ok(new { tagged = 0, message = "No rooms found in current view." });

            var tagType = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_RoomTags)
                .FirstElement() as FamilySymbol;

            if (tagType == null)
                return CommandResult.Fail("No room tag family loaded in the project.");

            int tagged = 0;
            using (var tx = new Transaction(doc, "MCP: Tag all rooms"))
            {
                tx.Start();
                foreach (var room in rooms)
                {
                    try
                    {
                        var point = room.Location as LocationPoint;
                        if (point == null) continue;

                        var tagRef = new Reference(room);
                        IndependentTag.Create(doc, view.Id, tagRef, false, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, point.Point);
                        tagged++;
                    }
                    catch { }
                }
                tx.Commit();
            }

            return CommandResult.Ok(new { tagged, totalRooms = rooms.Count });
        }
    }
}
