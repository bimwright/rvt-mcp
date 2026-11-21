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
            var sheetError = TagScan.SheetRefusal(doc, view);
            if (sheetError != null)
                return CommandResult.Fail(sheetError);

            var walls = new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType()
                .ToList();

            if (walls.Count == 0)
                return CommandResult.Ok(new { tagged = 0, message = "No walls found in current view." });

            // Find wall tag type
            var tagType = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_WallTags)
                .FirstElement() as FamilySymbol;

            if (tagType == null)
                return CommandResult.Fail("No wall tag family loaded in the project.");

            HashSet<long> alreadyTagged;
            try
            {
                // A wall tag or a multi-category tag labels the wall; a keynote or material tag does not.
                alreadyTagged = TagScan.CollectTaggedElementIds(doc, view, BuiltInCategory.OST_WallTags, BuiltInCategory.OST_MultiCategoryTags);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Failed to read existing tags in view: " + ex.Message);
            }

            var notTagged = walls.Where(w => !alreadyTagged.Contains(RevitCompat.GetId(w.Id))).ToList();
            var alreadyTaggedCount = walls.Count - notTagged.Count;
            // A wall without a location curve (an in-place wall, for example) has no midpoint to tag:
            // it is skipped on every call, not reported as a failure.
            var skipped = notTagged.Where(w => !(w.Location is LocationCurve)).Select(w => RevitCompat.GetId(w.Id)).ToList();
            var untagged = notTagged.Where(w => w.Location is LocationCurve).ToList();
            if (untagged.Count == 0)
                return CommandResult.Ok(new { tagged = 0, totalWalls = walls.Count, alreadyTagged = alreadyTaggedCount, skippedNoLocationCurve = skipped,
                    message = "All walls in current view with a location curve are already tagged." });

            int tagged = 0;
            var tagIds = new List<long>();
            var failed = new List<object>();
            using (var tx = new Transaction(doc, "MCP: Tag all walls"))
            {
                tx.Start();
                foreach (var wall in untagged)
                {
                    try
                    {
                        var location = (LocationCurve)wall.Location;
                        var midpoint = location.Curve.Evaluate(0.5, true);
                        var tag = IndependentTag.Create(doc, view.Id, new Reference(wall), false, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, midpoint);
                        if (tag == null) throw new InvalidOperationException("IndependentTag.Create returned null.");
                        tagIds.Add(RevitCompat.GetId(tag.Id));
                        tagged++;
                    }
                    catch (Exception ex) { failed.Add(new { wallId = RevitCompat.GetId(wall.Id), error = ex.Message }); }
                }
                if (tagged == 0)
                {
                    tx.RollBack();
                    return new CommandResult { Success = false, Error = "No walls could be tagged. See failed for individual errors.",
                        Data = new { tagged, totalWalls = walls.Count, alreadyTagged = alreadyTaggedCount, skippedNoLocationCurve = skipped, failed, failedCount = failed.Count } };
                }
                if (tx.Commit() != TransactionStatus.Committed)
                    return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
            }

            return CommandResult.Ok(new { tagged, totalWalls = walls.Count, alreadyTagged = alreadyTaggedCount, skippedNoLocationCurve = skipped, tagIds, failed, failedCount = failed.Count });
        }
    }
}
