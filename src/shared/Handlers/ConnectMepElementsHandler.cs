using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// connect_mep_elements — Connect the nearest open connectors of two MEP elements
    /// (e.g. snap a duct to mechanical equipment).
    /// </summary>
    public class ConnectMepElementsHandler : IRevitCommand
    {
        public string Name => "connect_mep_elements";

        public string Description =>
            "Connect the nearest open connectors of two MEP elements (e.g. snap a duct to equipment). " +
            "Resolves a ConnectorManager for each element, then picks the closest pair of unused connectors " +
            "with matching domains (or specific connector ids if supplied) and calls ConnectTo. " +
            "Rejects different assigned piping/HVAC system type IDs before connecting; unassigned equipment ports are allowed. " +
            "Returns the two connector origins in mm. Revit allows connecting non-coincident connectors; " +
            "the gap distance is reported when present.";

        public string ParametersSchema => @"{
""type"":""object"",
""required"":[""element_id_1"",""element_id_2""],
""properties"":{
""element_id_1"":{""type"":""integer""},
""element_id_2"":{""type"":""integer""},
""connector_index_1"":{""type"":""integer"",""description"":""Optional: Connector id (the connector_id field from get_mep_element_connectors), NOT the ordinal. If omitted, nearest unused connector is chosen.""},
""connector_index_2"":{""type"":""integer"",""description"":""Optional: Connector id (the connector_id field from get_mep_element_connectors), NOT the ordinal.""}
}}";

        private const double FeetToMm = 304.8;

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            try { return ExecuteCore(app, paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Failed to connect MEP elements: " + ex.Message); }
        }

        private CommandResult ExecuteCore(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            JObject request;
            try
            {
                request = JObject.Parse(paramsJson);
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail("Invalid JSON parameters: " + ex.Message);
            }

            var elementId1 = request.Value<long?>("element_id_1");
            var elementId2 = request.Value<long?>("element_id_2");
            if (elementId1 == null)
                return CommandResult.Fail("element_id_1 is required.");
            if (elementId2 == null)
                return CommandResult.Fail("element_id_2 is required.");

            var connectorIndex1 = request.Value<int?>("connector_index_1");
            var connectorIndex2 = request.Value<int?>("connector_index_2");

            ElementId eid1, eid2;
            try
            {
                eid1 = RevitCompat.ToElementId(elementId1.Value);
                eid2 = RevitCompat.ToElementId(elementId2.Value);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return CommandResult.Fail(ex.Message);
            }

            var element1 = doc.GetElement(eid1);
            if (element1 == null)
                return CommandResult.Fail($"Element {elementId1.Value} not found in model.");

            var element2 = doc.GetElement(eid2);
            if (element2 == null)
                return CommandResult.Fail($"Element {elementId2.Value} not found in model.");

            if (RevitCompat.GetId(element1.Id) == RevitCompat.GetId(element2.Id))
                return CommandResult.Fail("element_id_1 and element_id_2 must be different elements.");

            ConnectorManager cm1 = GetConnectorManager(element1);
            if (cm1 == null)
                return CommandResult.Fail($"Element {elementId1.Value} has no MEP connectors.");

            ConnectorManager cm2 = GetConnectorManager(element2);
            if (cm2 == null)
                return CommandResult.Fail($"Element {elementId2.Value} has no MEP connectors.");

            // Resolve the connector pair to connect.
            Connector connector1;
            Connector connector2;

            if (connectorIndex1.HasValue || connectorIndex2.HasValue)
            {
                if (!connectorIndex1.HasValue || !connectorIndex2.HasValue)
                    return CommandResult.Fail(
                        "When specifying connector ids, both connector_index_1 and connector_index_2 must be supplied.");

                connector1 = FindConnectorByIndex(cm1, connectorIndex1.Value);
                if (connector1 == null)
                    return CommandResult.Fail(
                        $"Element {elementId1.Value} has no connector with id {connectorIndex1.Value}.");

                connector2 = FindConnectorByIndex(cm2, connectorIndex2.Value);
                if (connector2 == null)
                    return CommandResult.Fail(
                        $"Element {elementId2.Value} has no connector with id {connectorIndex2.Value}.");
            }
            else
            {
                // Pick the closest pair of unused connectors with matching domains.
                if (!FindConnectedPair(cm1, cm2, out connector1, out connector2) &&
                    !FindNearestOpenPair(cm1, cm2, out connector1, out connector2))
                {
                    return CommandResult.Ok(new
                    {
                        connected = false,
                        element_id_1 = elementId1.Value,
                        element_id_2 = elementId2.Value,
                        connector_1_origin_mm = (object)null,
                        connector_2_origin_mm = (object)null,
                        gap_mm = (object)null,
                        error = "No compatible open connectors found (domain mismatch or all in use)."
                    });
                }
            }

            XYZ origin1 = SafeOrigin(connector1);
            XYZ origin2 = SafeOrigin(connector2);
            object originMm1 = ToMm(origin1);
            object originMm2 = ToMm(origin2);
            object gapMm = (origin1 != null && origin2 != null)
                ? (object)Math.Round(origin1.DistanceTo(origin2) * FeetToMm, 1)
                : null;

            MEPSystemType systemType1, systemType2;
            bool alreadyConnected;
            try
            {
                if (!IsPhysical(connector1) || !IsPhysical(connector2) || connector1.Domain != connector2.Domain)
                    return CommandResult.Fail("Selected connectors must be physical and have matching domains.");
                systemType1 = MepConnectionSystemType.Read(connector1);
                systemType2 = MepConnectionSystemType.Read(connector2);
                if (MepConnectionSystemType.Mismatch(systemType1, systemType2))
                    return CommandResult.Ok(new
                    {
                        connected = false,
                        element_id_1 = elementId1.Value, element_id_2 = elementId2.Value,
                        system_type_id_1 = RevitCompat.GetId(systemType1.Id), system_type_1 = systemType1.Name,
                        system_type_id_2 = RevitCompat.GetId(systemType2.Id), system_type_2 = systemType2.Name,
                        reason = "system_type_mismatch",
                        error = "System types differ; no connection was made. Recreate or explicitly reassign the incorrect element."
                    });
                alreadyConnected = connector1.IsConnectedTo(connector2);
                if (!alreadyConnected && (connector1.IsConnected || connector2.IsConnected))
                    return CommandResult.Fail("A selected connector is already in use by another connection.");
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Cannot validate selected connectors: " + ex.Message);
            }

            if (!alreadyConnected)
            using (var tx = new Transaction(doc, "RvtMcp: connect MEP elements"))
            {
                tx.Start();
                try
                {
                    connector1.ConnectTo(connector2);
                    doc.Regenerate();
                    if (!connector1.IsConnectedTo(connector2))
                        throw new InvalidOperationException("Revit did not establish the requested connector connection.");
                    systemType1 = MepConnectionSystemType.Read(connector1);
                    systemType2 = MepConnectionSystemType.Read(connector2);
                    if (tx.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Revit did not commit the connection.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Ok(new
                    {
                        connected = false,
                        element_id_1 = elementId1.Value,
                        element_id_2 = elementId2.Value,
                        connector_1_origin_mm = originMm1,
                        connector_2_origin_mm = originMm2,
                        gap_mm = gapMm,
                        error = "Failed to connect connectors: " + ex.Message
                    });
                }
            }

            return CommandResult.Ok(new
            {
                connected = true,
                already_connected = alreadyConnected,
                element_id_1 = elementId1.Value,
                element_id_2 = elementId2.Value,
                connector_1_origin_mm = originMm1,
                connector_2_origin_mm = originMm2,
                gap_mm = gapMm,
                system_type_id_1 = systemType1 == null ? (long?)null : RevitCompat.GetId(systemType1.Id),
                system_type_1 = systemType1?.Name,
                system_type_id_2 = systemType2 == null ? (long?)null : RevitCompat.GetId(systemType2.Id),
                system_type_2 = systemType2?.Name,
                error = (string)null
            });
        }

        /// <summary>
        /// Returns the ConnectorManager for an MEP curve or family instance, or null.
        /// </summary>
        private static ConnectorManager GetConnectorManager(Element el)
        {
            try
            {
                if (el is MEPCurve curve)
                    return curve.ConnectorManager;

                if (el is FamilyInstance fi)
                    return fi.MEPModel?.ConnectorManager;
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Finds an end/curve connector on the manager whose Id matches the requested id.
        /// </summary>
        private static Connector FindConnectorByIndex(ConnectorManager cm, int index)
        {
            try
            {
                foreach (Connector c in cm.Connectors)
                {
                    if (c.Id == index)
                        return c;
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Picks the closest pair of unused connectors (one from each manager) whose domains match.
        /// </summary>
        private static bool FindNearestOpenPair(
            ConnectorManager cm1,
            ConnectorManager cm2,
            out Connector best1,
            out Connector best2)
        {
            best1 = null;
            best2 = null;
            double bestDistance = double.MaxValue;

            ConnectorSet set1, set2;
            try { set1 = cm1.Connectors; } catch { return false; }
            try { set2 = cm2.Connectors; } catch { return false; }

            foreach (Connector c1 in set1)
            {
                if (!IsConnectableOpen(c1)) continue;
                XYZ o1 = SafeOrigin(c1);
                if (o1 == null) continue;

                foreach (Connector c2 in set2)
                {
                    if (!IsConnectableOpen(c2)) continue;

                    Domain d1, d2;
                    try
                    {
                        d1 = c1.Domain;
                        d2 = c2.Domain;
                    }
                    catch { continue; }
                    if (d1 != d2) continue;

                    XYZ o2 = SafeOrigin(c2);
                    if (o2 == null) continue;

                    double distance = o1.DistanceTo(o2);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best1 = c1;
                        best2 = c2;
                    }
                }
            }

            return best1 != null && best2 != null;
        }

        /// <summary>
        /// True if the connector is a free physical connector (End/Curve) not already joined.
        /// </summary>
        private static bool IsConnectableOpen(Connector c)
        {
            try
            {
                if (!IsPhysical(c))
                    return false;
                return !c.IsConnected;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPhysical(Connector connector)
            => connector.ConnectorType == ConnectorType.End || connector.ConnectorType == ConnectorType.Curve;

        private static bool FindConnectedPair(ConnectorManager first, ConnectorManager second,
            out Connector connected1, out Connector connected2)
        {
            connected1 = null;
            connected2 = null;
            foreach (Connector a in first.Connectors)
                foreach (Connector b in second.Connectors)
                    if (IsPhysical(a) && IsPhysical(b) && a.Domain == b.Domain && a.IsConnectedTo(b))
                    {
                        connected1 = a;
                        connected2 = b;
                        return true;
                    }
            return false;
        }

        /// <summary>
        /// Returns the connector origin, or null when the connector exposes no coordinate system
        /// (some logical connectors throw on Origin access).
        /// </summary>
        private static XYZ SafeOrigin(Connector c)
        {
            try { return c.Origin; }
            catch { return null; }
        }

        private static object ToMm(XYZ pt)
        {
            if (pt == null) return null;
            return new
            {
                x = Math.Round(pt.X * FeetToMm, 1),
                y = Math.Round(pt.Y * FeetToMm, 1),
                z = Math.Round(pt.Z * FeetToMm, 1)
            };
        }
    }
}
