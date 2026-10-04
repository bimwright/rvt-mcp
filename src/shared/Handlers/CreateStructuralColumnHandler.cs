using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateStructuralColumnHandler : IRevitCommand
    {
        public string Name => "create_structural_column";
        public string Description => "Create a structural column at a point. Resolves FamilySymbol via type_id or type_name (structural column category). Returns created_id, type_id, level_id.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""type_id"":{""type"":""integer""},""type_name"":{""type"":""string""},""x_mm"":{""type"":""number""},""y_mm"":{""type"":""number""},""z_mm"":{""type"":""number""},""level_id"":{""type"":""integer""},""level_name"":{""type"":""string""},""height_mm"":{""type"":""number""},""rotation_deg"":{""type"":""number""}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var typeId = req.Value<long?>("type_id");
            var typeName = req.Value<string>("type_name");
            var xMm = req.Value<double?>("x_mm") ?? 0;
            var yMm = req.Value<double?>("y_mm") ?? 0;
            var zMm = req.Value<double?>("z_mm") ?? 0;
            var levelId = req.Value<long?>("level_id");
            var levelName = req.Value<string>("level_name");
            var heightMm = req.Value<double?>("height_mm");
            var rotationDeg = req.Value<double?>("rotation_deg") ?? 0;

            // Resolve FamilySymbol (structural columns)
            var symbol = ResolveSymbol(doc, typeId, typeName);
            if (symbol == null)
                return CommandResult.Fail("Could not resolve structural column FamilySymbol. Provide type_id or type_name.");

            // Resolve Level
            var level = ResolveLevel(doc, levelId, levelName);
            if (level == null)
                return CommandResult.Fail("Could not resolve Level. Provide level_id or level_name, or ensure project has at least one level.");

            var pt = new XYZ(xMm / 304.8, yMm / 304.8, zMm / 304.8);

            using (var tx = new Transaction(doc, "RvtMcp: Create structural column"))
            {
                tx.Start();
                try
                {
                    if (!symbol.IsActive) symbol.Activate();
                    doc.Regenerate();

                    var inst = doc.Create.NewFamilyInstance(pt, symbol, level, StructuralType.Column);

                    if (heightMm.HasValue)
                    {
                        var topParam = inst.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM);
                        topParam?.Set(heightMm.Value / 304.8);
                    }

                    if (Math.Abs(rotationDeg) > 1e-6)
                    {
                        var axis = Line.CreateBound(pt, pt + XYZ.BasisZ);
                        ElementTransformUtils.RotateElement(doc, inst.Id, axis, rotationDeg * Math.PI / 180.0);
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
                    return CommandResult.Ok(new
                    {
                        created_id = RevitCompat.GetId(inst.Id),
                        type_id = RevitCompat.GetId(symbol.Id),
                        level_id = RevitCompat.GetId(level.Id),
                        base_point_mm = new { x = xMm, y = yMm, z = zMm },
                        structural_type = "Column"
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to create structural column: {ex.Message}");
                }
            }
        }

        private static FamilySymbol ResolveSymbol(Document doc, long? typeId, string typeName)
        {
            if (typeId.HasValue)
                return doc.GetElement(RevitCompat.ToElementId(typeId.Value)) as FamilySymbol;

            var query = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_StructuralColumns)
                .Cast<FamilySymbol>();

            if (!string.IsNullOrWhiteSpace(typeName))
            {
                return query.FirstOrDefault(s =>
                    s.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
                    $"{s.Family.Name}: {s.Name}".Equals(typeName, StringComparison.OrdinalIgnoreCase));
            }
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
