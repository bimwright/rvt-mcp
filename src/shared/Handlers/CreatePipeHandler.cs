using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Creates a plumbing pipe between two points. Coordinates in mm.
    /// Inherits and connects from an unambiguous open piping connector when no system type is supplied.
    /// </summary>
    public class CreatePipeHandler : IRevitCommand
    {
        private const double MmToFeet = 1.0 / 304.8;
        private const double FeetToMm = 304.8;
        private const double StartToleranceMm = 1.0;
        private const double DiameterToleranceMm = 0.01;

        public string Name => "create_pipe";

        public string Description =>
            "Create a plumbing pipe between two points. All coordinates and the diameter are " +
            "in millimeters. Without system_type_id, inherits system and diameter from one open piping End connector " +
            "within 1 mm of the start and connects immediately. Ambiguous matches or a conflicting diameter are rejected. " +
            "Use start_element_id/start_connector_id to disambiguate. With no match, uses the first PipingSystemType. " +
            "Pipe type defaults to first available; level defaults to nearest start_z.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""start_x"",""start_y"",""start_z"",""end_x"",""end_y"",""end_z""],
  ""properties"": {
    ""start_x"": {""type"":""number""}, ""start_y"": {""type"":""number""}, ""start_z"": {""type"":""number""},
    ""end_x"": {""type"":""number""}, ""end_y"": {""type"":""number""}, ""end_z"": {""type"":""number""},
    ""pipe_type_id"": {""type"":""integer"",""description"":""PipeType ElementId. If omitted, first available.""},
    ""system_type_id"": {""type"":""integer"",""description"":""Explicit type creates an independent pipe. If omitted, inherits from a unique open piping connector within 1 mm, otherwise first available type.""},
    ""start_element_id"": {""type"":""integer"",""description"":""Optional owner of the start connector. Requires system_type_id omitted.""},
    ""start_connector_id"": {""type"":""integer"",""description"":""Connector.Id, not ordinal. Requires start_element_id.""},
    ""level_id"": {""type"":""integer"",""description"":""Level ElementId. If omitted, nearest to start_z.""},
    ""diameter"": {""type"":""number"",""description"":""Pipe diameter in mm.""}
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            try { return ExecuteCore(app, paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Failed to create pipe: " + ex.Message); }
        }

        private CommandResult ExecuteCore(UIApplication app, string paramsJson)
        {
            var doc = app?.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            JObject request;
            try
            {
                request = JObject.Parse(paramsJson ?? "{}");
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail("Invalid JSON parameters: " + ex.Message);
            }

            // Required coordinates (mm).
            double startXmm, startYmm, startZmm, endXmm, endYmm, endZmm;
            try
            {
                startXmm = RequireDouble(request, "start_x");
                startYmm = RequireDouble(request, "start_y");
                startZmm = RequireDouble(request, "start_z");
                endXmm = RequireDouble(request, "end_x");
                endYmm = RequireDouble(request, "end_y");
                endZmm = RequireDouble(request, "end_z");
            }
            catch (ArgumentException ex)
            {
                return CommandResult.Fail(ex.Message);
            }

            var startPt = new XYZ(startXmm * MmToFeet, startYmm * MmToFeet, startZmm * MmToFeet);
            var endPt = new XYZ(endXmm * MmToFeet, endYmm * MmToFeet, endZmm * MmToFeet);

            if (startPt.DistanceTo(endPt) < 1e-7)
                return CommandResult.Fail("Start and end points are coincident; pipe has zero length.");

            // Resolve PipeType.
            PipeType pipeType;
            var pipeTypeId = request.Value<long?>("pipe_type_id");
            if (pipeTypeId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(pipeTypeId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(pipeTypeId.Value));
                pipeType = doc.GetElement(RevitCompat.ToElementId(pipeTypeId.Value)) as PipeType;
                if (pipeType == null)
                    return CommandResult.Fail($"Element {pipeTypeId.Value} is not a valid PipeType.");
            }
            else
            {
                pipeType = FirstOfClass<PipeType>(doc);
                if (pipeType == null)
                    return CommandResult.Fail("No PipeType found in the project. Load a pipe family first.");
            }

            var diameterMm = request.Value<double?>("diameter");
            if (diameterMm.HasValue && (double.IsNaN(diameterMm.Value) || double.IsInfinity(diameterMm.Value) || diameterMm <= 0))
                return CommandResult.Fail("diameter must be a finite positive value in mm.");

            PipingSystemType systemType = null;
            var systemTypeId = request.Value<long?>("system_type_id");
            var startElementId = request.Value<long?>("start_element_id");
            var startConnectorId = request.Value<int?>("start_connector_id");
            if (startConnectorId.HasValue && !startElementId.HasValue)
                return CommandResult.Fail("start_connector_id requires start_element_id.");
            if (systemTypeId.HasValue && startElementId.HasValue)
                return CommandResult.Fail("Omit system_type_id when selecting a start connector to inherit from.");

            Connector startConnector = null;
            string systemTypeSource = "explicit";
            if (systemTypeId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(systemTypeId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(systemTypeId.Value));
                systemType = doc.GetElement(RevitCompat.ToElementId(systemTypeId.Value)) as PipingSystemType;
                if (systemType == null)
                    return CommandResult.Fail($"Element {systemTypeId.Value} is not a valid PipingSystemType.");
            }
            else
            {
                var candidates = FindStartConnectors(doc, startPt, startElementId, startConnectorId);
                if (candidates.Count > 1)
                    return CommandResult.Ok(new
                    {
                        created = false, reason = "ambiguous_start_connector",
                        error = "Multiple open piping connectors match the start. Specify start_element_id and start_connector_id.",
                        candidates = candidates.Select(c => new { element_id = RevitCompat.GetId(c.Owner.Id), connector_id = c.Id }).ToArray()
                    });
                if (candidates.Count == 0 && startElementId.HasValue)
                    return CommandResult.Fail("Selected start connector must be an open piping End connector within 1 mm of the start point.");
                startConnector = candidates.SingleOrDefault();
                if (startConnector != null)
                {
                    systemTypeSource = "connector";
                    MepConnectionSystemType.Read(startConnector);
                    if (diameterMm.HasValue && Math.Abs(diameterMm.Value - startConnector.Radius * 2 * FeetToMm) > DiameterToleranceMm)
                        return CommandResult.Fail("Requested diameter differs from the start connector diameter (" +
                            Math.Round(startConnector.Radius * 2 * FeetToMm, 3) + " mm). Omit diameter to inherit it or create an explicit transition.");
                }
                else
                {
                    systemTypeSource = "default";
                    systemType = FirstOfClass<PipingSystemType>(doc);
                    if (systemType == null)
                        return CommandResult.Fail("No PipingSystemType found in the project.");
                }
            }

            // Resolve Level.
            Level level;
            var levelId = request.Value<long?>("level_id");
            if (levelId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(levelId.Value))
                    return CommandResult.Fail(RevitCompat.ElementIdRangeError(levelId.Value));
                level = doc.GetElement(RevitCompat.ToElementId(levelId.Value)) as Level;
                if (level == null)
                    return CommandResult.Fail($"Element {levelId.Value} is not a valid Level.");
            }
            else
            {
                level = NearestLevel(doc, startZmm * MmToFeet);
                if (level == null)
                    return CommandResult.Fail("No Level found in the project.");
            }

            using (var tx = new Transaction(doc, "RvtMcp: create pipe"))
            {
                tx.Start();
                try
                {
                    var sourceOwnerId = startConnector == null ? (long?)null : RevitCompat.GetId(startConnector.Owner.Id);
                    var sourceConnectorId = startConnector == null ? (int?)null : startConnector.Id;
                    var pipe = startConnector == null
                        ? Pipe.Create(doc, systemType.Id, pipeType.Id, level.Id, startPt, endPt)
                        : Pipe.Create(doc, pipeType.Id, level.Id, startConnector, endPt);
                    if (pipe == null)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        return CommandResult.Fail("Pipe.Create returned null.");
                    }

                    if (startConnector == null && diameterMm.HasValue)
                    {
                        var diamParam = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                        if (diamParam != null && !diamParam.IsReadOnly)
                            diamParam.Set(diameterMm.Value * MmToFeet);
                    }

                    doc.Regenerate();
                    var actualSystemType = doc.GetElement(pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM).AsElementId()) as PipingSystemType;
                    if (actualSystemType == null)
                        throw new InvalidOperationException("Cannot read the created pipe's actual system type.");
                    if (startConnector != null && !startConnector.IsConnected)
                        throw new InvalidOperationException("The new pipe did not connect to the selected start connector.");

                    double lengthMm = ReadLengthMm(pipe);
                    double actualDiameterMm = ReadDiameterMm(pipe);
                    string actualTypeName = actualSystemType.Name;
                    long actualTypeId = RevitCompat.GetId(actualSystemType.Id);

                    if (tx.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Revit did not commit pipe creation.");

                    return CommandResult.Ok(new
                    {
                        created = true,
                        pipe_id = RevitCompat.GetId(pipe.Id),
                        pipe_type = pipeType.Name,
                        system_type = actualTypeName,
                        system_type_id = actualTypeId,
                        system_type_source = systemTypeSource,
                        connected_to_start = startConnector != null,
                        start_element_id = sourceOwnerId,
                        start_connector_id = sourceConnectorId,
                        level = level.Name,
                        length_mm = Math.Round(lengthMm, 1),
                        diameter_mm = Math.Round(actualDiameterMm, 1),
                        error = (string)null
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Failed to create pipe: " + ex.Message);
                }
            }
        }

        private static double RequireDouble(JObject request, string key)
        {
            var token = request[key];
            if (token == null || token.Type == JTokenType.Null)
                throw new ArgumentException($"Parameter '{key}' is required.");
            try
            {
                var value = token.Value<double>();
                if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException();
                return value;
            }
            catch
            {
                throw new ArgumentException($"Parameter '{key}' must be a number.");
            }
        }

        private static List<Connector> FindStartConnectors(Document doc, XYZ start, long? elementId, int? connectorId)
        {
            IEnumerable<Element> owners;
            if (elementId.HasValue)
            {
                var owner = doc.GetElement(RevitCompat.ToElementId(elementId.Value));
                if (owner == null) throw new ArgumentException("Start element was not found.");
                owners = new[] { owner };
            }
            else
            {
                owners = new FilteredElementCollector(doc).OfClass(typeof(MEPCurve)).Cast<Element>()
                    .Concat(new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<Element>());
            }
            var matches = new List<Connector>();
            foreach (var owner in owners)
            {
                var manager = owner is MEPCurve curve ? curve.ConnectorManager
                    : (owner as FamilyInstance)?.MEPModel?.ConnectorManager;
                if (manager == null) continue;
                foreach (Connector connector in manager.Connectors)
                {
                    if (connectorId.HasValue && connector.Id != connectorId.Value) continue;
                    if (connector.ConnectorType == ConnectorType.End && connector.Domain == Domain.DomainPiping &&
                        !connector.IsConnected && connector.Origin.DistanceTo(start) <= StartToleranceMm * MmToFeet)
                        matches.Add(connector);
                }
            }
            return matches;
        }

        private static T FirstOfClass<T>(Document doc) where T : Element
        {
            var collector = new FilteredElementCollector(doc).OfClass(typeof(T));
            foreach (T el in collector)
                return el;
            return null;
        }

        private static Level NearestLevel(Document doc, double elevationFt)
        {
            Level nearest = null;
            double bestDelta = double.MaxValue;
            var collector = new FilteredElementCollector(doc).OfClass(typeof(Level));
            foreach (Level lv in collector)
            {
                double delta = Math.Abs(lv.Elevation - elevationFt);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    nearest = lv;
                }
            }
            return nearest;
        }

        private static double ReadLengthMm(Pipe pipe)
        {
            try
            {
                var lengthParam = pipe.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
                if (lengthParam != null && lengthParam.HasValue)
                    return lengthParam.AsDouble() * FeetToMm;
            }
            catch { }

            try
            {
                if (pipe.Location is LocationCurve lc && lc.Curve != null)
                    return lc.Curve.Length * FeetToMm;
            }
            catch { }

            return 0.0;
        }

        private static double ReadDiameterMm(Pipe pipe)
        {
            try
            {
                var diamParam = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                if (diamParam != null && diamParam.HasValue)
                    return diamParam.AsDouble() * FeetToMm;
            }
            catch { }

            try
            {
                return pipe.Diameter * FeetToMm;
            }
            catch { }

            return 0.0;
        }
    }
}
