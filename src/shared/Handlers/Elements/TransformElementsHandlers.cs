using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class MoveElementsHandler : IRevitCommand
    {
        public string Name => "move_elements";
        public string Description => "Translate existing elements in model coordinates (mm), atomically and with Undo.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},""dx_mm"":{""type"":""number""},""dy_mm"":{""type"":""number""},""dz_mm"":{""type"":""number""}},""required"":[""elementIds"",""dx_mm"",""dy_mm"",""dz_mm""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
            => PlacementOperation.Execute(app, paramsJson, false);
    }

    public class RotateElementsHandler : IRevitCommand
    {
        public string Name => "rotate_elements";
        public string Description => "Rotate existing elements around an explicit model-space vertical Z axis; degrees, positive counterclockwise, Undo supported.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},""angle_degrees"":{""type"":""number""},""pivot_x_mm"":{""type"":""number""},""pivot_y_mm"":{""type"":""number""},""pivot_z_mm"":{""type"":""number""}},""required"":[""elementIds"",""angle_degrees"",""pivot_x_mm"",""pivot_y_mm"",""pivot_z_mm""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
            => PlacementOperation.Execute(app, paramsJson, true);
    }

    internal static class PlacementOperation
    {
        private const double MmPerFoot = 304.8;
        private const double PositionTolerance = 1.0 / MmPerFoot;
        private const double AngleTolerance = 1e-6;

        internal static CommandResult Execute(UIApplication app, string paramsJson, bool rotate)
        {
            try
            {
                return ExecuteCore(app, JObject.Parse(paramsJson), rotate);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Placement failed: " + ex.Message);
            }
        }

        private static CommandResult ExecuteCore(UIApplication app, JObject request, bool rotate)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");
            if (doc.IsReadOnly) return CommandResult.Fail("The document is read-only.");
            var inputIds = request["elementIds"] as JArray;
            if (inputIds == null || inputIds.Any(t => t.Type != JTokenType.Integer))
                return CommandResult.Fail("elementIds must be an array of positive integer IDs.");
            var rawIds = PlacementInput.ElementIds(inputIds.ToObject<long[]>());
            var ids = rawIds.Select(RevitCompat.ToElementId).ToList();
            var elements = ids.Select(doc.GetElement).ToList();
            if (elements.Any(e => e == null)) return CommandResult.Fail("An element id does not exist; nothing was changed.");
            if (elements.Any(e => e is ElementType || e.Pinned || e.GroupId != ElementId.InvalidElementId))
                return CommandResult.Fail("Types, pinned elements and individual group members are refused; nothing was changed.");

            var x = Required(request, rotate ? "pivot_x_mm" : "dx_mm");
            var y = Required(request, rotate ? "pivot_y_mm" : "dy_mm");
            var z = Required(request, rotate ? "pivot_z_mm" : "dz_mm");
            var degrees = rotate ? Required(request, "angle_degrees") : 0;
            PlacementInput.Finite(x, y, z, degrees);
            // A full turn is equivalent to zero; normalizing avoids overflow and
            // precision loss for large finite angles before conversion to radians.
            var radians = (degrees % 360.0) * Math.PI / 180.0;
            var vector = new XYZ(x / MmPerFoot, y / MmPerFoot, z / MmPerFoot);
            var before = elements.Select(Snapshot).ToArray();
            if (before.Any(s => s.Point == null))
                return CommandResult.Fail("Only elements with a point or bounded curve location are supported; nothing was changed.");
            var transform = rotate ? Transform.CreateRotationAtPoint(XYZ.BasisZ, radians, vector) : Transform.CreateTranslation(vector);

            using (var tx = new Transaction(doc, rotate ? "MCP: Rotate elements" : "MCP: Move elements"))
            {
                tx.Start();
                try
                {
                    if (rotate)
                        ElementTransformUtils.RotateElements(doc, ids, Line.CreateBound(vector, vector + XYZ.BasisZ), radians);
                    else
                        ElementTransformUtils.MoveElements(doc, ids, vector);
                    doc.Regenerate();
                    // Revit may constrain a transform without throwing. Check each
                    // selected location before commit so one miss rolls back all IDs.
                    for (var i = 0; i < elements.Count; i++)
                    {
                        var after = Snapshot(elements[i]);
                        var expected = before[i];
                        if (after.Point == null || after.Point.DistanceTo(transform.OfPoint(expected.Point)) > PositionTolerance
                            || (expected.End != null && (after.End == null || after.End.DistanceTo(transform.OfPoint(expected.End)) > PositionTolerance))
                            || (expected.Rotation.HasValue && (!after.Rotation.HasValue
                                || Math.Abs(Math.IEEERemainder(after.Rotation.Value - expected.Rotation.Value - (rotate ? radians : 0), 2 * Math.PI)) > AngleTolerance)))
                            throw new InvalidOperationException("Revit constrained the requested transform; all changes were rolled back.");
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; changes were not confirmed.");
                }
                catch
                {
                    if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                    throw;
                }
            }
            return CommandResult.Ok(new
            {
                operation = rotate ? "rotate" : "move", count = ids.Count,
                angle_degrees = degrees,
                before = before.Select(s => s.Dto).ToArray(),
                after = elements.Select(e => Snapshot(e).Dto).ToArray()
            });
        }

        private static double Required(JObject request, string name)
        {
            var token = request[name];
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float))
                throw new ArgumentException(name + " must be an explicit number.");
            return token.Value<double>();
        }

        private sealed class State
        {
            public XYZ Point;
            public XYZ End;
            public double? Rotation;
            public object Dto;
        }

        private static State Snapshot(Element element)
        {
            var pointLocation = element.Location as LocationPoint;
            var curve = (element.Location as LocationCurve)?.Curve;
            var point = pointLocation?.Point ?? (curve != null && curve.IsBound ? curve.GetEndPoint(0) : null);
            var end = curve != null && curve.IsBound ? curve.GetEndPoint(1) : null;
            var rotation = pointLocation == null ? (double?)null : pointLocation.Rotation;
            var box = element.get_BoundingBox(null);
            return new State { Point = point, End = end, Rotation = rotation, Dto = new
            {
                element_id = RevitCompat.GetId(element.Id),
                location_mm = Coordinates(point),
                curve_end_mm = Coordinates(end),
                rotation_degrees = rotation * 180.0 / Math.PI,
                bbox_min_mm = Coordinates(box?.Min), bbox_max_mm = Coordinates(box?.Max)
            }};
        }

        private static object Coordinates(XYZ p)
            => p == null ? null : new { x = p.X * MmPerFoot, y = p.Y * MmPerFoot, z = p.Z * MmPerFoot };
    }
}
