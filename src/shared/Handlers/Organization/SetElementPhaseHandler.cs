using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// set_element_phase — sets the Phase Created and/or Phase Demolished of one or more elements.
    /// Phases are resolved by ElementId or by name. Passing demolished id -1 (or name "None")
    /// clears the demolition phase.
    /// </summary>
    public class SetElementPhaseHandler : IRevitCommand
    {
        public string Name => "set_element_phase";

        public string Description =>
            "Set the Phase Created and/or Phase Demolished of one or more elements. " +
            "Resolve a phase by phase_created_id/phase_demolished_id or by phase_created_name/phase_demolished_name. " +
            "Use phase_demolished_id = -1 or phase_demolished_name = 'None' to clear the demolition phase. " +
            "At least one of phase created / phase demolished must be supplied.";

        public string ParametersSchema => @"{
  ""type"":""object"",
  ""required"":[""element_ids""],
  ""properties"":{
    ""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
    ""phase_created_id"":{""type"":""integer"",""description"":""Phase ElementId for 'Phase Created'. Optional.""},
    ""phase_created_name"":{""type"":""string"",""description"":""Phase name (used if phase_created_id omitted). Optional.""},
    ""phase_demolished_id"":{""type"":""integer"",""description"":""Phase ElementId for 'Phase Demolished'. Use -1 / phase_demolished_name='None' to clear demolition. Optional.""},
    ""phase_demolished_name"":{""type"":""string"",""description"":""Phase name, or 'None' to clear. Optional.""}
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
            catch (JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            if (!TryReadElementIds(request, out var elementIds, out var idError))
                return CommandResult.Fail(idError);

            if (elementIds.Count == 0)
                return CommandResult.Fail("element_ids array is required and must not be empty.");

            // Resolve the requested phases (created / demolished) before opening the transaction.
            ElementId phaseCreatedId = null;
            string phaseCreatedName = null;
            var hasPhaseCreated = HasValue(request, "phase_created_id") || HasValue(request, "phase_created_name");
            if (hasPhaseCreated)
            {
                if (!TryResolvePhase(doc, request, "phase_created_id", "phase_created_name",
                        allowClear: false, out phaseCreatedId, out phaseCreatedName, out var createdError))
                    return CommandResult.Fail(createdError);
            }

            // For "demolished" a clear request (-1 / "None") is valid and yields InvalidElementId.
            ElementId phaseDemolishedId = null;
            string phaseDemolishedName = null;
            var hasPhaseDemolished = HasValue(request, "phase_demolished_id") || HasValue(request, "phase_demolished_name");
            if (hasPhaseDemolished)
            {
                if (!TryResolvePhase(doc, request, "phase_demolished_id", "phase_demolished_name",
                        allowClear: true, out phaseDemolishedId, out phaseDemolishedName, out var demolishedError))
                    return CommandResult.Fail(demolishedError);
            }

            if (!hasPhaseCreated && !hasPhaseDemolished)
                return CommandResult.Fail(
                    "At least one of phase_created_id/phase_created_name or phase_demolished_id/phase_demolished_name must be supplied.");

            var succeeded = 0;
            var failed = new List<object>();

            using (var tx = new Transaction(doc, "RvtMcp: set element phase"))
            {
                try
                {
                    tx.Start();

                    foreach (var elementId in elementIds)
                    {
                        using (var sub = new SubTransaction(doc))
                        {
                            var subStarted = false;
                            try
                            {
                                sub.Start();
                                subStarted = true;

                                var ok = TrySetElementPhase(doc, elementId, hasPhaseCreated, phaseCreatedId,
                                    hasPhaseDemolished, phaseDemolishedId, out var failure);

                                if (ok)
                                {
                                    var subStatus = sub.Commit();
                                    subStarted = false;
                                    if (subStatus == TransactionStatus.Committed)
                                    {
                                        succeeded++;
                                    }
                                    else
                                    {
                                        failed.Add(MakeFailure(elementId,
                                            "Revit did not commit phase updates for this element. Subtransaction status: " + subStatus));
                                    }
                                }
                                else
                                {
                                    sub.RollBack();
                                    subStarted = false;
                                    failed.Add(failure);
                                }
                            }
                            catch (Exception ex)
                            {
                                if (subStarted)
                                {
                                    try { sub.RollBack(); }
                                    catch { }
                                }

                                failed.Add(MakeFailure(elementId, ex.Message));
                            }
                        }
                    }

                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail("Revit did not commit phase updates. Transaction status: " + status);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted())
                        tx.RollBack();

                    return CommandResult.Fail("Failed to set element phase: " + ex.Message);
                }
            }

            return CommandResult.Ok(new
            {
                updated = true,
                element_count = elementIds.Count,
                succeeded,
                failed = failed.ToArray(),
                phase_created = hasPhaseCreated ? phaseCreatedName : null,
                phase_demolished = hasPhaseDemolished ? phaseDemolishedName : null,
                error = (string)null
            });
        }

        private static bool TrySetElementPhase(
            Document doc,
            long elementId,
            bool hasPhaseCreated,
            ElementId phaseCreatedId,
            bool hasPhaseDemolished,
            ElementId phaseDemolishedId,
            out object failure)
        {
            failure = null;

            Element element;
            try
            {
                element = doc.GetElement(RevitCompat.ToElementId(elementId));
            }
            catch (Exception ex)
            {
                failure = MakeFailure(elementId, ex.Message);
                return false;
            }

            if (element == null)
            {
                failure = MakeFailure(elementId, "Element not found.");
                return false;
            }

            if (hasPhaseCreated)
            {
                if (!TrySetPhaseParameter(element, BuiltInParameter.PHASE_CREATED, phaseCreatedId,
                        "Phase Created", out var createdError))
                {
                    failure = MakeFailure(elementId, createdError);
                    return false;
                }
            }

            if (hasPhaseDemolished)
            {
                if (!TrySetPhaseParameter(element, BuiltInParameter.PHASE_DEMOLISHED, phaseDemolishedId,
                        "Phase Demolished", out var demolishedError))
                {
                    failure = MakeFailure(elementId, demolishedError);
                    return false;
                }
            }

            return true;
        }

        private static bool TrySetPhaseParameter(
            Element element,
            BuiltInParameter bip,
            ElementId phaseId,
            string label,
            out string error)
        {
            error = null;

            Parameter parameter;
            try
            {
                parameter = element.get_Parameter(bip);
            }
            catch (Exception ex)
            {
                error = label + " parameter could not be read: " + ex.Message;
                return false;
            }

            if (parameter == null)
            {
                error = label + " parameter is not present on this element.";
                return false;
            }

            if (SafeIsReadOnly(parameter))
            {
                error = label + " parameter is read-only on this element.";
                return false;
            }

            try
            {
                if (!parameter.Set(phaseId))
                {
                    error = "Revit rejected the " + label + " value.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = "Failed to set " + label + ": " + ex.Message;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Resolves a phase from an id field or a name field. When allowClear is true an id of -1
        /// or a name of "None" resolves to ElementId.InvalidElementId (clears the demolition phase).
        /// </summary>
        private static bool TryResolvePhase(
            Document doc,
            JObject request,
            string idKey,
            string nameKey,
            bool allowClear,
            out ElementId phaseId,
            out string phaseName,
            out string error)
        {
            phaseId = null;
            phaseName = null;
            error = null;

            var idToken = request[idKey];
            if (idToken != null && idToken.Type == JTokenType.Integer)
            {
                var rawId = idToken.Value<long>();

                if (allowClear && rawId == -1)
                {
                    phaseId = ElementId.InvalidElementId;
                    phaseName = "None";
                    return true;
                }

                if (!RevitCompat.CanRepresentElementId(rawId))
                {
                    error = idKey + " " + RevitCompat.ElementIdRangeError(rawId);
                    return false;
                }

                var phase = ResolvePhaseById(doc, rawId);
                if (phase == null)
                {
                    error = "No phase found with " + idKey + " = " + rawId.ToString(CultureInfo.InvariantCulture) + ".";
                    return false;
                }

                phaseId = phase.Id;
                phaseName = phase.Name;
                return true;
            }

            var nameToken = request[nameKey];
            if (nameToken != null && nameToken.Type == JTokenType.String)
            {
                var requestedName = nameToken.Value<string>();
                if (string.IsNullOrWhiteSpace(requestedName))
                {
                    error = nameKey + " must not be blank.";
                    return false;
                }

                requestedName = requestedName.Trim();

                if (allowClear && string.Equals(requestedName, "None", StringComparison.OrdinalIgnoreCase))
                {
                    phaseId = ElementId.InvalidElementId;
                    phaseName = "None";
                    return true;
                }

                var phase = ResolvePhaseByName(doc, requestedName);
                if (phase == null)
                {
                    error = "No phase named '" + requestedName + "' was found. Available phases: "
                        + DescribeAvailablePhases(doc) + ".";
                    return false;
                }

                phaseId = phase.Id;
                phaseName = phase.Name;
                return true;
            }

            error = "Provide either " + idKey + " (integer) or " + nameKey + " (string).";
            return false;
        }

        private static Phase ResolvePhaseById(Document doc, long id)
        {
            try
            {
                var phaseArray = doc.Phases;
                if (phaseArray == null)
                    return null;

                foreach (Phase phase in phaseArray)
                {
                    if (phase != null && RevitCompat.GetId(phase.Id) == id)
                        return phase;
                }
            }
            catch
            {
                // Fall through to null.
            }

            return null;
        }

        private static Phase ResolvePhaseByName(Document doc, string name)
        {
            try
            {
                var phaseArray = doc.Phases;
                if (phaseArray == null)
                    return null;

                foreach (Phase phase in phaseArray)
                {
                    if (phase != null && string.Equals(phase.Name, name, StringComparison.OrdinalIgnoreCase))
                        return phase;
                }
            }
            catch
            {
                // Fall through to null.
            }

            return null;
        }

        private static string DescribeAvailablePhases(Document doc)
        {
            try
            {
                var names = new List<string>();
                var phaseArray = doc.Phases;
                if (phaseArray != null)
                {
                    foreach (Phase phase in phaseArray)
                    {
                        if (phase != null && !string.IsNullOrEmpty(phase.Name))
                            names.Add("'" + phase.Name + "'");
                    }
                }

                return names.Count == 0 ? "(none)" : string.Join(", ", names);
            }
            catch
            {
                return "(unavailable)";
            }
        }

        private static bool TryReadElementIds(JObject request, out List<long> elementIds, out string error)
        {
            elementIds = new List<long>();
            error = null;

            var token = request["element_ids"];
            if (token == null || token.Type != JTokenType.Array)
            {
                error = "element_ids must be an array of integers.";
                return false;
            }

            var array = (JArray)token;
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i].Type != JTokenType.Integer)
                {
                    error = "element_ids[" + i.ToString(CultureInfo.InvariantCulture) + "] must be an integer.";
                    return false;
                }

                var elementId = array[i].Value<long>();
                if (!RevitCompat.CanRepresentElementId(elementId))
                {
                    error = "element_ids[" + i.ToString(CultureInfo.InvariantCulture) + "] "
                        + RevitCompat.ElementIdRangeError(elementId);
                    return false;
                }

                elementIds.Add(elementId);
            }

            return true;
        }

        private static bool HasValue(JObject request, string key)
        {
            var token = request[key];
            return token != null && token.Type != JTokenType.Null;
        }

        private static bool SafeIsReadOnly(Parameter parameter)
        {
            try
            {
                return parameter?.IsReadOnly ?? false;
            }
            catch
            {
                return false;
            }
        }

        private static object MakeFailure(long elementId, string error)
        {
            return new
            {
                element_id = elementId,
                error
            };
        }
    }
}
