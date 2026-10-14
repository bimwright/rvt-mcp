using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Inspects all connectors on an MEP element (duct/pipe/fitting/equipment/terminal):
    /// domain, shape, position, connection status, and flow. Read-only — no transaction.
    /// </summary>
    public class GetMepElementConnectorsHandler : IRevitCommand
    {
        private const double FeetToMm = 304.8;

        public string Name => "get_mep_element_connectors";

        public string Description =>
            "Inspect all connectors on an MEP element (duct, pipe, cable tray, conduit, fitting, " +
            "equipment, or air terminal). Returns each connector's domain, shape, origin in mm, " +
            "connection status, connected element IDs, size (radius or width/height in mm), " +
            "flow, flow direction, stable connector_id, and ordinal. Pass connector_id (not ordinal) " +
            "to connect_mep_elements / create_mep_fitting.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""element_id""],
  ""properties"": {
    ""element_id"": {""type"":""integer"",""description"":""ElementId of an MEP element. Pass connector_id (not ordinal) to connect_mep_elements / create_mep_fitting.""}
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            JObject request;
            try
            {
                request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            }
            catch (Newtonsoft.Json.JsonException ex)
            {
                return CommandResult.Fail("Invalid JSON parameters: " + ex.Message);
            }

            var elementId = request.Value<long?>("element_id");
            if (elementId == null)
                return CommandResult.Fail("element_id is required.");

            if (!RevitCompat.CanRepresentElementId(elementId.Value))
                return CommandResult.Fail(RevitCompat.ElementIdRangeError(elementId.Value));

            Element element;
            try
            {
                element = doc.GetElement(RevitCompat.ToElementId(elementId.Value));
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Failed to resolve element " + elementId.Value + ": " + ex.Message);
            }

            if (element == null)
                return CommandResult.Fail("Element " + elementId.Value + " not found in model.");

            long ownerId = RevitCompat.GetId(element.Id);
            string elementCategory = null;
            try { elementCategory = element.Category?.Name; } catch { }

            ConnectorManager connectorManager = GetConnectorManager(element);
            var connectors = new List<object>();

            if (connectorManager != null)
            {
                ConnectorSet connectorSet = null;
                try { connectorSet = connectorManager.Connectors; } catch { connectorSet = null; }

                if (connectorSet != null)
                {
                    int ordinal = 0;
                    foreach (Connector connector in connectorSet)
                    {
                        try
                        {
                            connectors.Add(BuildConnectorDto(connector, ordinal, ownerId));
                        }
                        catch
                        {
                            int? connectorId = null;
                            try { connectorId = connector.Id; } catch { }

                            // One bad connector must not abort the whole read.
                            connectors.Add(new
                            {
                                connector_id = connectorId,
                                ordinal = ordinal,
                                domain = "Undefined",
                                shape = "Undefined",
                                origin_mm = (object)null,
                                is_connected = false,
                                connected_to_element_ids = new string[0],
                                radius_mm = (double?)null,
                                width_mm = (double?)null,
                                height_mm = (double?)null,
                                flow = (double?)null,
                                direction = "Undefined"
                            });
                        }
                        ordinal++;
                    }
                }
            }

            return CommandResult.Ok(new
            {
                element_id = ownerId.ToString(),
                element_category = elementCategory,
                connector_count = connectors.Count,
                connectors = connectors
            });
        }

        private static object BuildConnectorDto(Connector connector, int ordinal, long ownerId)
        {
            int? connectorId = null;
            try { connectorId = connector.Id; } catch { }

            string domain = "Undefined";
            try { domain = connector.Domain.ToString().Replace("Domain", ""); } catch { }

            string shape = "Undefined";
            bool shapeKnown = false;
            ConnectorProfileType shapeType = ConnectorProfileType.Invalid;
            try
            {
                shapeType = connector.Shape;
                shape = shapeType.ToString();
                shapeKnown = true;
            }
            catch { }

            object originMm = null;
            try
            {
                var origin = connector.Origin;
                if (origin != null)
                {
                    originMm = new[]
                    {
                        Math.Round(origin.X * FeetToMm, 1),
                        Math.Round(origin.Y * FeetToMm, 1),
                        Math.Round(origin.Z * FeetToMm, 1)
                    };
                }
            }
            catch { }

            bool isConnected = false;
            try { isConnected = connector.IsConnected; } catch { }

            var connectedToIds = new List<string>();
            try
            {
                ConnectorSet allRefs = connector.AllRefs;
                if (allRefs != null)
                {
                    foreach (Connector refConn in allRefs)
                    {
                        Element refOwner = null;
                        try { refOwner = refConn.Owner; } catch { }
                        if (refOwner == null) continue;

                        long refId = RevitCompat.GetId(refOwner.Id);
                        if (refId <= 0) continue;          // skip logical / pseudo-connector refs
                        if (refId == ownerId) continue;    // skip self

                        string refIdStr = refId.ToString();
                        if (!connectedToIds.Contains(refIdStr))
                            connectedToIds.Add(refIdStr);
                    }
                }
            }
            catch { }

            // Size is shape-dependent: Radius valid for round, Width/Height for rectangular/oval.
            double? radiusMm = null;
            double? widthMm = null;
            double? heightMm = null;

            if (!shapeKnown || shapeType == ConnectorProfileType.Round)
            {
                try { radiusMm = Math.Round(connector.Radius * FeetToMm, 1); }
                catch { radiusMm = null; }
            }

            if (!shapeKnown || shapeType == ConnectorProfileType.Rectangular || shapeType == ConnectorProfileType.Oval)
            {
                try { widthMm = Math.Round(connector.Width * FeetToMm, 1); }
                catch { widthMm = null; }
                try { heightMm = Math.Round(connector.Height * FeetToMm, 1); }
                catch { heightMm = null; }
            }

            // Flow may throw on electrical / non-flow connectors.
            double? flow = null;
            try { flow = Math.Round(connector.Flow, 4); }
            catch { flow = null; }

            string direction = "Undefined";
            try { direction = connector.Direction.ToString(); } catch { }

            return new
            {
                connector_id = connectorId,
                ordinal = ordinal,
                domain = domain,
                shape = shape,
                origin_mm = originMm,
                is_connected = isConnected,
                connected_to_element_ids = connectedToIds,
                radius_mm = radiusMm,
                width_mm = widthMm,
                height_mm = heightMm,
                flow = flow,
                direction = direction
            };
        }

        private static ConnectorManager GetConnectorManager(Element element)
        {
            try
            {
                if (element is MEPCurve curve)
                    return curve.ConnectorManager;

                if (element is FamilyInstance fi)
                    return fi.MEPModel?.ConnectorManager;
            }
            catch { }

            return null;
        }
    }
}
