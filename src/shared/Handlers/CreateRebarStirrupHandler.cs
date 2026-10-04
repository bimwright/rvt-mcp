using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateRebarStirrupHandler : IRevitCommand
    {
        public string Name => "create_rebar_stirrup";
        public string Description => "Create a shape-driven rebar (typically a closed stirrup) inside a concrete host using Rebar.CreateFromRebarShape. Resolves RebarShape via shape_id or shape_name (e.g. 'Stirrup T1', 'M_T1'). Coordinates in mm; direction vectors are unit-less.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""host_id"":{""type"":""integer""},""bar_type_id"":{""type"":""integer""},""bar_type_name"":{""type"":""string""},""shape_id"":{""type"":""integer""},""shape_name"":{""type"":""string""},""origin_x_mm"":{""type"":""number""},""origin_y_mm"":{""type"":""number""},""origin_z_mm"":{""type"":""number""},""x_vec_x"":{""type"":""number""},""x_vec_y"":{""type"":""number""},""x_vec_z"":{""type"":""number""},""y_vec_x"":{""type"":""number""},""y_vec_y"":{""type"":""number""},""y_vec_z"":{""type"":""number""}},""required"":[""host_id"",""origin_x_mm"",""origin_y_mm"",""origin_z_mm""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var hostId = req.Value<long?>("host_id");
            var barTypeId = req.Value<long?>("bar_type_id");
            var barTypeName = req.Value<string>("bar_type_name");
            var shapeId = req.Value<long?>("shape_id");
            var shapeName = req.Value<string>("shape_name");
            var ox = req.Value<double?>("origin_x_mm") ?? 0;
            var oy = req.Value<double?>("origin_y_mm") ?? 0;
            var oz = req.Value<double?>("origin_z_mm") ?? 0;

            // Direction vectors — unit-less; defaults to world X / Y.
            var xvx = req.Value<double?>("x_vec_x") ?? 1;
            var xvy = req.Value<double?>("x_vec_y") ?? 0;
            var xvz = req.Value<double?>("x_vec_z") ?? 0;
            var yvx = req.Value<double?>("y_vec_x") ?? 0;
            var yvy = req.Value<double?>("y_vec_y") ?? 1;
            var yvz = req.Value<double?>("y_vec_z") ?? 0;

            if (!hostId.HasValue)
                return CommandResult.Fail("host_id is required.");

            var host = doc.GetElement(RevitCompat.ToElementId(hostId.Value));
            if (host == null)
                return CommandResult.Fail($"Could not resolve host element with id {hostId.Value}.");

            var barType = ResolveBarType(doc, barTypeId, barTypeName);
            if (barType == null)
                return CommandResult.Fail("Could not resolve RebarBarType. Provide bar_type_id or bar_type_name, or ensure project has at least one RebarBarType.");

            var shape = ResolveShape(doc, shapeId, shapeName);
            if (shape == null)
                return CommandResult.Fail("Could not resolve RebarShape. Provide shape_id or shape_name (e.g. 'Stirrup T1', 'M_T1'), or ensure project has at least one RebarShape loaded.");

            var origin = new XYZ(ox / 304.8, oy / 304.8, oz / 304.8);
            var xVec = SafeNormalize(new XYZ(xvx, xvy, xvz), XYZ.BasisX);
            var yVec = SafeNormalize(new XYZ(yvx, yvy, yvz), XYZ.BasisY);

            if (xVec.IsAlmostEqualTo(yVec) || xVec.CrossProduct(yVec).GetLength() < 1e-6)
                return CommandResult.Fail("x_vec and y_vec must be linearly independent (non-parallel) directions.");

            using (var tx = new Transaction(doc, "RvtMcp: Create rebar stirrup"))
            {
                tx.Start();
                try
                {
                    var rebar = Rebar.CreateFromRebarShape(doc, shape, barType, host, origin, xVec, yVec);
                    if (rebar == null)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        return CommandResult.Fail("Rebar.CreateFromRebarShape returned null.");
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
                    return CommandResult.Ok(new
                    {
                        created_id = RevitCompat.GetId(rebar.Id),
                        host_id = RevitCompat.GetId(host.Id),
                        bar_type_id = RevitCompat.GetId(barType.Id),
                        shape_id = RevitCompat.GetId(shape.Id),
                        shape_name = shape.Name
                    });
                }
                catch (Exception ex_)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to create rebar stirrup: {ex_.Message}");
                }
            }
        }

        private static XYZ SafeNormalize(XYZ v, XYZ fallback)
        {
            if (v == null) return fallback;
            var len = v.GetLength();
            if (len < 1e-9) return fallback;
            return v.Normalize();
        }

        private static RebarBarType ResolveBarType(Document doc, long? barTypeId, string barTypeName)
        {
            if (barTypeId.HasValue)
                return doc.GetElement(RevitCompat.ToElementId(barTypeId.Value)) as RebarBarType;

            var query = new FilteredElementCollector(doc)
                .OfClass(typeof(RebarBarType))
                .Cast<RebarBarType>();

            if (!string.IsNullOrWhiteSpace(barTypeName))
            {
                return query.FirstOrDefault(b =>
                    b.Name.Equals(barTypeName, StringComparison.OrdinalIgnoreCase));
            }
            return query.FirstOrDefault();
        }

        private static RebarShape ResolveShape(Document doc, long? shapeId, string shapeName)
        {
            if (shapeId.HasValue)
                return doc.GetElement(RevitCompat.ToElementId(shapeId.Value)) as RebarShape;

            var query = new FilteredElementCollector(doc)
                .OfClass(typeof(RebarShape))
                .Cast<RebarShape>();

            if (!string.IsNullOrWhiteSpace(shapeName))
            {
                return query.FirstOrDefault(s =>
                    s.Name.Equals(shapeName, StringComparison.OrdinalIgnoreCase));
            }
            return query.FirstOrDefault();
        }
    }
}
