using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Run through revit_send_code_to_revit in an installed Revit host with a sample project.
// All temporary views and tags are rolled back; no Save/Sync or approval changes.
public class McpDynamicScript
{
    static readonly List<object> Checks = new List<object>();
    static long Id(ElementId id) => Convert.ToInt64((typeof(ElementId).GetProperty("Value")
        ?? typeof(ElementId).GetProperty("IntegerValue")).GetValue(id));
    static void Check(string name, bool pass, object actual) => Checks.Add(new { name, pass, actual });
    static JObject Data(object result) => JObject.FromObject(result);

    public static object Run(UIApplication app)
    {
        Checks.Clear();
        var doc = app.ActiveUIDocument.Document;
        var originalView = doc.ActiveView.Id;
        var before = new { count = new FilteredElementCollector(doc).GetElementCount(), modified = doc.IsModified,
            warnings = doc.GetWarnings().Count };
        var room = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType().Cast<Room>().First(r => r.Area > 0 && r.Location is LocationPoint);
        var tagType = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_RoomTags).Cast<FamilySymbol>().First();
        var handler = new RvtMcp.Plugin.Handlers.TagAllByCategoryHandler();
        using (var group = new TransactionGroup(doc, "Native room-category tag regression"))
        {
            group.Start();
            try
            {
                ViewPlan view;
                using (var tx = new Transaction(doc, "Room category view fixture"))
                {
                    tx.Start();
                    var family = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
                        .Cast<ViewFamilyType>().First(t => t.ViewFamily == ViewFamily.FloorPlan);
                    view = ViewPlan.Create(doc, family.Id, room.LevelId);
                    view.Name = "NATIVE_ROOM_CATEGORY_TAG_FIXTURE";
                    view.CropBoxActive = false;
                    view.get_Parameter(BuiltInParameter.VIEW_PHASE).Set(room.get_Parameter(BuiltInParameter.ROOM_PHASE).AsElementId());
                    if (tx.Commit() != TransactionStatus.Committed) throw new Exception("View fixture did not commit.");
                }
                Func<bool, bool, object> request = (dry, skip) => new { category = "Rooms", view_id = Id(view.Id),
                    tag_type_id = Id(tagType.Id), leader = true, dry_run = dry, skip_existing = skip };
                Func<int> nativeCount = () => new FilteredElementCollector(doc, view.Id)
                    .OfCategory(BuiltInCategory.OST_RoomTags).WhereElementIsNotElementType().GetElementCount();
                var dry = handler.Execute(app, JsonConvert.SerializeObject(request(true, true)));
                var preview = Data(dry.Data);
                Check("dry_run_reports_no_actual_creation", dry.Success && preview.Value<int>("created") == 0
                    && preview.Value<int>("candidate_count") > 0 && preview.Value<int>("would_create") == preview.Value<int>("candidate_count")
                    && nativeCount() == 0, preview);
                var first = handler.Execute(app, JsonConvert.SerializeObject(request(false, true)));
                var created = Data(first.Data);
                var expected = created.Value<int>("candidate_count");
                Check("category_rooms_create_native_tags", first.Success && expected > 0
                    && created.Value<int>("created") == expected && created["failed"].Count() == 0 && nativeCount() == expected, created);
                var tags = new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_RoomTags)
                    .WhereElementIsNotElementType().Cast<RoomTag>().ToArray();
                Check("native_tag_type_view_room_and_leader", tags.Length == expected && tags.All(t => t.Room != null
                    && t.OwnerViewId == view.Id && t.GetTypeId() == tagType.Id && t.HasLeader), new { count = tags.Length });
                var again = handler.Execute(app, JsonConvert.SerializeObject(request(false, true)));
                var repeated = Data(again.Data);
                Check("skip_existing_recognizes_room_tags", again.Success && repeated.Value<int>("created") == 0
                    && repeated.Value<int>("already_tagged") == expected && nativeCount() == expected, repeated);
                var duplicate = handler.Execute(app, JsonConvert.SerializeObject(new { category = "Rooms", view_id = Id(view.Id),
                    tag_type_id = Id(tagType.Id), skip_existing = false, limit = 1 }));
                Check("explicit_duplicate_option_is_preserved", duplicate.Success && Data(duplicate.Data).Value<int>("created") == 1
                    && nativeCount() == expected + 1, duplicate.Data);
                var wrongType = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                    .First(t => Id(t.Category.Id) != (long)BuiltInCategory.OST_RoomTags);
                var invalid = handler.Execute(app, JsonConvert.SerializeObject(new { category = "Rooms", view_id = Id(view.Id), tag_type_id = Id(wrongType.Id) }));
                Check("wrong_room_tag_type_is_rejected", !invalid.Success && invalid.Error.Contains("Room Tags"), invalid.Error);
                var sheet = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().First();
                var wrongView = handler.Execute(app, JsonConvert.SerializeObject(new { category = "Rooms", view_id = Id(sheet.Id), dry_run = true }));
                Check("room_tags_require_plan_even_for_preview", !wrongView.Success && wrongView.Error.Contains("plan"), wrongView.Error);
            }
            finally { Check("fixture_rollback", group.RollBack() == TransactionStatus.RolledBack, null); }
        }
        var after = new { count = new FilteredElementCollector(doc).GetElementCount(), modified = doc.IsModified,
            warnings = doc.GetWarnings().Count };
        Check("native_state_unchanged", before.count == after.count && before.modified == after.modified
            && before.warnings == after.warnings && doc.ActiveView.Id == originalView, new { before, after });
        var assembly = typeof(RvtMcp.Plugin.SafeFailuresPreprocessor).Assembly;
        return new { year = app.Application.VersionNumber, checks = Checks,
            informational = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                .Cast<AssemblyInformationalVersionAttribute>().Single().InformationalVersion };
    }
}
