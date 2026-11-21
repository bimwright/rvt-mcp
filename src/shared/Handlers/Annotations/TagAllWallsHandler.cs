using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RvtMcp.Plugin.Handlers
{
    public class TagAllWallsHandler : IRevitCommand
    {
        public string Name => "tag_all_walls";
        public string Description => "Tag all walls in the current view";
        public string ParametersSchema => "{}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            var view = doc.ActiveView;
            if (view == null)
                return CommandResult.Fail("No active view.");
            if (view is ViewSheet sheet)
                return CommandResult.Fail(SheetTagGuard.Error(doc, sheet));

            var walls = new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType()
                .ToList();

            if (walls.Count == 0)
                return CommandResult.Ok(new { tagged = 0, message = "No walls found in current view." });

            // Keynotes and material tags are IndependentTag too; they do not make a wall tagged.
            var alreadyTagged = new HashSet<long>();
            foreach (var tag in new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(IndependentTag))
                .Cast<IndependentTag>()
                .Where(t => t.Category != null
                    && (RevitCompat.GetId(t.Category.Id) == (long)BuiltInCategory.OST_WallTags
                        || RevitCompat.GetId(t.Category.Id) == (long)BuiltInCategory.OST_MultiCategoryTags)))
            {
                foreach (var id in tag.GetTaggedLocalElementIds())
                    alreadyTagged.Add(RevitCompat.GetId(id));
            }
            var untaggedWalls = walls.Where(w => !alreadyTagged.Contains(RevitCompat.GetId(w.Id))).ToList();
            if (untaggedWalls.Count == 0)
                return CommandResult.Ok(new { tagged = 0, totalWalls = walls.Count, message = "All walls in current view are already tagged." });

            // Find wall tag type
            var tagType = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_WallTags)
                .FirstElement() as FamilySymbol;

            if (tagType == null)
                return CommandResult.Fail("No wall tag family loaded in the project.");

            int tagged = 0;
            var failed = new List<object>();
            var tagIds = new List<long>();
            using (var tx = new Transaction(doc, "MCP: Tag all walls"))
            {
                tx.Start();
                foreach (var wall in untaggedWalls)
                {
                    try
                    {
                        var location = wall.Location as LocationCurve;
                        if (location == null) throw new InvalidOperationException("Wall has no location curve.");

                        var midpoint = location.Curve.Evaluate(0.5, true);
                        var tag = IndependentTag.Create(doc, view.Id, new Reference(wall), false, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, midpoint);
                        tagIds.Add(RevitCompat.GetId(tag.Id));
                        tagged++;
                    }
                    catch (Exception ex) { failed.Add(new { wallId = RevitCompat.GetId(wall.Id), error = ex.Message }); }
                }
                if (tagged == 0)
                {
                    tx.RollBack();
                    return new CommandResult { Success = false, Error = "No walls could be tagged. See failed for individual errors.", Data = new { tagged, totalWalls = walls.Count, failed } };
                }
                if (tx.Commit() != TransactionStatus.Committed)
                    return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
            }

            return CommandResult.Ok(new { tagged, totalWalls = walls.Count, tagIds, failed, failedCount = failed.Count });
        }
    }
}
