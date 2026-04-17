using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcp.Plugin.Handlers
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

            int tagged = 0;
            using (var tx = new Transaction(doc, "MCP: Tag all walls"))
            {
                tx.Start();
                foreach (var wall in walls)
                {
                    try
                    {
                        var location = wall.Location as LocationCurve;
                        if (location == null) continue;

                        var midpoint = location.Curve.Evaluate(0.5, true);
                        var tagRef = new Reference(wall);
                        IndependentTag.Create(doc, view.Id, tagRef, false, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, midpoint);
                        tagged++;
                    }
                    catch { }
                }
                tx.Commit();
            }

            return CommandResult.Ok(new { tagged, totalWalls = walls.Count });
        }
    }
}
