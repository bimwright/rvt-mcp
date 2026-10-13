using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;

namespace RvtMcp.Plugin.Handlers
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
            if (!(view is ViewPlan))
                return CommandResult.Fail("Room tags require a plan view.");

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
            var failed = new List<object>();
            var tagIds = new List<long>();
            using (var tx = new Transaction(doc, "MCP: Tag all rooms"))
            {
                tx.Start();
                foreach (var room in rooms)
                {
                    try
                    {
                        var point = room.Location as LocationPoint;
                        if (point == null) throw new InvalidOperationException("Room has no location point.");
                        var tag = doc.Create.NewRoomTag(new LinkElementId(room.Id), new UV(point.Point.X, point.Point.Y), view.Id);
                        tag.ChangeTypeId(tagType.Id);
                        tagIds.Add(RevitCompat.GetId(tag.Id));
                        tagged++;
                    }
                    catch (Exception ex) { failed.Add(new { roomId = RevitCompat.GetId(room.Id), error = ex.Message }); }
                }
                if (tagged == 0)
                {
                    tx.RollBack();
                    return new CommandResult { Success = false, Error = "No rooms could be tagged. See failed for individual errors.", Data = new { tagged, totalRooms = rooms.Count, failed } };
                }
                if (tx.Commit() != TransactionStatus.Committed)
                    return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
            }

            return CommandResult.Ok(new { tagged, totalRooms = rooms.Count, tagIds, failed });
        }
    }
}
