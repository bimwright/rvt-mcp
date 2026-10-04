using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class WorkflowRoomDocumentationHandler : IRevitCommand
    {
        public string Name => "workflow_room_documentation";
        public string Description => "Generate room documentation records, optional callouts, room tags, finish schedule, and sheet placement.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""room_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } },
    ""level_name"": { ""type"": ""string"" },
    ""create_callouts"": { ""type"": ""boolean"", ""default"": true },
    ""create_finish_schedule"": { ""type"": ""boolean"", ""default"": true },
    ""tag_rooms"": { ""type"": ""boolean"", ""default"": true },
    ""sheet_id"": { ""type"": ""integer"" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""limit"": { ""type"": ""integer"", ""default"": 50, ""minimum"": 1, ""maximum"": 200 }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No active document is available.");

            JObject request;
            try
            {
                request = WorkflowSupport.ParseParams(paramsJson);
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            long[] roomIds;
            try
            {
                roomIds = WorkflowSupport.ReadLongArray(request, "room_ids");
            }
            catch (Exception ex)
            {
                return CommandResult.Fail(ex.Message);
            }

            var levelName = request.Value<string>("level_name");
            var createCallouts = request.Value<bool?>("create_callouts") ?? true;
            var createFinishSchedule = request.Value<bool?>("create_finish_schedule") ?? true;
            var tagRooms = request.Value<bool?>("tag_rooms") ?? true;
            var sheetId = request.Value<long?>("sheet_id");
            var dryRun = request.Value<bool?>("dry_run") ?? true;
            var limit = request.Value<int?>("limit") ?? 50;

            if (limit < 1 || limit > 200)
                return CommandResult.Fail("limit must be between 1 and 200.");
            if (sheetId.HasValue && !RevitCompat.CanRepresentElementId(sheetId.Value))
                return CommandResult.Fail("sheet_id " + RevitCompat.ElementIdRangeError(sheetId.Value));

            ViewSheet sheet = null;
            if (sheetId.HasValue)
            {
                sheet = doc.GetElement(RevitCompat.ToElementId(sheetId.Value)) as ViewSheet;
                if (sheet == null)
                    return CommandResult.Fail("sheet_id does not resolve to a ViewSheet: " + sheetId.Value.ToString(CultureInfo.InvariantCulture));
                if (sheet.IsPlaceholder)
                    return CommandResult.Fail("Cannot place documentation on a placeholder sheet.");
            }

            var rooms = ResolveRooms(doc, roomIds, levelName, limit, out var truncated, out var resolveWarnings);
            var warnings = new List<string>(resolveWarnings);
            if (truncated)
                warnings.Add("Room list was truncated at " + limit.ToString(CultureInfo.InvariantCulture) + ".");

            var steps = new JArray();
            var roomDocs = new JArray();
            var createdIds = new List<long>();
            var modifiedIds = new List<long>();

            foreach (var room in rooms)
                roomDocs.Add(BuildRoomPreview(room));

            steps.Add(WorkflowSupport.Step(
                "Collect Rooms",
                "list_rooms",
                "succeeded",
                "Resolve placed rooms by explicit IDs or level filter.",
                new { requested = roomIds == null ? 0 : roomIds.Length, returned = rooms.Count, truncated }));

            if (dryRun)
            {
                if (createCallouts)
                    steps.Add(WorkflowSupport.Step("Create Room Callouts", "ViewSection.CreateCallout", "skipped", "Dry-run: callouts would be created for placed rooms with a compatible active parent view.", new { rooms = rooms.Count }));
                if (createFinishSchedule)
                    steps.Add(WorkflowSupport.Step("Create Finish Schedule", "ViewSchedule.CreateSchedule", "skipped", "Dry-run: one room finish schedule would be created.", new { planned = true }));
                if (tagRooms)
                    steps.Add(WorkflowSupport.Step("Tag Rooms", "doc.Create.NewRoomTag", "skipped", "Dry-run: room tags would be created in the room documentation view.", new { rooms = rooms.Count }));

                return CommandResult.Ok(BuildResult(true, "succeeded", steps, createdIds, modifiedIds, warnings, roomDocs, WorkflowSupport.Rollback("TransactionGroup", false, "Dry-run; no write operations attempted.")));
            }

            var statusText = "succeeded";
            using (var group = new TransactionGroup(doc, "RvtMcp: workflow room documentation"))
            {
                group.Start();
                using (var tx = new Transaction(doc, "RvtMcp: room documentation"))
                {
                    tx.Start();
                    try
                    {
                        ViewSchedule finishSchedule = null;
                        if (createFinishSchedule)
                        {
                            try
                            {
                                finishSchedule = CreateFinishSchedule(doc);
                                createdIds.Add(RevitCompat.GetId(finishSchedule.Id));
                                steps.Add(WorkflowSupport.Step(
                                    "Create Finish Schedule",
                                    "ViewSchedule.CreateSchedule",
                                    "succeeded",
                                    "Create a room finish schedule with common finish fields.",
                                    new { schedule_id = RevitCompat.GetId(finishSchedule.Id), name = finishSchedule.Name }));
                            }
                            catch (Exception ex)
                            {
                                statusText = "partial";
                                warnings.Add("Finish schedule creation failed: " + ex.Message);
                                steps.Add(WorkflowSupport.Step("Create Finish Schedule", "ViewSchedule.CreateSchedule", "failed", "Create a room finish schedule with common finish fields.", null, ex.Message));
                            }
                        }

                        var parentView = doc.ActiveView;
                        ViewFamilyType calloutType = null;
                        if (createCallouts)
                            calloutType = FindCalloutType(doc);

                        var updatedDocs = new JArray();
                        foreach (var room in rooms)
                        {
                            using (var sub = new SubTransaction(doc))
                            {
                                sub.Start();
                                var roomReport = BuildRoomPreview(room);
                                var createdBeforeRoom = createdIds.Count;
                                try
                                {
                                    View calloutView = null;
                                    if (createCallouts)
                                    {
                                        calloutView = TryCreateCallout(doc, parentView, calloutType, room, roomReport);
                                        if (calloutView != null)
                                            createdIds.Add(RevitCompat.GetId(calloutView.Id));
                                    }

                                    if (tagRooms)
                                    {
                                        var tagView = calloutView ?? parentView;
                                        var tag = TryTagRoom(doc, room, tagView, roomReport);
                                        if (tag != null)
                                            createdIds.Add(RevitCompat.GetId(tag.Id));
                                    }

                                    if (sheet != null && calloutView != null && Viewport.CanAddViewToSheet(doc, sheet.Id, calloutView.Id))
                                    {
                                        var point = SheetPoint(updatedDocs.Count);
                                        var viewport = Viewport.Create(doc, sheet.Id, calloutView.Id, point);
                                        createdIds.Add(RevitCompat.GetId(viewport.Id));
                                        ((JObject)roomReport)["viewport_id"] = RevitCompat.GetId(viewport.Id);
                                    }

                                    if (sub.Commit() != TransactionStatus.Committed)
                                        throw new InvalidOperationException("Room documentation subtransaction did not commit.");
                                }
                                catch (Exception ex)
                                {
                                    statusText = "partial";
                                    if (sub.HasStarted())
                                        sub.RollBack();
                                    createdIds.RemoveRange(createdBeforeRoom, createdIds.Count - createdBeforeRoom);
                                    ((JObject)roomReport)["callout_view_id"] = JValue.CreateNull();
                                    ((JObject)roomReport)["tag_ids"] = new JArray();
                                    ((JObject)roomReport).Remove("viewport_id");
                                    ((JObject)roomReport)["status"] = "failed";
                                    ((JObject)roomReport)["warnings"] = new JArray(ex.Message);
                                    warnings.Add("Room " + RevitCompat.GetId(room.Id).ToString(CultureInfo.InvariantCulture) + " documentation failed: " + ex.Message);
                                }

                                updatedDocs.Add(roomReport);
                            }
                        }

                        roomDocs = updatedDocs;

                        if (sheet != null && finishSchedule != null)
                        {
                            try
                            {
                                var instance = ScheduleSheetInstance.Create(doc, sheet.Id, finishSchedule.Id, new XYZ(1.0, 0.5, 0));
                                createdIds.Add(RevitCompat.GetId(instance.Id));
                                steps.Add(WorkflowSupport.Step(
                                    "Place Finish Schedule On Sheet",
                                    "ScheduleSheetInstance.Create",
                                    "succeeded",
                                    "Place finish schedule on requested sheet.",
                                    new { schedule_instance_id = RevitCompat.GetId(instance.Id), sheet_id = RevitCompat.GetId(sheet.Id) }));
                            }
                            catch (Exception ex)
                            {
                                statusText = "partial";
                                warnings.Add("Finish schedule placement failed: " + ex.Message);
                                steps.Add(WorkflowSupport.Step("Place Finish Schedule On Sheet", "ScheduleSheetInstance.Create", "failed", "Place finish schedule on requested sheet.", null, ex.Message));
                            }
                        }

                        steps.Add(WorkflowSupport.Step(
                            "Document Rooms",
                            "create_callout_view/tag_elements/place_view_on_sheet",
                            statusText == "succeeded" ? "succeeded" : "partial",
                            "Create per-room callouts/tags and optional sheet placements.",
                            new { rooms = roomDocs.Count, created_ids = createdIds.Count }));

                        var status = tx.Commit();
                        if (status != TransactionStatus.Committed)
                            throw new InvalidOperationException("Transaction status: " + status);
                    }
                    catch (Exception ex)
                    {
                        if (tx.HasStarted())
                            tx.RollBack();
                        group.RollBack();
                        steps.Add(WorkflowSupport.Step("Commit Room Documentation", "Transaction", "failed", "Commit room documentation workflow.", null, ex.Message));
                        return CommandResult.Ok(BuildResult(false, "failed", steps, createdIds, modifiedIds, warnings, roomDocs, WorkflowSupport.Rollback("TransactionGroup", true, "Room documentation transaction failed and was rolled back.")));
                    }
                }
                if (group.Assimilate() != TransactionStatus.Committed)
                    return CommandResult.Fail("Revit did not commit the room documentation transaction group; model changes were not confirmed.");
            }

            return CommandResult.Ok(BuildResult(false, statusText, steps, createdIds, modifiedIds, warnings, roomDocs, WorkflowSupport.Rollback("TransactionGroup", false, "Room documentation transaction group committed.")));
        }

        private JObject BuildResult(
            bool dryRun,
            string status,
            JArray steps,
            IEnumerable<long> createdIds,
            IEnumerable<long> modifiedIds,
            IEnumerable<string> warnings,
            JArray roomDocs,
            JObject rollback)
        {
            var result = WorkflowSupport.Envelope(Name, dryRun, status, steps, createdIds, modifiedIds, warnings, rollback);
            result["room_documentation"] = roomDocs ?? new JArray();
            return result;
        }

        private static List<Room> ResolveRooms(Document doc, long[] roomIds, string levelName, int limit, out bool truncated, out List<string> warnings)
        {
            truncated = false;
            warnings = new List<string>();
            var rooms = new List<Room>();

            if (roomIds != null && roomIds.Length > 0)
            {
                foreach (var id in roomIds)
                {
                    var room = doc.GetElement(RevitCompat.ToElementId(id)) as Room;
                    if (room == null)
                    {
                        warnings.Add("Element id " + id.ToString(CultureInfo.InvariantCulture) + " is not a Room.");
                        continue;
                    }
                    if (!IsPlaced(room))
                    {
                        warnings.Add("Room " + id.ToString(CultureInfo.InvariantCulture) + " is unplaced or not enclosed and was skipped.");
                        continue;
                    }
                    if (rooms.Count >= limit)
                    {
                        truncated = true;
                        break;
                    }
                    rooms.Add(room);
                }
                return rooms;
            }

            foreach (var room in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().Cast<Room>())
            {
                if (rooms.Count >= limit)
                {
                    truncated = true;
                    break;
                }
                if (!string.IsNullOrWhiteSpace(levelName) && !string.Equals(room.Level?.Name, levelName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!IsPlaced(room))
                    continue;
                rooms.Add(room);
            }

            return rooms;
        }

        private static JObject BuildRoomPreview(Room room)
        {
            return new JObject
            {
                ["room_id"] = RevitCompat.GetId(room.Id),
                ["room_number"] = room.Number,
                ["room_name"] = room.Name,
                ["level_name"] = room.Level?.Name ?? string.Empty,
                ["status"] = "planned",
                ["callout_view_id"] = JValue.CreateNull(),
                ["schedule_id"] = JValue.CreateNull(),
                ["tag_ids"] = new JArray(),
                ["warnings"] = new JArray()
            };
        }

        private static ViewSchedule CreateFinishSchedule(Document doc)
        {
            var schedule = ViewSchedule.CreateSchedule(doc, RevitCompat.ToElementId((int)BuiltInCategory.OST_Rooms));
            schedule.Name = WorkflowSupport.UniqueName(
                doc,
                new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<Element>(),
                "RvtMcp Room Finish Schedule");

            foreach (var fieldName in new[] { "Number", "Name", "Level", "Base Finish", "Floor Finish", "Ceiling Finish", "Wall Finish" })
                TryAddScheduleField(schedule, fieldName);

            return schedule;
        }

        private static void TryAddScheduleField(ViewSchedule schedule, string fieldName)
        {
            try
            {
                var definition = schedule.Definition;
                var field = definition.GetSchedulableFields()
                    .FirstOrDefault(f => string.Equals(f.GetName(schedule.Document), fieldName, StringComparison.OrdinalIgnoreCase));
                if (field != null)
                    definition.AddField(field);
            }
            catch { }
        }

        private static ViewFamilyType FindCalloutType(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(vt => vt.ViewFamily == ViewFamily.Detail || vt.ViewFamily == ViewFamily.Section || vt.ViewFamily == ViewFamily.FloorPlan);
        }

        private static View TryCreateCallout(Document doc, View parentView, ViewFamilyType calloutType, Room room, JToken roomReport)
        {
            if (parentView == null || parentView.IsTemplate || parentView.ViewType == ViewType.DrawingSheet || parentView.ViewType == ViewType.Schedule)
            {
                AddRoomWarning(roomReport, "Active view cannot host callouts.");
                return null;
            }
            if (calloutType == null)
            {
                AddRoomWarning(roomReport, "No compatible callout ViewFamilyType found.");
                return null;
            }

            var bbox = room.get_BoundingBox(parentView) ?? room.get_BoundingBox(null);
            if (bbox == null)
            {
                AddRoomWarning(roomReport, "Room has no bounding box in the active view.");
                return null;
            }

            var pad = 1000.0 / WorkflowSupport.FeetToMm;
            var min = new XYZ(bbox.Min.X - pad, bbox.Min.Y - pad, bbox.Min.Z);
            var max = new XYZ(bbox.Max.X + pad, bbox.Max.Y + pad, bbox.Max.Z);
            var callout = ViewSection.CreateCallout(doc, parentView.Id, calloutType.Id, min, max);
            callout.Name = WorkflowSupport.UniqueName(
                doc,
                new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<Element>(),
                "Room " + room.Number + " - " + room.Name);
            ((JObject)roomReport)["callout_view_id"] = RevitCompat.GetId(callout.Id);
            ((JObject)roomReport)["status"] = "created";
            return callout;
        }

        private static RoomTag TryTagRoom(Document doc, Room room, View view, JToken roomReport)
        {
            if (view == null || view.IsTemplate || view.ViewType == ViewType.ThreeD || view.ViewType == ViewType.DrawingSheet || view.ViewType == ViewType.Schedule)
            {
                AddRoomWarning(roomReport, "Target view cannot host room tags.");
                return null;
            }

            var lp = room.Location as LocationPoint;
            if (lp == null)
            {
                AddRoomWarning(roomReport, "Room has no location point for tagging.");
                return null;
            }

            var tag = doc.Create.NewRoomTag(new LinkElementId(room.Id), new UV(lp.Point.X, lp.Point.Y), view.Id);
            ((JArray)((JObject)roomReport)["tag_ids"]).Add(RevitCompat.GetId(tag.Id));
            ((JObject)roomReport)["status"] = "created";
            return tag;
        }

        private static XYZ SheetPoint(int index)
        {
            var column = index % 2;
            var row = index / 2;
            return new XYZ(0.35 + column * 0.55, 0.75 - row * 0.25, 0);
        }

        private static bool IsPlaced(Room room)
        {
            try
            {
                return room.Location is LocationPoint && room.Area > 0.0001 && room.GetBoundarySegments(new SpatialElementBoundaryOptions()) != null;
            }
            catch
            {
                return false;
            }
        }

        private static void AddRoomWarning(JToken roomReport, string message)
        {
            var warnings = ((JObject)roomReport)["warnings"] as JArray;
            if (warnings == null)
            {
                warnings = new JArray();
                ((JObject)roomReport)["warnings"] = warnings;
            }
            warnings.Add(message);
        }
    }
}
