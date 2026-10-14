using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// set_system_classification — Add MEP elements to an existing duct/piping system,
    /// or (when system_id is omitted) report each element's current system membership
    /// read-only via connector inspection.
    /// </summary>
    public class SetSystemClassificationHandler : IRevitCommand
    {
        public string Name => "set_system_classification";

        public string Description =>
            "Add MEP elements to an existing mechanical (duct) or piping system, or report current " +
            "system membership. Provide element_ids (MEP element ElementIds). Supply system_id to add " +
            "those elements to the target MechanicalSystem or PipingSystem inside a transaction; the " +
            "element must have a free connector of the matching domain. Omit system_id for a read-only " +
            "report of each element's current system membership (no transaction).";

        public string ParametersSchema => @"{""type"":""object"",""required"":[""element_ids""],""properties"":{""element_ids"":{""type"":""array"",""items"":{""type"":""integer""},""description"":""MEP element ElementIds to add to the system.""},""system_id"":{""type"":""integer"",""description"":""Target MechanicalSystem or PipingSystem ElementId. If omitted, the handler only reports current membership (read-only).""}}}";

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

            if (!TryReadIdArray(request, "element_ids", out var elementIds, out var idError))
                return CommandResult.Fail(idError);
            if (elementIds.Count == 0)
                return CommandResult.Fail("element_ids array is required and must not be empty.");

            var systemIdToken = request["system_id"];
            bool hasSystemId = systemIdToken != null && systemIdToken.Type != JTokenType.Null;

            if (!hasSystemId)
                return ReportMembership(doc, elementIds);

            if (systemIdToken.Type != JTokenType.Integer)
                return CommandResult.Fail("system_id must be an integer.");

            long systemId = systemIdToken.Value<long>();
            if (!RevitCompat.CanRepresentElementId(systemId))
                return CommandResult.Fail("system_id " + RevitCompat.ElementIdRangeError(systemId));

            return AddToSystem(doc, elementIds, systemId);
        }

        /// <summary>
        /// Read-only branch: inspect each element's connectors and report the system(s)
        /// it currently belongs to. No transaction.
        /// </summary>
        private static CommandResult ReportMembership(Document doc, List<long> elementIds)
        {
            var alreadyMember = new List<string>();
            var failed = new List<object>();

            foreach (var id in elementIds)
            {
                string idText = id.ToString(CultureInfo.InvariantCulture);
                try
                {
                    var element = doc.GetElement(RevitCompat.ToElementId(id));
                    if (element == null)
                    {
                        failed.Add(new { element_id = idText, error = "Element not found." });
                        continue;
                    }

                    var cm = GetConnectorManager(element);
                    if (cm == null)
                    {
                        failed.Add(new { element_id = idText, error = "Element has no MEP connectors." });
                        continue;
                    }

                    var systemNames = new List<string>();
                    var systemIds = new List<string>();
                    var seen = new HashSet<long>();
                    try
                    {
                        foreach (Connector connector in cm.Connectors)
                        {
                            MEPSystem sys = null;
                            try { sys = connector.MEPSystem; }
                            catch { }
                            if (sys == null) continue;

                            long sysId = RevitCompat.GetId(sys.Id);
                            if (!seen.Add(sysId)) continue;
                            systemIds.Add(sysId.ToString(CultureInfo.InvariantCulture));
                            systemNames.Add(sys.Name ?? "");
                        }
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new { element_id = idText, error = "Failed to read connectors: " + ex.Message });
                        continue;
                    }

                    if (seen.Count == 0)
                    {
                        failed.Add(new { element_id = idText, error = "Element is not assigned to any MEP system." });
                        continue;
                    }

                    // Element already belongs to a system — report it as a current member.
                    alreadyMember.Add(idText + " -> " + string.Join(", ", systemIds.Select((s, i) => s + " (" + systemNames[i] + ")")));
                }
                catch (Exception ex)
                {
                    failed.Add(new { element_id = idText, error = ex.Message });
                }
            }

            return CommandResult.Ok(new
            {
                system_id = (string)null,
                system_name = (string)null,
                added = new List<string>(),
                already_member = alreadyMember,
                failed = failed,
                dry_report = true,
                error = (string)null
            });
        }

        /// <summary>
        /// Write branch: resolve the target MechanicalSystem / PipingSystem and add the
        /// elements to it inside a transaction.
        /// </summary>
        private static CommandResult AddToSystem(Document doc, List<long> elementIds, long systemId)
        {
            ElementId systemElementId;
            try
            {
                systemElementId = RevitCompat.ToElementId(systemId);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return CommandResult.Fail(ex.Message);
            }

            var systemElement = doc.GetElement(systemElementId);
            if (systemElement == null)
                return CommandResult.Fail($"No element found with id {systemId}.");

            var mechSystem = systemElement as MechanicalSystem;
            var pipeSystem = systemElement as PipingSystem;
            if (mechSystem == null && pipeSystem == null)
                return CommandResult.Fail(
                    $"Element {systemId} is not a MechanicalSystem or PipingSystem (found {systemElement.GetType().Name}).");

            string systemName = systemElement.Name ?? "";
            string systemIdText = systemId.ToString(CultureInfo.InvariantCulture);
            Domain targetDomain = mechSystem != null ? Domain.DomainHvac : Domain.DomainPiping;

            // Pre-compute existing members so we can classify already-member elements.
            var existingMembers = new HashSet<long>();
            try
            {
                ElementSet members = mechSystem != null ? mechSystem.Elements : pipeSystem.Elements;
                if (members != null)
                {
                    foreach (Element member in members)
                    {
                        if (member == null) continue;
                        existingMembers.Add(RevitCompat.GetId(member.Id));
                    }
                }
            }
            catch { }

            var added = new List<string>();
            var alreadyMember = new List<string>();
            var failed = new List<object>();

            using (var tx = new Transaction(doc, "RvtMcp: set system classification"))
            {
                tx.Start();

                foreach (var id in elementIds)
                {
                    string idText = id.ToString(CultureInfo.InvariantCulture);
                    try
                    {
                        var element = doc.GetElement(RevitCompat.ToElementId(id));
                        if (element == null)
                        {
                            failed.Add(new { element_id = idText, error = "Element not found." });
                            continue;
                        }

                        if (existingMembers.Contains(id))
                        {
                            alreadyMember.Add(idText);
                            continue;
                        }

                        // MEP systems are built from connectors, not elements:
                        // MechanicalSystem/PipingSystem.Add takes a ConnectorSet.
                        ConnectorManager connMgr = null;
                        try
                        {
                            if (element is MEPCurve)
                                connMgr = ((MEPCurve)element).ConnectorManager;
                            else if (element is FamilyInstance)
                            {
                                var mepModel = ((FamilyInstance)element).MEPModel;
                                if (mepModel != null) connMgr = mepModel.ConnectorManager;
                            }
                        }
                        catch { }

                        if (connMgr == null)
                        {
                            failed.Add(new { element_id = idText, error = "Element has no MEP connectors; cannot add it to a system." });
                            continue;
                        }

                        if (!HasEndConnectorInDomain(connMgr, targetDomain))
                        {
                            failed.Add(new { element_id = idText, error = "Element has no free End connector in the target system domain (" + targetDomain + "); not added." });
                            continue;
                        }

                        try
                        {
                            if (mechSystem != null)
                                mechSystem.Add(connMgr.Connectors);
                            else
                                pipeSystem.Add(connMgr.Connectors);

                            added.Add(idText);
                            existingMembers.Add(id);
                        }
                        catch (Exception ex)
                        {
                            failed.Add(new
                            {
                                element_id = idText,
                                error = "Revit rejected the add (element may lack a free connector of the matching domain): " + ex.Message
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new { element_id = idText, error = ex.Message });
                    }
                }

                var status = tx.Commit();
                if (status != TransactionStatus.Committed)
                    return CommandResult.Fail("Revit did not commit the system classification. Transaction status: " + status);
            }

            return CommandResult.Ok(new
            {
                system_id = systemIdText,
                system_name = systemName,
                added = added,
                already_member = alreadyMember,
                failed = failed,
                dry_report = false,
                error = (string)null
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

        private static bool HasEndConnectorInDomain(ConnectorManager connMgr, Domain targetDomain)
        {
            ConnectorSet connectors = null;
            try { connectors = connMgr.Connectors; } catch { connectors = null; }
            if (connectors == null) return false;

            foreach (Connector connector in connectors)
            {
                try
                {
                    if (connector.ConnectorType == ConnectorType.End && connector.Domain == targetDomain)
                        return true;
                }
                catch { }
            }

            return false;
        }

        private static bool TryReadIdArray(JObject request, string propertyName, out List<long> ids, out string error)
        {
            ids = new List<long>();
            error = null;

            var token = request[propertyName];
            if (token == null || token.Type == JTokenType.Null)
            {
                error = propertyName + " array is required.";
                return false;
            }

            if (token.Type != JTokenType.Array)
            {
                error = propertyName + " must be an array of integers.";
                return false;
            }

            var array = (JArray)token;
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i].Type != JTokenType.Integer)
                {
                    error = propertyName + "[" + i.ToString(CultureInfo.InvariantCulture) + "] must be an integer.";
                    return false;
                }

                var id = array[i].Value<long>();
                if (!RevitCompat.CanRepresentElementId(id))
                {
                    error = propertyName + "[" + i.ToString(CultureInfo.InvariantCulture) + "] " + RevitCompat.ElementIdRangeError(id);
                    return false;
                }

                ids.Add(id);
            }

            return true;
        }
    }
}
