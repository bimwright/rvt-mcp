using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateFoundationWallHandler : IRevitCommand
    {
        public string Name => "create_foundation_wall";
        public string Description => "Create a wall foundation under an existing wall using WallFoundation.Create.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""wall_id"":{""type"":""integer""},""foundation_type_id"":{""type"":""integer""},""foundation_type_name"":{""type"":""string""}},""required"":[""wall_id""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var wallId = req.Value<long?>("wall_id");
            if (!wallId.HasValue) return CommandResult.Fail("wall_id is required.");

            var wallElemId = RevitCompat.ToElementId(wallId.Value);
            var wall = doc.GetElement(wallElemId) as Wall;
            if (wall == null) return CommandResult.Fail($"wall_id {wallId} is not a Wall element.");

            var footingType = ResolveFootingType(doc, req.Value<long?>("foundation_type_id"), req.Value<string>("foundation_type_name"));
            if (footingType == null)
                return CommandResult.Fail("Could not resolve WallFoundation type. Provide foundation_type_id or foundation_type_name.");

            using (var tx = new Transaction(doc, "RvtMcp: Create wall foundation"))
            {
                tx.Start();
                try
                {
                    var wf = WallFoundation.Create(doc, footingType.Id, wallElemId);
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
                    return CommandResult.Ok(new
                    {
                        created_id = RevitCompat.GetId(wf.Id),
                        wall_id = wallId.Value,
                        foundation_type_id = RevitCompat.GetId(footingType.Id)
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to create wall foundation: {ex.Message}");
                }
            }
        }

        private static WallFoundationType ResolveFootingType(Document doc, long? typeId, string typeName)
        {
            if (typeId.HasValue)
                return doc.GetElement(RevitCompat.ToElementId(typeId.Value)) as WallFoundationType;
            var types = new FilteredElementCollector(doc).OfClass(typeof(WallFoundationType)).Cast<WallFoundationType>();
            if (!string.IsNullOrWhiteSpace(typeName))
                return types.FirstOrDefault(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
            return types.FirstOrDefault();
        }
    }
}
