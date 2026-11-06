using System;
using System.Linq;
using System.Collections.Generic;
using RvtMcp.Plugin.Failures;
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
        public string Description => "Rotate existing elements about a vertical Z axis through one explicit model-space pivot, or each about its own location (pivot_mode=each_location); degrees, positive counterclockwise, Undo supported.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},""angle_degrees"":{""type"":""number""},""pivot_x_mm"":{""type"":""number""},""pivot_y_mm"":{""type"":""number""},""pivot_z_mm"":{""type"":""number""},""pivot_mode"":{""type"":""string"",""enum"":[""point"",""each_location""]}},""required"":[""elementIds"",""angle_degrees""]}";

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

            var eachLocation = false;
            if (rotate)
            {
                var mode = request["pivot_mode"];
                if (mode != null && mode.Type != JTokenType.Null)
                {
                    if (mode.Type == JTokenType.String && (string)mode == "each_location") eachLocation = true;
                    else if (mode.Type != JTokenType.String || (string)mode != "point")
                        return CommandResult.Fail("pivot_mode must be 'point' or 'each_location'; nothing was changed.");
                }
                if (eachLocation && new[] { "pivot_x_mm", "pivot_y_mm", "pivot_z_mm" }.Any(name => !IsMissing(request, name)))
                    return CommandResult.Fail("pivot_mode='each_location' turns each element about its own location; omit pivot_x_mm, pivot_y_mm and pivot_z_mm. Nothing was changed.");
            }

            double x = 0, y = 0, z = 0;
            if (!eachLocation)
            {
                if (rotate && (IsMissing(request, "pivot_x_mm") || IsMissing(request, "pivot_y_mm")))
                    return CommandResult.Fail("Pass pivot_x_mm and pivot_y_mm for one pivot, or pivot_mode='each_location' to turn each element in place; nothing was changed.");
                x = Required(request, rotate ? "pivot_x_mm" : "dx_mm");
                y = Required(request, rotate ? "pivot_y_mm" : "dy_mm");
                // The axis is vertical, so the pivot's Z does not change the result.
                z = rotate && IsMissing(request, "pivot_z_mm") ? 0 : Required(request, rotate ? "pivot_z_mm" : "dz_mm");
            }
            var degrees = rotate ? Required(request, "angle_degrees") : 0;
            PlacementInput.Finite(x, y, z, degrees);
            // A full turn is equivalent to zero; normalizing avoids overflow and
            // precision loss for large finite angles before conversion to radians.
            var radians = (degrees % 360.0) * Math.PI / 180.0;
            var vector = new XYZ(x / MmPerFoot, y / MmPerFoot, z / MmPerFoot);
            var before = elements.Select(Snapshot).ToArray();
            if (before.Any(s => s.Point == null))
                return CommandResult.Fail("Only elements with a point or bounded curve location are supported; nothing was changed.");
            // In each_location mode every element turns about its own location point, or about
            // the midpoint between a location curve's end points.
            var pivots = before.Select(s => !eachLocation ? vector : s.End == null ? s.Point
                : new XYZ((s.Point.X + s.End.X) / 2, (s.Point.Y + s.End.Y) / 2, (s.Point.Z + s.End.Z) / 2)).ToArray();
            var transforms = pivots.Select(p => rotate ? Transform.CreateRotationAtPoint(XYZ.BasisZ, radians, p) : Transform.CreateTranslation(vector)).ToArray();

            State[] after;
            // Keep committed inner edits reversible until final placement is known.
            // BeginBatch also preserves the failure ledger's outer-group accounting.
            using (FailureControl.BeginBatch())
            using (var group = new TransactionGroup(doc, rotate ? "MCP: Rotate elements" : "MCP: Move elements"))
            {
                if (group.Start() != TransactionStatus.Started)
                    return CommandResult.Fail("Revit did not start the placement group; nothing was changed.");
                try
                {
                    using (var tx = new Transaction(doc, rotate ? "MCP: Rotate elements" : "MCP: Move elements"))
                    {
                        if (tx.Start() != TransactionStatus.Started)
                            throw new InvalidOperationException("Revit did not start the placement transaction; nothing was changed.");
                        try
                        {
                            if (!rotate)
                                ElementTransformUtils.MoveElements(doc, ids, vector);
                            else if (!eachLocation)
                                ElementTransformUtils.RotateElements(doc, ids, Line.CreateBound(vector, vector + XYZ.BasisZ), radians);
                            else
                                for (var i = 0; i < ids.Count; i++)
                                    ElementTransformUtils.RotateElement(doc, ids[i], Line.CreateBound(pivots[i], pivots[i] + XYZ.BasisZ), radians);
                            doc.Regenerate();
                            ValidatePlacement(doc, ids, before, transforms, rotate ? radians : 0);
                            if (tx.Commit() != TransactionStatus.Committed)
                                throw new InvalidOperationException("Revit did not commit the transaction; changes were not confirmed.");
                        }
                        catch
                        {
                            if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                            throw;
                        }
                    }
                    // Commit may apply auto-joins or other finalization changes.
                    // Re-resolve IDs, validate the committed state, and capture the
                    // response while the group can still restore the whole selection.
                    after = ValidatePlacement(doc, ids, before, transforms, rotate ? radians : 0);
                    if (group.Assimilate() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Revit did not finalize the placement group; changes were not confirmed.");
                }
                catch
                {
                    if (group.GetStatus() == TransactionStatus.Started
                        && group.RollBack() != TransactionStatus.RolledBack)
                        throw new InvalidOperationException("Placement group rollback was not confirmed.");
                    if (group.GetStatus() == TransactionStatus.RolledBack)
                        FailureControl.NotifyBatchRolledBack();
                    throw;
                }
            }
            return CommandResult.Ok(new
            {
                operation = rotate ? "rotate" : "move", count = ids.Count,
                angle_degrees = degrees,
                pivot_mode = rotate ? (eachLocation ? "each_location" : "point") : null,
                pivot_mm = rotate && !eachLocation ? Coordinates(vector) : null,
                before = before.Select(s => s.Dto).ToArray(),
                after = after.Select(state => state.Dto).ToArray()
            });
        }

        private static State[] ValidatePlacement(Document doc, List<ElementId> ids, State[] before, Transform[] transforms, double angle)
        {
            var result = ids.Select(id => Snapshot(doc.GetElement(id))).ToArray();
            for (var i = 0; i < result.Length; i++)
            {
                var actual = result[i];
                var expected = before[i];
                var transform = transforms[i];
                if (actual.Point == null || actual.Point.DistanceTo(transform.OfPoint(expected.Point)) > PositionTolerance
                    || (expected.End != null && (actual.End == null || actual.End.DistanceTo(transform.OfPoint(expected.End)) > PositionTolerance))
                    || (expected.Rotation.HasValue && (!actual.Rotation.HasValue
                        || Math.Abs(Math.IEEERemainder(actual.Rotation.Value - expected.Rotation.Value - angle, 2 * Math.PI)) > AngleTolerance)))
                    throw new InvalidOperationException("Revit constrained the requested transform of element " + RevitCompat.GetId(ids[i])
                        + " (for example a hosted, joined or level-bound element); all changes were rolled back.");
            }
            return result;
        }

        private static bool IsMissing(JObject request, string name)
            => request[name] == null || request[name].Type == JTokenType.Null;

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
            if (element == null) throw new InvalidOperationException("An element disappeared while finalizing placement.");
            var pointLocation = element.Location as LocationPoint;
            var curve = (element.Location as LocationCurve)?.Curve;
            var point = pointLocation?.Point ?? (curve != null && curve.IsBound ? curve.GetEndPoint(0) : null);
            var end = curve != null && curve.IsBound ? curve.GetEndPoint(1) : null;
            var rotation = pointLocation == null ? (double?)null : ReadRotation(pointLocation);
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

        // Rooms and model groups have a location point without a rotation; only their position is checked.
        private static double? ReadRotation(LocationPoint location)
        {
            try { return location.Rotation; }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException) { return null; }
        }

        private static object Coordinates(XYZ p)
            => p == null ? null : new { x = p.X * MmPerFoot, y = p.Y * MmPerFoot, z = p.Z * MmPerFoot };
    }
}
