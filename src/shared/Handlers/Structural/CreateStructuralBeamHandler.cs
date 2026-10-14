using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateStructuralBeamHandler : IRevitCommand
    {
        public string Name => "create_structural_beam";
        public string Description => "Create a structural beam between two points. Resolves FamilySymbol via type_id or type_name (StructuralFraming category). Returns created_id.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""type_id"":{""type"":""integer""},""type_name"":{""type"":""string""},""start_x_mm"":{""type"":""number""},""start_y_mm"":{""type"":""number""},""start_z_mm"":{""type"":""number""},""end_x_mm"":{""type"":""number""},""end_y_mm"":{""type"":""number""},""end_z_mm"":{""type"":""number""},""level_id"":{""type"":""integer""},""level_name"":{""type"":""string""},""usage"":{""type"":""string"",""enum"":[""beam"",""brace"",""joist""]}},""required"":[""start_x_mm"",""start_y_mm"",""end_x_mm"",""end_y_mm""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var typeId = req.Value<long?>("type_id");
            var typeName = req.Value<string>("type_name");
            var sx = req.Value<double?>("start_x_mm") ?? 0;
            var sy = req.Value<double?>("start_y_mm") ?? 0;
            var sz = req.Value<double?>("start_z_mm") ?? 0;
            var ex = req.Value<double?>("end_x_mm") ?? 0;
            var ey = req.Value<double?>("end_y_mm") ?? 0;
            var ez = req.Value<double?>("end_z_mm") ?? 0;
            var levelId = req.Value<long?>("level_id");
            var levelName = req.Value<string>("level_name");
            var usage = (req.Value<string>("usage") ?? "beam").ToLowerInvariant();

            var symbol = ResolveSymbol(doc, typeId, typeName);
            if (symbol == null)
                return CommandResult.Fail("Could not resolve structural framing FamilySymbol. Provide type_id or type_name.");

            var level = ResolveLevel(doc, levelId, levelName);
            if (level == null)
                return CommandResult.Fail("Could not resolve Level.");

            var start = new XYZ(sx / 304.8, sy / 304.8, sz / 304.8);
            var end = new XYZ(ex / 304.8, ey / 304.8, ez / 304.8);
            if (start.DistanceTo(end) < 1e-6)
                return CommandResult.Fail("start and end points are identical.");

            var line = Line.CreateBound(start, end);

            var structType = usage switch
            {
                "brace" => StructuralType.Brace,
                "joist" => StructuralType.Beam, // R22-R27 has no Joist; Beam + joist family is common
                _ => StructuralType.Beam
            };

            using (var tx = new Transaction(doc, "RvtMcp: Create structural beam"))
            {
                tx.Start();
                try
                {
                    if (!symbol.IsActive) symbol.Activate();
                    doc.Regenerate();

                    var inst = doc.Create.NewFamilyInstance(line, symbol, level, structType);

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
                    return CommandResult.Ok(new
                    {
                        created_id = RevitCompat.GetId(inst.Id),
                        type_id = RevitCompat.GetId(symbol.Id),
                        level_id = RevitCompat.GetId(level.Id),
                        usage,
                        structural_type = structType.ToString()
                    });
                }
                catch (Exception ex_)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to create structural beam: {ex_.Message}");
                }
            }
        }

        private static FamilySymbol ResolveSymbol(Document doc, long? typeId, string typeName)
        {
            if (typeId.HasValue)
                return doc.GetElement(RevitCompat.ToElementId(typeId.Value)) as FamilySymbol;

            var query = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_StructuralFraming)
                .Cast<FamilySymbol>();
            if (!string.IsNullOrWhiteSpace(typeName))
                return query.FirstOrDefault(s =>
                    s.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
                    $"{s.Family.Name}: {s.Name}".Equals(typeName, StringComparison.OrdinalIgnoreCase));
            return query.FirstOrDefault();
        }

        private static Level ResolveLevel(Document doc, long? levelId, string levelName)
        {
            if (levelId.HasValue)
                return doc.GetElement(RevitCompat.ToElementId(levelId.Value)) as Level;
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>();
            if (!string.IsNullOrWhiteSpace(levelName))
                return levels.FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
            return levels.OrderBy(l => l.Elevation).FirstOrDefault();
        }
    }
}
