using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateRebarSetHandler : IRevitCommand
    {
        public string Name => "create_rebar_set";
        public string Description => "Create a straight rebar set inside a structural host (column/beam/wall/foundation) using Rebar.CreateFromCurves. Supports Single, FixedNumber, MaximumSpacing layouts. Resolves RebarBarType via bar_type_id or bar_type_name. Coordinates in mm.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""host_id"":{""type"":""integer""},""bar_type_id"":{""type"":""integer""},""bar_type_name"":{""type"":""string""},""layout_rule"":{""type"":""string"",""enum"":[""Single"",""FixedNumber"",""MaximumSpacing""]},""spacing_mm"":{""type"":""number""},""quantity"":{""type"":""integer""},""start_x_mm"":{""type"":""number""},""start_y_mm"":{""type"":""number""},""start_z_mm"":{""type"":""number""},""end_x_mm"":{""type"":""number""},""end_y_mm"":{""type"":""number""},""end_z_mm"":{""type"":""number""}},""required"":[""host_id"",""start_x_mm"",""start_y_mm"",""start_z_mm"",""end_x_mm"",""end_y_mm"",""end_z_mm""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var hostId = req.Value<long?>("host_id");
            var barTypeId = req.Value<long?>("bar_type_id");
            var barTypeName = req.Value<string>("bar_type_name");
            var layoutRule = (req.Value<string>("layout_rule") ?? "Single").Trim();
            var spacingMm = req.Value<double?>("spacing_mm");
            var quantity = req.Value<int?>("quantity") ?? 1;
            var sx = req.Value<double?>("start_x_mm") ?? 0;
            var sy = req.Value<double?>("start_y_mm") ?? 0;
            var sz = req.Value<double?>("start_z_mm") ?? 0;
            var ex = req.Value<double?>("end_x_mm") ?? 0;
            var ey = req.Value<double?>("end_y_mm") ?? 0;
            var ez = req.Value<double?>("end_z_mm") ?? 0;

            if (!hostId.HasValue)
                return CommandResult.Fail("host_id is required.");

            var host = doc.GetElement(RevitCompat.ToElementId(hostId.Value));
            if (host == null)
                return CommandResult.Fail($"Could not resolve host element with id {hostId.Value}.");

            var barType = ResolveBarType(doc, barTypeId, barTypeName);
            if (barType == null)
                return CommandResult.Fail("Could not resolve RebarBarType. Provide bar_type_id or bar_type_name, or ensure project has at least one RebarBarType.");

            var start = new XYZ(sx / 304.8, sy / 304.8, sz / 304.8);
            var end = new XYZ(ex / 304.8, ey / 304.8, ez / 304.8);
            if (start.DistanceTo(end) < 1e-6)
                return CommandResult.Fail("start and end points are identical.");

            // Normal vector — perpendicular to bar direction. For non-vertical bars,
            // normal is the curve cross XYZ.BasisZ (a horizontal vector); for vertical bars,
            // fall back to XYZ.BasisX.
            var dir = (end - start).Normalize();
            XYZ normal;
            if (Math.Abs(dir.Z) > 0.999)
                normal = XYZ.BasisX;
            else
                normal = dir.CrossProduct(XYZ.BasisZ).Normalize();

            var curves = new List<Curve> { Line.CreateBound(start, end) };

            // Validate layout/spacing/quantity combinations.
            var rule = ParseLayout(layoutRule);
            if (!rule.HasValue)
                return CommandResult.Fail($"Unknown layout_rule '{layoutRule}'. Use Single, FixedNumber, or MaximumSpacing.");

            if (rule.Value == RebarLayoutKind.FixedNumber)
            {
                if (quantity < 2)
                    return CommandResult.Fail("layout_rule=FixedNumber requires quantity >= 2.");
                if (!spacingMm.HasValue || spacingMm.Value <= 0)
                    return CommandResult.Fail("layout_rule=FixedNumber requires spacing_mm > 0.");
            }
            else if (rule.Value == RebarLayoutKind.MaximumSpacing)
            {
                if (!spacingMm.HasValue || spacingMm.Value <= 0)
                    return CommandResult.Fail("layout_rule=MaximumSpacing requires spacing_mm > 0.");
            }

            using (var tx = new Transaction(doc, "RvtMcp: Create rebar set"))
            {
                tx.Start();
                try
                {
                    Rebar rebar = CreateRebarFromCurves(doc, barType, host, normal, curves);
                    if (rebar == null)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        return CommandResult.Fail("Rebar.CreateFromCurves returned null.");
                    }

                    // Apply layout via the shape-driven accessor.
                    var accessor = rebar.GetShapeDrivenAccessor();
                    if (accessor == null)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        return CommandResult.Fail("Created rebar is not shape-driven; cannot apply layout rule.");
                    }

                    switch (rule.Value)
                    {
                        case RebarLayoutKind.Single:
                            accessor.SetLayoutAsSingle();
                            break;
                        case RebarLayoutKind.FixedNumber:
                        {
                            var spacingFt = spacingMm!.Value / 304.8;
                            var arrayLength = spacingFt * (quantity - 1);
                            accessor.SetLayoutAsFixedNumber(quantity, arrayLength, true, true, true);
                            break;
                        }
                        case RebarLayoutKind.MaximumSpacing:
                        {
                            var spacingFt = spacingMm!.Value / 304.8;
                            // Heuristic array length: assume host bounding-box footprint or fall back
                            // to a generous default proportional to spacing; user can adjust by passing
                            // a tighter quantity-based override later if needed.
                            var arrayLength = ComputeArrayLength(host, spacingFt, quantity);
                            accessor.SetLayoutAsMaximumSpacing(spacingFt, arrayLength, true, true, true);
                            break;
                        }
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");
                    return CommandResult.Ok(new
                    {
                        created_id = RevitCompat.GetId(rebar.Id),
                        host_id = RevitCompat.GetId(host.Id),
                        bar_type_id = RevitCompat.GetId(barType.Id),
                        layout_rule = rule.Value.ToString(),
                        quantity = rebar.Quantity,
                        spacing_mm = spacingMm
                    });
                }
                catch (Exception ex_)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to create rebar set: {ex_.Message}");
                }
            }
        }

        private static Rebar CreateRebarFromCurves(Document doc, RebarBarType barType, Element host, XYZ normal, IList<Curve> curves)
        {
#if REVIT2027_OR_GREATER
            // R27+ removed the legacy RebarHookType/RebarHookOrientation overload.
            // Use the new BarTerminationsData overload (default = no hooks, no end treatments).
            var terms = new BarTerminationsData(doc);
            return Rebar.CreateFromCurves(
                doc,
                RebarStyle.Standard,
                barType,
                host,
                normal,
                curves,
                terms,
                useExistingShapeIfPossible: true,
                createNewShape: true);
#else
            // R22-R26: legacy overload accepts null hook types and Right/Left orientations.
            return Rebar.CreateFromCurves(
                doc,
                RebarStyle.Standard,
                barType,
                null,
                null,
                host,
                normal,
                curves,
                RebarHookOrientation.Right,
                RebarHookOrientation.Left,
                useExistingShapeIfPossible: true,
                createNewShape: true);
#endif
        }

        private static double ComputeArrayLength(Element host, double spacingFt, int quantityHint)
        {
            try
            {
                var bbox = host?.get_BoundingBox(null);
                if (bbox != null)
                {
                    var span = bbox.Max - bbox.Min;
                    var hostLength = Math.Max(span.X, Math.Max(span.Y, span.Z));
                    if (hostLength > 0) return hostLength;
                }
            }
            catch { }
            // Fallback: derive from spacing × (quantityHint or 10).
            var n = quantityHint > 1 ? quantityHint : 10;
            return spacingFt * (n - 1);
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

        private enum RebarLayoutKind { Single, FixedNumber, MaximumSpacing }

        private static RebarLayoutKind? ParseLayout(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return RebarLayoutKind.Single;
            if (s.Equals("Single", StringComparison.OrdinalIgnoreCase)) return RebarLayoutKind.Single;
            if (s.Equals("FixedNumber", StringComparison.OrdinalIgnoreCase)) return RebarLayoutKind.FixedNumber;
            if (s.Equals("MaximumSpacing", StringComparison.OrdinalIgnoreCase)) return RebarLayoutKind.MaximumSpacing;
            return null;
        }
    }
}
