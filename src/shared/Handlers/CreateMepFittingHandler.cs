using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Inserts an MEP fitting (elbow / tee / union / cross / transition) at the
    /// connectors of existing MEP elements via the Document.Create factory methods.
    /// </summary>
    public class CreateMepFittingHandler : IRevitCommand
    {
        public string Name => "create_mep_fitting";

        public string Description =>
            "Insert an MEP fitting at the connectors of existing MEP elements (pipes, ducts, fittings, accessories). " +
            "fitting_kind is one of elbow, tee, union, cross, transition. " +
            "connectors is an array of {element_id, connector_index} objects identifying the connectors to join: " +
            "elbow/union/transition require exactly 2, tee requires 3, cross requires 4. " +
            "connector_index is the connector_id from get_mep_element_connectors, not the ordinal. " +
            "Returns the created fitting's id and category.";

        public string ParametersSchema => @"{""type"":""object"",""required"":[""fitting_kind"",""connectors""],""properties"":{""fitting_kind"":{""type"":""string"",""enum"":[""elbow"",""tee"",""union"",""cross"",""transition""]},""connectors"":{""type"":""array"",""description"":""Array of {element_id, connector_index} objects. connector_index is the connector_id from get_mep_element_connectors. elbow/union/transition need 2, tee needs 3, cross needs 4."",""items"":{""type"":""object"",""properties"":{""element_id"":{""type"":""integer""},""connector_index"":{""type"":""integer"",""description"":""Connector id: the connector_id field from get_mep_element_connectors, NOT the ordinal.""}}}}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
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

            var fittingKind = request.Value<string>("fitting_kind")?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(fittingKind))
                return CommandResult.Fail("fitting_kind is required.");

            int requiredCount;
            switch (fittingKind)
            {
                case "elbow":
                case "union":
                case "transition":
                    requiredCount = 2;
                    break;
                case "tee":
                    requiredCount = 3;
                    break;
                case "cross":
                    requiredCount = 4;
                    break;
                default:
                    return CommandResult.Fail(
                        "Unknown fitting_kind '" + fittingKind +
                        "'. Supported: elbow, tee, union, cross, transition.");
            }

            var connectorsToken = request["connectors"] as JArray;
            if (connectorsToken == null || connectorsToken.Count == 0)
                return CommandResult.Fail("connectors is required and must be a non-empty array.");

            if (connectorsToken.Count != requiredCount)
                return Error(fittingKind,
                    "Fitting kind '" + fittingKind + "' requires exactly " + requiredCount +
                    " connector(s) but " + connectorsToken.Count + " were provided.");

            // Resolve each {element_id, connector_index} to a Connector.
            var connectors = new Connector[requiredCount];
            for (int i = 0; i < requiredCount; i++)
            {
                var entry = connectorsToken[i] as JObject;
                if (entry == null)
                    return Error(fittingKind,
                        "connectors[" + i + "] must be an object with element_id and connector_index.");

                var elementId = entry.Value<long?>("element_id");
                if (elementId == null)
                    return Error(fittingKind, "connectors[" + i + "].element_id is required.");

                var connectorIndex = entry.Value<int?>("connector_index");
                if (connectorIndex == null)
                    return Error(fittingKind, "connectors[" + i + "].connector_index is required.");

                if (!RevitCompat.CanRepresentElementId(elementId.Value))
                    return Error(fittingKind, RevitCompat.ElementIdRangeError(elementId.Value));

                var element = doc.GetElement(RevitCompat.ToElementId(elementId.Value));
                if (element == null)
                    return Error(fittingKind,
                        "Element " + elementId.Value + " not found in model.");

                var cm = GetConnectorManager(element);
                if (cm == null)
                    return Error(fittingKind,
                        "Element " + elementId.Value + " has no MEP connectors.");

                var connector = GetConnectorByIndex(cm, connectorIndex.Value);
                if (connector == null)
                    return Error(fittingKind,
                        "No connector with id " + connectorIndex.Value + " on element " +
                        elementId.Value + ".");

                connectors[i] = connector;
            }

            using (var tx = new Transaction(doc, "RvtMcp: create MEP fitting"))
            {
                tx.Start();
                try
                {
                    FamilyInstance fitting;
                    switch (fittingKind)
                    {
                        case "elbow":
                            fitting = doc.Create.NewElbowFitting(connectors[0], connectors[1]);
                            break;
                        case "transition":
                            fitting = doc.Create.NewTransitionFitting(connectors[0], connectors[1]);
                            break;
                        case "union":
                            fitting = doc.Create.NewUnionFitting(connectors[0], connectors[1]);
                            break;
                        case "tee":
                            fitting = doc.Create.NewTeeFitting(connectors[0], connectors[1], connectors[2]);
                            break;
                        case "cross":
                            fitting = doc.Create.NewCrossFitting(
                                connectors[0], connectors[1], connectors[2], connectors[3]);
                            break;
                        default:
                            tx.RollBack();
                            return Error(fittingKind, "Unsupported fitting_kind '" + fittingKind + "'.");
                    }

                    if (fitting == null)
                    {
                        tx.RollBack();
                        return Error(fittingKind,
                            "Revit returned no fitting for the supplied connectors.");
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit the transaction; model changes were not confirmed.");

                    return CommandResult.Ok(new
                    {
                        created = true,
                        fitting_id = RevitCompat.GetId(fitting.Id),
                        fitting_kind = fittingKind,
                        fitting_category = fitting.Category?.Name,
                        error = (string)null
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return Error(fittingKind,
                        "Failed to create " + fittingKind + " fitting: " + ex.Message);
                }
            }
        }

        /// <summary>Returns a successful CommandResult carrying an error DTO (created=false).</summary>
        private static CommandResult Error(string fittingKind, string message)
        {
            return CommandResult.Ok(new
            {
                created = false,
                fitting_id = (long?)null,
                fitting_kind = fittingKind,
                fitting_category = (string)null,
                error = message
            });
        }

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

        private static Connector GetConnectorByIndex(ConnectorManager cm, int index)
        {
            if (cm == null || index < 0) return null;

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
    }
}
